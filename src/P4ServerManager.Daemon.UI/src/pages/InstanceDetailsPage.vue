<template>
  <v-container max-width="1200">
    <template v-if="instance">
      <div class="d-flex flex-wrap align-center ga-4 mb-2">
        <h1 class="text-headline-small">{{ instance.name }}</h1>
        <InstanceStatusChip :instance="instance" />
        <v-spacer />

        <template v-if="status === 'running' || status === 'restarting' || status === 'stopping'">
          <v-btn
            :disabled="!canAct"
            :loading="status === 'restarting'"
            prepend-icon="mdi-restart"
            :text="t('actions.restart')"
            variant="tonal"
            @click="ask('restart')"
          />

          <v-btn
            :disabled="!canAct"
            :loading="status === 'stopping'"
            prepend-icon="mdi-stop"
            :text="t('actions.stop')"
            variant="tonal"
            @click="ask('stop')"
          />
        </template>

        <v-btn
          v-else
          color="primary"
          :disabled="!canAct"
          :loading="status === 'starting'"
          prepend-icon="mdi-play"
          :text="t('actions.start')"
          variant="flat"
          @click="run(instances.start)"
        />

        <v-btn
          color="error"
          :disabled="!canDelete"
          :loading="status === 'deleting'"
          prepend-icon="mdi-delete"
          :text="t('actions.delete')"
          variant="text"
          @click="deleteDialog = true"
        />
      </div>

      <DeleteInstanceDialog v-model="deleteDialog" :instance="instance" @deleted="onDeleted" />

      <ConfirmInstanceActionDialog
        v-model="confirmDialog"
        :action="confirmAction"
        :names="[instance.name]"
        @confirm="run(confirmAction === 'stop' ? instances.stop : instances.restart)"
      />

      <p v-if="instance.description" class="text-body-medium text-medium-emphasis mb-4">
        {{ instance.description }}
      </p>

      <v-alert
        v-if="actionError"
        class="mb-4"
        closable
        density="compact"
        type="error"
        variant="tonal"
        @click:close="actionError = null"
      >
        {{ actionError }}
      </v-alert>

      <v-row>
        <v-col v-if="showOperation" cols="12" md="6">
          <OperationProgress :instance="instance" :snapshot="snapshot" />
        </v-col>

        <v-col cols="12" :md="showOperation ? 6 : 12">
          <v-card :title="t('instance.details')">
            <v-list density="compact">
              <v-list-item v-for="detail in details" :key="detail.label" :subtitle="detail.value" :title="t(detail.label)" />

              <v-list-item :subtitle="t('instance.autoStartHint')" :title="t('instance.autoStart')">
                <template #append>
                  <v-switch
                    :aria-label="t('instance.autoStart')"
                    color="primary"
                    density="compact"
                    :disabled="savingAutoStart || status === 'deleting'"
                    hide-details
                    :loading="savingAutoStart"
                    :model-value="instance.autoStart"
                    name="autoStart"
                    @update:model-value="setAutoStart"
                  />
                </template>
              </v-list-item>
            </v-list>
          </v-card>

          <v-alert
            v-if="instance.lastError && !showOperation"
            class="mt-4"
            density="compact"
            :title="t('instance.lastError')"
            type="warning"
            variant="tonal"
          >
            {{ instance.lastError }}
          </v-alert>
        </v-col>
      </v-row>
    </template>

    <v-empty-state
      v-else-if="instances.loaded"
      icon="mdi-server-off"
      :title="t('instance.notFound')"
    />
  </v-container>
</template>

<script lang="ts" setup>
  // Composables
  import { computed, ref, watch } from 'vue'
  import { useI18n } from 'vue-i18n'
  import { useRouter } from 'vue-router'

  // Components, stores and utilities
  import ConfirmInstanceActionDialog from '@/components/instances/ConfirmInstanceActionDialog.vue'
  import DeleteInstanceDialog from '@/components/instances/DeleteInstanceDialog.vue'
  import InstanceStatusChip from '@/components/instances/InstanceStatusChip.vue'
  import OperationProgress from '@/components/instances/OperationProgress.vue'
  import { useInstancesStore } from '@/stores/instances'
  import { formatPort } from '@/utils/instancePort'
  import { getInstanceStatus, isInstanceStatusInProgress } from '@/utils/instanceStatus'

  const props = defineProps<{
    id: string
  }>()

  const { t, d } = useI18n()
  const router = useRouter()
  const instances = useInstancesStore()

  // True from the click to the answer of the daemon. What is in progress comes from the daemon: it is shared with the
  // other clients and survives a reload of the page.
  const sending = ref(false)
  const actionError = ref<string | null>(null)
  const deleteDialog = ref(false)
  const confirmDialog = ref(false)
  const confirmAction = ref<'stop' | 'restart'>('stop')
  const savingAutoStart = ref(false)

  const instance = computed(() => instances.getInstance(props.id))
  const snapshot = computed(() => instances.operations[props.id] ?? null)
  const status = computed(() => instance.value ? getInstanceStatus(instance.value) : null)
  const canAct = computed(() =>
    instance.value?.state === 'Ready' && !sending.value && !!status.value && !isInstanceStatusInProgress(status.value),
  )
  // A failed instance can be deleted too, but not one being provisioned.
  const canDelete = computed(() =>
    instance.value?.state !== 'Provisioning' && !sending.value && !!status.value
    && !isInstanceStatusInProgress(status.value),
  )
  const showOperation = computed(() =>
    instance.value?.state !== 'Ready' || snapshot.value?.status === 'Running',
  )

  const details = computed(() => {
    const value = instance.value
    if (!value) {
      return []
    }
    return [
      {
        label: 'instance.version',
        value: value.version ? `${value.version}/${value.build} (${value.release})` : t('instance.pendingVersion'),
      },
      { label: 'instance.port', value: formatPort(value) },
      ...(value.sslFingerprint ? [{ label: 'instance.sslFingerprint', value: value.sslFingerprint }] : []),
      { label: 'instance.root', value: value.rootPath },
      {
        label: 'instance.caseHandling',
        value: value.caseSensitive ? t('instance.caseSensitive') : t('instance.caseInsensitive'),
      },
      { label: 'instance.unicode', value: value.unicode ? t('instance.enabled') : t('instance.disabled') },
      { label: 'instance.createdAt', value: d(new Date(value.createdAt), 'long') },
    ]
  })

  // The snapshot is kept by the daemon in memory: it is read once, then the hub keeps it up to date.
  watch(() => props.id, id => {
    actionError.value = null
    void instances.fetchOperation(id).catch(() => {})
  }, { immediate: true })

  function onDeleted () {
    void router.push({ name: 'instances' })
  }

  async function setAutoStart (value: boolean | null) {
    savingAutoStart.value = true
    actionError.value = null
    try {
      await instances.update(props.id, { autoStart: !!value })
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error)
      actionError.value = t('instance.autoStartError', { message })
    } finally {
      savingAutoStart.value = false
    }
  }

  // A stop or a restart disconnects the users of the server: it is confirmed. A start is not.
  function ask (action: 'stop' | 'restart') {
    confirmAction.value = action
    confirmDialog.value = true
  }

  async function run (action: (id: string) => Promise<void>) {
    sending.value = true
    actionError.value = null
    try {
      await action(props.id)
    } catch (error) {
      actionError.value = error instanceof Error ? error.message : String(error)
    } finally {
      sending.value = false
    }
  }
</script>

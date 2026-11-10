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
          :disabled="!canManage"
          prepend-icon="mdi-pencil"
          :text="t('actions.edit')"
          variant="text"
          @click="editDialog = true"
        />

        <v-btn
          :disabled="!canAct"
          :loading="status === 'upgrading'"
          prepend-icon="mdi-update"
          :text="t('actions.upgrade')"
          variant="text"
          @click="upgradeDialog = true"
        />

        <v-btn
          color="error"
          :disabled="!canManage"
          :loading="status === 'deleting'"
          prepend-icon="mdi-delete"
          :text="t('actions.delete')"
          variant="text"
          @click="deleteDialog = true"
        />
      </div>

      <EditInstanceDialog v-model="editDialog" :instance="instance" />

      <DeleteInstanceDialog v-model="deleteDialog" :instance="instance" />

      <UpgradeInstanceDialog v-model="upgradeDialog" :instance="instance" />

      <ConvertToUnicodeDialog v-model="convertDialog" :instance="instance" />

      <ChangeServerIdDialog v-model="serverIdDialog" :instance="instance" />

      <SuperUserDialog v-model="superUserDialog" :instance="instance" />

      <ConfirmInstanceActionDialog
        v-model="confirmDialog"
        :action="confirmAction"
        :names="[instance.name]"
        @confirm="run(confirmedActions[confirmAction])"
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
              <v-list-item v-for="detail in details" :key="detail.label" :subtitle="detail.value" :title="t(detail.label)">
                <template v-if="detail.label === 'instance.unicode' && !instance.unicode" #append>
                  <v-btn
                    :disabled="!canAct"
                    :loading="status === 'convertingToUnicode'"
                    size="small"
                    :text="t('actions.convertToUnicode')"
                    variant="tonal"
                    @click="convertDialog = true"
                  />
                </template>

                <template v-else-if="detail.label === 'instance.serverId'" #append>
                  <!-- A disabled button gets no pointer events: the tooltip hangs on the span around it. -->
                  <v-tooltip :disabled="!superUserProblem" location="start" :text="t('changeServerId.superUserRequired')">
                    <template #activator="{ props: tooltip }">
                      <span v-bind="tooltip" data-change-server-id>
                        <v-btn
                          :disabled="!canAct || !!superUserProblem"
                          :loading="status === 'changingServerId'"
                          size="small"
                          :text="t('actions.changeServerId')"
                          variant="tonal"
                          @click="serverIdDialog = true"
                        />
                      </span>
                    </template>
                  </v-tooltip>
                </template>
              </v-list-item>

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

              <v-list-item
                :subtitle="instance.maintenanceMode ? t('instance.maintenanceModeOn') : t('instance.maintenanceModeOff')"
                :title="t('instance.maintenanceMode')"
              >
                <template #append>
                  <v-btn
                    :disabled="!canAct"
                    :prepend-icon="instance.maintenanceMode ? 'mdi-wrench-check' : 'mdi-wrench'"
                    size="small"
                    :text="t(`actions.${maintenanceAction}`)"
                    variant="tonal"
                    @click="switchMaintenance"
                  />
                </template>
              </v-list-item>

              <v-list-item :subtitle="superUserSummary" :title="t('superUser.title')">
                <template #append>
                  <div class="d-flex ga-2">
                    <v-btn
                      v-if="instance.superUser"
                      :disabled="!canAct || status !== 'running'"
                      :loading="checkingSuperUser"
                      name="checkSuperUser"
                      size="small"
                      :text="t('actions.checkNow')"
                      variant="text"
                      @click="checkSuperUser"
                    />

                    <v-btn
                      :disabled="!canAct"
                      name="changeSuperUser"
                      prepend-icon="mdi-account-key"
                      size="small"
                      :text="instance.superUser ? t('superUser.change') : t('superUser.set')"
                      variant="tonal"
                      @click="superUserDialog = true"
                    />
                  </div>
                </template>
              </v-list-item>
            </v-list>
          </v-card>

          <v-alert
            v-if="superUserProblem && instance.state === 'Ready'"
            class="mt-4"
            density="compact"
            name="superUserWarning"
            :title="t('superUser.warning.title')"
            type="warning"
            variant="tonal"
          >
            <div>{{ t(`superUser.warning.${superUserProblem}`) }}</div>
            <div v-if="instance.superUser?.error" class="text-body-small mt-1">{{ instance.superUser.error }}</div>
          </v-alert>

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
  import ChangeServerIdDialog from '@/components/instances/ChangeServerIdDialog.vue'
  import ConfirmInstanceActionDialog from '@/components/instances/ConfirmInstanceActionDialog.vue'
  import ConvertToUnicodeDialog from '@/components/instances/ConvertToUnicodeDialog.vue'
  import DeleteInstanceDialog from '@/components/instances/DeleteInstanceDialog.vue'
  import EditInstanceDialog from '@/components/instances/EditInstanceDialog.vue'
  import InstanceStatusChip from '@/components/instances/InstanceStatusChip.vue'
  import OperationProgress from '@/components/instances/OperationProgress.vue'
  import SuperUserDialog from '@/components/instances/SuperUserDialog.vue'
  import UpgradeInstanceDialog from '@/components/instances/UpgradeInstanceDialog.vue'
  import { useInstancesStore } from '@/stores/instances'
  import { formatPort } from '@/utils/instancePort'
  import { getInstanceStatus, isInstanceStatusInProgress } from '@/utils/instanceStatus'
  import { getSuperUserProblem } from '@/utils/superUser'

  const props = defineProps<{
    id: string
  }>()

  // The actions that restart or stop a running server, and so are confirmed first.
  type ConfirmableAction = 'stop' | 'restart' | 'enableMaintenance' | 'disableMaintenance'

  const { t, d } = useI18n()
  const router = useRouter()
  const instances = useInstancesStore()

  // True from the click to the answer of the daemon. What is in progress comes from the daemon: it is shared with the
  // other clients and survives a reload of the page.
  const sending = ref(false)
  const actionError = ref<string | null>(null)
  const editDialog = ref(false)
  const deleteDialog = ref(false)
  const upgradeDialog = ref(false)
  const convertDialog = ref(false)
  const serverIdDialog = ref(false)
  const confirmDialog = ref(false)
  const confirmAction = ref<ConfirmableAction>('stop')
  const savingAutoStart = ref(false)
  const superUserDialog = ref(false)
  const checkingSuperUser = ref(false)

  const instance = computed(() => instances.getInstance(props.id))
  const snapshot = computed(() => instances.operations[props.id] ?? null)
  const status = computed(() => instance.value ? getInstanceStatus(instance.value) : null)
  const canAct = computed(() =>
    instance.value?.state === 'Ready' && !sending.value && !!status.value && !isInstanceStatusInProgress(status.value),
  )
  // A failed instance can be edited and deleted too, but not one being provisioned.
  const canManage = computed(() =>
    instance.value?.state !== 'Provisioning' && !sending.value && !!status.value
    && !isInstanceStatusInProgress(status.value),
  )
  // What the maintenance mode button does: disable it, or enable it with a restart or a start.
  const maintenanceAction = computed(() => {
    if (instance.value?.maintenanceMode) {
      return 'disableMaintenance'
    }
    return status.value === 'stopped' ? 'startInMaintenance' : 'enableMaintenance'
  })
  const confirmedActions: Record<ConfirmableAction, (id: string) => Promise<void>> = {
    stop: instances.stop,
    restart: instances.restart,
    enableMaintenance: instances.enableMaintenance,
    disableMaintenance: instances.disableMaintenance,
  }
  const showOperation = computed(() =>
    instance.value?.state !== 'Ready' || snapshot.value?.status === 'Running',
  )

  const superUserProblem = computed(() => instance.value ? getSuperUserProblem(instance.value) : null)
  const superUserSummary = computed(() => {
    const superUser = instance.value?.superUser
    if (!superUser) {
      return t('superUser.none')
    }
    const statusText = t(`superUser.status.${superUser.status}`)
    const summary = superUser.checkedAt
      ? t('superUser.checkedAt', { status: statusText, date: d(new Date(superUser.checkedAt), 'long') })
      : statusText
    return `${superUser.userName} · ${summary}`
  })

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
      { label: 'instance.serverId', value: value.serverId ?? t('instance.unknownServerId') },
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

  // Once deleted, by this page or by another client, the instance leaves the store, which unmounts the delete dialog
  // before it could tell this page: the page goes back to the list as soon as its instance is gone.
  watch(instance, (value, previous) => {
    if (!value && previous?.id === props.id) {
      void router.replace({ name: 'instances' })
    }
  })

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

  async function checkSuperUser () {
    checkingSuperUser.value = true
    actionError.value = null
    try {
      await instances.checkSuperUser(props.id)
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error)
      actionError.value = t('superUser.checkError', { message })
    } finally {
      checkingSuperUser.value = false
    }
  }

  // A stop or a restart disconnects the users of the server: it is confirmed. A start is not.
  function ask (action: ConfirmableAction) {
    confirmAction.value = action
    confirmDialog.value = true
  }

  // When the maintenance mode is enabled or disabled, a running server restarts: it is confirmed. A stopped one is started in
  // maintenance mode, or left stopped, without a confirmation.
  function switchMaintenance () {
    const action = instance.value?.maintenanceMode ? 'disableMaintenance' : 'enableMaintenance'
    if (status.value === 'running') {
      ask(action)
    } else {
      void run(confirmedActions[action])
    }
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

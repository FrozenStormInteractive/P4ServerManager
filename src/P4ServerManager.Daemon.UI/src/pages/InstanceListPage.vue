<template>
  <v-container max-width="1200">
    <div class="d-flex align-center mb-6">
      <h1 class="text-headline-small">{{ t('instances.title') }}</h1>
      <v-spacer />

      <v-btn
        color="primary"
        prepend-icon="mdi-plus"
        :text="t('actions.newServer')"
        variant="flat"
        @click="app.openCreateDialog()"
      />
    </div>

    <v-alert
      v-if="instances.error"
      class="mb-4"
      density="compact"
      type="error"
      variant="tonal"
    >
      {{ t('instances.loadError', { message: instances.error }) }}
    </v-alert>

    <v-alert
      v-if="actionError"
      class="mb-4"
      closable
      density="compact"
      :title="actionError.title"
      type="error"
      variant="tonal"
      @click:close="actionError = null"
    >
      <div v-for="line in actionError.lines" :key="line">{{ line }}</div>
    </v-alert>

    <LegacyProfilesCard />

    <v-card>
      <div class="d-flex flex-wrap align-center ga-2 pa-2">
        <template v-if="selectedInstances.length > 0">
          <v-btn
            :aria-label="t('instances.clearSelection')"
            icon="mdi-close"
            size="small"
            variant="text"
            @click="selected = []"
          />

          <span class="text-body-medium mr-2">{{ t('instances.selected', { count: selectedInstances.length }) }}</span>

          <v-btn
            v-for="action in actions"
            :key="action.name"
            :disabled="sending !== null || selectedFor(action.name).length === 0"
            :loading="sending === action.name"
            :prepend-icon="action.icon"
            :text="t(`actions.${action.name}`)"
            variant="tonal"
            @click="runOnSelection(action.name)"
          />
        </template>

        <span v-else class="text-body-medium text-medium-emphasis px-2">{{ t('instances.selectionHint') }}</span>

        <v-spacer />

        <v-menu>
          <template #activator="{ props: menuProps }">
            <v-btn
              v-bind="menuProps"
              append-icon="mdi-menu-down"
              :disabled="sending !== null"
              :loading="sending === 'all'"
              :text="t('instances.allServers')"
              variant="text"
            />
          </template>

          <v-list density="compact">
            <v-list-item
              v-for="action in actions"
              :key="action.name"
              :disabled="!canRunOnAll(action.name)"
              :prepend-icon="action.icon"
              :title="t(`instances.all.${action.name}`)"
              @click="askRunOnAll(action.name)"
            />
          </v-list>
        </v-menu>
      </div>

      <v-divider />

      <v-data-table
        v-model="selected"
        :headers="headers"
        hover
        item-value="id"
        :items="instances.sortedInstances"
        :loading="instances.loading && !instances.loaded"
        :no-data-text="t('instances.noData')"
        show-select
        @click:row="openInstance"
      >
        <template #item.status="{ item }">
          <InstanceStatusChip :instance="item" />
        </template>

        <template #item.version="{ item }">
          {{ item.version ? `${item.version}/${item.build}` : item.release }}
        </template>
      </v-data-table>
    </v-card>

    <ConfirmInstanceActionDialog
      v-if="pending"
      v-model="confirmDialog"
      :action="pending.action"
      :names="pending.names"
      @confirm="confirmPending"
    />
  </v-container>
</template>

<script lang="ts" setup>
  // Types
  import type { Instance, InstanceAction, InstanceSelection } from '@/api/types'

  // Composables
  import { computed, ref } from 'vue'
  import { useI18n } from 'vue-i18n'
  import { useRouter } from 'vue-router'

  // Components and stores
  import ConfirmInstanceActionDialog from '@/components/instances/ConfirmInstanceActionDialog.vue'
  import InstanceStatusChip from '@/components/instances/InstanceStatusChip.vue'
  import LegacyProfilesCard from '@/components/instances/LegacyProfilesCard.vue'
  import { useAppStore } from '@/stores/app'
  import { useInstancesStore } from '@/stores/instances'
  import { formatPort } from '@/utils/instancePort'
  import { getInstanceStatus } from '@/utils/instanceStatus'

  const { t } = useI18n()
  const router = useRouter()
  const app = useAppStore()
  const instances = useInstancesStore()

  const actions: { name: InstanceAction, icon: string }[] = [
    { name: 'start', icon: 'mdi-play' },
    { name: 'stop', icon: 'mdi-stop' },
    { name: 'restart', icon: 'mdi-restart' },
  ]

  const selected = ref<string[]>([])
  // The action waiting for the answer of the daemon, on the selection or on all the servers.
  const sending = ref<InstanceAction | 'all' | null>(null)
  const actionError = ref<{ title: string, lines: string[] } | null>(null)
  const confirmDialog = ref(false)
  // The stop or the restart waiting for a confirmation, with the servers it applies to when they are listed.
  const pending = ref<{
    action: 'stop' | 'restart'
    source: InstanceAction | 'all'
    selection: InstanceSelection
    names: string[] | null
  } | null>(null)

  const headers = computed(() => [
    { title: t('instances.headers.name'), key: 'name' },
    { title: t('instances.headers.status'), key: 'status', sortable: false },
    { title: t('instances.headers.version'), key: 'version' },
    { title: t('instances.headers.port'), key: 'port', value: (item: Instance) => formatPort(item) },
    { title: t('instances.headers.root'), key: 'rootPath' },
  ])

  // A deleted instance leaves the selection.
  const selectedInstances = computed(() =>
    selected.value.map(id => instances.getInstance(id)).filter((instance): instance is Instance => !!instance),
  )

  /**
   * Whether the action applies to the instance: it is not sent for the others, so that the daemon only reports real
   * failures. As on the page of an instance, only a running server is restarted: a stopped one is started.
   */
  function appliesTo (action: InstanceAction, instance: Instance): boolean {
    return getInstanceStatus(instance) === (action === 'start' ? 'stopped' : 'running')
  }

  function selectedFor (action: InstanceAction): Instance[] {
    return selectedInstances.value.filter(instance => appliesTo(action, instance))
  }

  function canRunOnAll (action: InstanceAction): boolean {
    return instances.sortedInstances.some(instance => appliesTo(action, instance))
  }

  function openInstance (_event: Event, { item }: { item: Instance }) {
    void router.push({ name: 'instance', params: { id: item.id } })
  }

  function runOnSelection (action: InstanceAction) {
    const targets = selectedFor(action)
    const selection = { instanceIds: targets.map(instance => instance.id) }
    if (action === 'start') {
      void run(action, action, selection)
      return
    }
    ask({ action, source: action, selection, names: targets.map(instance => instance.name) })
  }

  function askRunOnAll (action: InstanceAction) {
    if (action === 'start') {
      void run(action, 'all', { all: true })
      return
    }
    ask({ action, source: 'all', selection: { all: true }, names: null })
  }

  // A stop or a restart disconnects the users of the servers: it is confirmed. A start is not.
  function ask (request: NonNullable<typeof pending.value>) {
    pending.value = request
    confirmDialog.value = true
  }

  function confirmPending () {
    if (pending.value) {
      void run(pending.value.action, pending.value.source, pending.value.selection)
    }
  }

  async function run (action: InstanceAction, source: InstanceAction | 'all', selection: InstanceSelection) {
    sending.value = source
    actionError.value = null
    try {
      const failures = (await instances.runAction(action, selection)).filter(result => result.outcome === 'Failed')
      if (failures.length > 0) {
        actionError.value = {
          title: t(`instances.actionFailed.${action}`),
          lines: failures.map(result => `${result.instance.name}: ${result.error}`),
        }
      }
    } catch (error) {
      actionError.value = {
        title: t(`instances.actionFailed.${action}`),
        lines: [error instanceof Error ? error.message : String(error)],
      }
    } finally {
      sending.value = null
    }
  }
</script>

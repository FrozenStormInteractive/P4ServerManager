<template>
  <v-container max-width="1200">
    <template v-if="instance">
      <div class="d-flex flex-wrap align-center ga-4 mb-6">
        <h1 class="text-headline-small">{{ t('backups.title') }}</h1>
        <v-spacer />

        <v-btn
          v-if="running"
          :loading="canceling"
          prepend-icon="mdi-stop"
          :text="t('backups.cancel')"
          variant="tonal"
          @click="cancel"
        />

        <v-menu v-else>
          <template #activator="{ props: activator }">
            <v-btn
              v-bind="activator"
              append-icon="mdi-menu-down"
              color="primary"
              :disabled="!canStart"
              :loading="starting"
              prepend-icon="mdi-cloud-upload"
              :text="t('backups.start')"
              variant="flat"
            />
          </template>

          <v-list>
            <v-list-item
              :subtitle="t('backups.fullHint')"
              :title="t('backups.full')"
              @click="start('Full')"
            />

            <v-list-item
              :subtitle="t('backups.incrementalHint')"
              :title="t('backups.incremental')"
              @click="start('Incremental')"
            />
          </v-list>
        </v-menu>
      </div>

      <BackupRunDialog v-model="runDialog" :can-verify="canStart" :run="selectedRun" />

      <v-alert
        v-if="loadError"
        class="mb-4"
        density="compact"
        type="error"
        variant="tonal"
      >
        {{ t('backups.loadError', { message: loadError }) }}
      </v-alert>

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
          <v-card data-testid="backup-destinations" :title="t('backups.destinations.title')">
            <v-card-text>
              <p class="mb-4">{{ t('backups.destinations.text') }}</p>

              <template v-if="backups.destinations.length > 0">
                <v-select
                  v-model="selectedDestinations"
                  chips
                  closable-chips
                  :items="destinationItems"
                  :label="t('backups.destinations.label')"
                  multiple
                  name="destinations"
                />
              </template>

              <p v-else class="text-medium-emphasis">{{ t('backups.destinations.noneDefined') }}</p>

              <p v-if="backups.destinations.length > 0 && savedDestinations.length === 0" class="text-warning mb-0">
                {{ t('backups.destinations.none') }}
              </p>
            </v-card-text>

            <v-card-actions>
              <v-btn
                prepend-icon="mdi-cog-outline"
                :text="t('backups.destinations.manage')"
                :to="{ name: 'settings-backups' }"
                variant="text"
              />

              <v-spacer />

              <v-btn
                color="primary"
                :disabled="!destinationsChanged"
                :loading="savingDestinations"
                :text="t('actions.save')"
                variant="tonal"
                @click="saveDestinations"
              />
            </v-card-actions>
          </v-card>
        </v-col>
      </v-row>

      <v-card class="mt-6" :title="t('backups.history.title')">
        <v-data-table
          :headers="headers"
          hide-default-footer
          hover
          item-value="id"
          :items="runs"
          :items-per-page="-1"
          :loading="!loaded && !loadError"
          :no-data-text="t('backups.history.empty')"
          @click:row="(_: unknown, row: { item: BackupRun }) => openRun(row.item)"
        >
          <template #item.startedAt="{ item }">
            {{ d(new Date(item.startedAt), 'long') }}
          </template>

          <template #item.kind="{ item }">
            {{ t(`backups.kinds.${item.kind}`) }}
          </template>

          <template #item.status="{ item }">
            <v-chip
              :color="getBackupStatusLook(item.status).color"
              label
              :prepend-icon="getBackupStatusLook(item.status).icon"
              size="small"
              :text="t(`backups.statuses.${item.status}`)"
            />

            <v-tooltip v-if="item.warnings.length > 0" location="top" :text="item.warnings.join(' ')">
              <template #activator="{ props: tooltip }">
                <v-icon
                  v-bind="tooltip"
                  :aria-label="t('backups.run.warnings', { count: item.warnings.length })"
                  class="ml-1"
                  color="warning"
                  data-testid="run-warnings"
                  icon="mdi-alert-outline"
                  size="small"
                />
              </template>
            </v-tooltip>
          </template>

          <template #item.files="{ item }">
            {{ t('backups.run.filesValue', { count: item.fileCount, size: formatBytes(item.totalBytes, locale) }) }}
          </template>

          <template #item.destinations="{ item }">
            <div class="d-flex flex-wrap ga-1 py-1">
              <v-chip
                v-for="destination in item.destinations"
                :key="destination.destinationId"
                :color="getBackupDestinationStatusLook(destination.status).color"
                :prepend-icon="getBackupDestinationStatusLook(destination.status).icon"
                size="small"
                :text="destination.destinationName"
                variant="tonal"
              />
            </div>
          </template>

          <template #item.actions="{ item }">
            <div class="d-flex justify-end">
              <v-btn
                :aria-label="t('backups.history.details')"
                icon="mdi-file-document-outline"
                size="small"
                variant="text"
                @click.stop="openRun(item)"
              />
            </div>
          </template>
        </v-data-table>
      </v-card>
    </template>

    <v-empty-state
      v-else-if="instances.loaded"
      icon="mdi-server-off"
      :title="t('instance.notFound')"
    />
  </v-container>
</template>

<script lang="ts" setup>
  // Types
  import type { BackupKind, BackupRun } from '@/api/types'

  // Composables
  import { computed, onMounted, ref, watch } from 'vue'
  import { useI18n } from 'vue-i18n'

  // Components, stores and utilities
  import BackupRunDialog from '@/components/backups/BackupRunDialog.vue'
  import OperationProgress from '@/components/instances/OperationProgress.vue'
  import { useBackupsStore } from '@/stores/backups'
  import { useInstancesStore } from '@/stores/instances'
  import { useNotificationsStore } from '@/stores/notifications'
  import { getBackupDestinationStatusLook, getBackupStatusLook } from '@/utils/backupStatus'
  import { formatBytes } from '@/utils/bytes'
  import { getInstanceStatus, isInstanceStatusInProgress } from '@/utils/instanceStatus'

  /**
   * The backups of an instance: where they go, the one in progress, and the previous ones with what each did at each
   * destination. The backups are followed live through the hub the instances store listens to.
   */
  const props = defineProps<{
    id: string
  }>()

  const { t, d, locale } = useI18n()
  const instances = useInstancesStore()
  const backups = useBackupsStore()
  const notifications = useNotificationsStore()

  const runDialog = ref(false)
  const selectedId = ref<string | null>(null)
  const selectedDestinations = ref<string[]>([])
  const starting = ref(false)
  const canceling = ref(false)
  const savingDestinations = ref(false)
  const actionError = ref<string | null>(null)

  const instance = computed(() => instances.getInstance(props.id))
  const runs = computed(() => backups.getRuns(props.id))
  const running = computed(() => backups.getRunningRun(props.id))
  const selectedRun = computed(() => runs.value.find(run => run.id === selectedId.value) ?? null)
  const loaded = computed(() => backups.runsLoaded[props.id] ?? false)
  const loadError = computed(() => backups.runsErrors[props.id] ?? null)
  const savedDestinations = computed(() => backups.getInstanceDestinations(props.id))

  const snapshot = computed(() => instances.operations[props.id] ?? null)
  const showOperation = computed(() =>
    snapshot.value?.status === 'Running'
    && (snapshot.value.kind === 'backup' || snapshot.value.kind === 'verify-backup'),
  )

  const destinationItems = computed(() => backups.destinations.map(destination => ({
    value: destination.id,
    title: destination.name,
    subtitle: destination.location,
  })))

  const destinationsChanged = computed(() => {
    const saved = new Set(savedDestinations.value)
    return selectedDestinations.value.length !== saved.size
      || selectedDestinations.value.some(id => !saved.has(id))
  })

  const canStart = computed(() => {
    const value = instance.value
    return !!value
      && value.state === 'Ready'
      && !isInstanceStatusInProgress(getInstanceStatus(value))
      && savedDestinations.value.length > 0
  })

  const headers = computed(() => [
    { title: t('backups.history.started'), key: 'startedAt', sortable: false },
    { title: t('backups.history.kind'), key: 'kind', sortable: false },
    { title: t('backups.history.status'), key: 'status', sortable: false },
    { title: t('backups.history.files'), key: 'files', sortable: false },
    { title: t('backups.history.destinations'), key: 'destinations', sortable: false },
    { title: '', key: 'actions', sortable: false, align: 'end' as const },
  ])

  watch(savedDestinations, value => {
    selectedDestinations.value = [...value]
  }, { immediate: true })

  watch(() => props.id, id => void backups.fetchRuns(id), { immediate: true })

  onMounted(() => void backups.fetchDestinations())

  function openRun (run: BackupRun) {
    selectedId.value = run.id
    runDialog.value = true
  }

  async function start (kind: BackupKind) {
    starting.value = true
    actionError.value = null
    try {
      await backups.start(props.id, kind)
    } catch (error) {
      actionError.value = error instanceof Error ? error.message : String(error)
    } finally {
      starting.value = false
    }
  }

  async function cancel () {
    if (!running.value) {
      return
    }

    canceling.value = true
    actionError.value = null
    try {
      await backups.cancel(props.id, running.value.id)
    } catch (error) {
      actionError.value = error instanceof Error ? error.message : String(error)
    } finally {
      canceling.value = false
    }
  }

  async function saveDestinations () {
    savingDestinations.value = true
    actionError.value = null
    try {
      await backups.setInstanceDestinations(props.id, selectedDestinations.value)
      notifications.notify(t('backups.destinations.saved'))
    } catch (error) {
      actionError.value = error instanceof Error ? error.message : String(error)
    } finally {
      savingDestinations.value = false
    }
  }
</script>

<template>
  <v-container max-width="1200">
    <template v-if="instance">
      <div class="d-flex flex-wrap align-center ga-4 mb-6">
        <h1 class="text-headline-small">{{ t('health.title') }}</h1>
        <v-spacer />

        <v-btn
          v-if="running"
          :loading="canceling"
          prepend-icon="mdi-stop"
          :text="t('health.cancelRun')"
          variant="tonal"
          @click="cancel"
        />

        <v-btn
          v-else
          color="primary"
          :disabled="!canRun"
          prepend-icon="mdi-heart-pulse"
          :text="t('health.run')"
          variant="flat"
          @click="runDialog = true"
        />
      </div>

      <RunHealthCheckDialog v-model="runDialog" :instance="instance" />

      <HealthCheckReportDialog v-model="reportDialog" :run="selectedRun" />

      <DeleteHealthCheckDialog v-model="deleteDialog" :run="selectedRun" />

      <v-alert
        v-if="loadError"
        class="mb-4"
        density="compact"
        type="error"
        variant="tonal"
      >
        {{ t('health.loadError', { message: loadError }) }}
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

      <v-card class="mb-6" data-testid="health-state">
        <v-card-item>
          <template #prepend>
            <v-progress-circular
              v-if="running"
              class="mr-2"
              color="info"
              indeterminate
              size="32"
            />

            <v-icon
              v-else
              class="mr-2"
              :color="state.color"
              :icon="state.icon"
              size="32"
            />
          </template>

          <v-card-title>{{ t(`health.state.${state.key}`) }}</v-card-title>

          <v-card-subtitle v-if="stateText" class="text-wrap">{{ stateText }}</v-card-subtitle>
        </v-card-item>

        <v-card-text v-if="running" class="pt-0">
          <v-progress-linear
            class="mb-2"
            color="primary"
            height="8"
            :model-value="progress?.percent ?? 0"
            rounded
          />

          <div class="d-flex flex-wrap justify-space-between ga-2 text-body-small text-medium-emphasis">
            <span>{{ progress?.percent ?? 0 }}%</span>

            <span v-if="progress?.item" data-testid="health-item">
              {{ t(`health.checks.${progress.check}`) }}: {{ progress.item }}
              ({{ t('health.progress.reached', { count: progress.itemsChecked }) }})
            </span>
          </div>

          <p class="text-body-small text-medium-emphasis mt-2 mb-0">{{ t('health.progress.estimate') }}</p>
        </v-card-text>

        <template v-if="shownFindings.length > 0">
          <v-divider />

          <div class="text-title-small px-4 pt-4">
            {{ t(running ? 'health.findings.titleSoFar' : 'health.findings.title') }}
          </div>

          <HealthCheckFindings :findings="shownFindings" />

          <v-card-text v-if="!running" class="pt-0">
            <p class="text-body-small text-medium-emphasis mb-0">{{ t('health.stateText.advice') }}</p>
          </v-card-text>
        </template>

        <v-card-text v-else-if="running" class="pt-0 text-body-small text-medium-emphasis">
          {{ t('health.findings.none') }}
        </v-card-text>

        <v-card-actions v-if="!running && health.fixJournalRun">
          <v-btn
            download="jnl.fix"
            :href="getFixJournalUrl(health.fixJournalRun.instanceId, health.fixJournalRun.id)"
            prepend-icon="mdi-download"
            :text="t('health.fixJournal')"
            variant="text"
          />
        </v-card-actions>
      </v-card>

      <v-card :title="t('health.history.title')">
        <v-data-table
          :headers="headers"
          hide-default-footer
          hover
          item-value="id"
          :items="reports"
          :items-per-page="-1"
          :loading="!loaded && !loadError"
          :no-data-text="t('health.history.empty')"
        >
          <template #item.startedAt="{ item }">
            {{ d(new Date(item.startedAt), 'long') }}
          </template>

          <template #item.status="{ item }">
            <v-chip
              :color="getHealthCheckStatusLook(item.status).color"
              label
              :prepend-icon="getHealthCheckStatusLook(item.status).icon"
              size="small"
              :text="t(`health.statuses.${item.status}`)"
            />
          </template>

          <template #item.checks="{ item }">
            {{ item.checks.map(check => t(`health.checks.${check}`)).join(', ') }}
          </template>

          <template #item.duration="{ item }">
            {{ item.finishedAt ? formatDuration(item.startedAt, item.finishedAt) : '' }}
          </template>

          <template #item.problems="{ item }">
            {{ item.findings.length }}
          </template>

          <template #item.actions="{ item }">
            <div class="d-flex justify-end">
              <v-btn
                :aria-label="t('health.history.view')"
                icon="mdi-file-document-outline"
                size="small"
                variant="text"
                @click="openReport(item)"
              />

              <v-btn
                :aria-label="t('health.history.delete')"
                color="error"
                icon="mdi-delete-outline"
                size="small"
                variant="text"
                @click="askDelete(item)"
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
  import type { HealthCheckRun } from '@/api/types'

  // Composables
  import { computed, ref } from 'vue'
  import { useI18n } from 'vue-i18n'

  // API, components, stores and utilities
  import { getFixJournalUrl } from '@/api/healthChecks'
  import DeleteHealthCheckDialog from '@/components/instances/health/DeleteHealthCheckDialog.vue'
  import HealthCheckFindings from '@/components/instances/health/HealthCheckFindings.vue'
  import HealthCheckReportDialog from '@/components/instances/health/HealthCheckReportDialog.vue'
  import RunHealthCheckDialog from '@/components/instances/health/RunHealthCheckDialog.vue'
  import { useHealthChecksStore } from '@/stores/healthChecks'
  import { useInstancesStore } from '@/stores/instances'
  import { formatDuration, getHealthCheckStatusLook } from '@/utils/healthCheckStatus'
  import { getInstanceStatus, isInstanceStatusInProgress } from '@/utils/instanceStatus'

  /**
   * The state of the database of an instance, the check in progress, and the reports of the previous ones. The instance
   * page, its parent, loads the reports.
   */
  const props = defineProps<{
    id: string
  }>()

  const { t, d } = useI18n()
  const instances = useInstancesStore()
  const healthChecks = useHealthChecksStore()

  const runDialog = ref(false)
  const reportDialog = ref(false)
  const deleteDialog = ref(false)
  const selectedId = ref<string | null>(null)
  const canceling = ref(false)
  const actionError = ref<string | null>(null)

  const instance = computed(() => instances.getInstance(props.id))
  const running = computed(() => healthChecks.getRunningRun(props.id))
  const progress = computed(() => running.value?.progress ?? null)
  const health = computed(() => healthChecks.getHealth(props.id))
  const loaded = computed(() => healthChecks.loaded[props.id] ?? false)
  const loadError = computed(() => healthChecks.errors[props.id] ?? null)

  // The reports are the runs that ended and were not deleted: the card above tells the one in progress.
  const endedRuns = computed(() => healthChecks.getRuns(props.id).filter(run => run.status !== 'Running'))
  const reports = computed(() => endedRuns.value.filter(run => !run.reportHidden))
  const selectedRun = computed(() => reports.value.find(run => run.id === selectedId.value) ?? null)

  const canRun = computed(() => {
    const value = instance.value
    return !!value && value.state === 'Ready' && !isInstanceStatusInProgress(getInstanceStatus(value))
  })

  const state = computed(() => {
    if (running.value) {
      return { key: 'running', color: 'info', icon: 'mdi-progress-clock' }
    }
    switch (health.value.status) {
      case 'healthy': {
        return { key: 'healthy', ...getHealthCheckStatusLook('Healthy') }
      }
      case 'problems': {
        return { key: 'problems', ...getHealthCheckStatusLook('ProblemsFound') }
      }
      default: {
        return { key: 'unknown', color: 'grey', icon: 'mdi-help-circle-outline' }
      }
    }
  })

  const stateText = computed(() => {
    if (running.value) {
      return running.value.checks.map(check => t(`health.checks.${check}`)).join(', ')
    }
    const { latestRun: run, findings, uncheckedChecks } = health.value
    if (!run) {
      return t('health.stateText.unknown')
    }

    const date = d(new Date(run.finishedAt ?? run.startedAt), 'long')
    const count = findings.length
    const texts = [
      count === 0
        ? t('health.stateText.healthy', { date })
        : t('health.stateText.problems', { date, count }, count),
    ]
    if (uncheckedChecks.length > 0) {
      const checks = uncheckedChecks.map(check => t(`health.checks.${check}`)).join(', ')
      texts.push(t('health.stateText.partial', { checks }))
    }
    if (endedRuns.value[0]?.id !== run.id) {
      texts.push(t('health.stateText.newerIncomplete'))
    }
    return texts.join(' ')
  })

  const shownFindings = computed(() => running.value?.findings ?? health.value.findings)

  const headers = computed(() => [
    { title: t('health.history.started'), key: 'startedAt', sortable: false },
    { title: t('health.history.status'), key: 'status', sortable: false },
    { title: t('health.history.checks'), key: 'checks', sortable: false },
    { title: t('health.history.duration'), key: 'duration', sortable: false },
    { title: t('health.history.problems'), key: 'problems', sortable: false },
    { title: t('health.history.version'), key: 'p4dVersion', sortable: false },
    { title: '', key: 'actions', sortable: false, align: 'end' as const },
  ])

  function openReport (run: HealthCheckRun) {
    selectedId.value = run.id
    reportDialog.value = true
  }

  function askDelete (run: HealthCheckRun) {
    selectedId.value = run.id
    deleteDialog.value = true
  }

  async function cancel () {
    if (!running.value) {
      return
    }

    canceling.value = true
    actionError.value = null
    try {
      await healthChecks.cancel(props.id, running.value.id)
    } catch (error) {
      actionError.value = error instanceof Error ? error.message : String(error)
    } finally {
      canceling.value = false
    }
  }
</script>

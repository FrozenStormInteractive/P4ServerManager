<template>
  <v-dialog v-model="open" max-width="720" scrollable>
    <v-card v-if="run" :title="t('health.reportDialog.title', { date: d(new Date(run.startedAt), 'long') })">
      <template #append>
        <v-chip
          :color="look.color"
          label
          :prepend-icon="look.icon"
          size="small"
          :text="t(`health.statuses.${run.status}`)"
        />
      </template>

      <v-card-text class="pa-0">
        <v-list density="compact">
          <v-list-item :subtitle="run.p4dVersion" :title="t('health.reportDialog.version')" />

          <v-list-item :subtitle="checkNames" :title="t('health.reportDialog.checks')" />

          <v-list-item :subtitle="d(new Date(run.startedAt), 'long')" :title="t('health.reportDialog.started')" />

          <v-list-item
            v-if="run.finishedAt"
            :subtitle="d(new Date(run.finishedAt), 'long')"
            :title="t('health.reportDialog.finished')"
          />
        </v-list>

        <v-alert
          v-if="run.error"
          class="mx-4 mb-4"
          density="compact"
          :title="t('health.reportDialog.error')"
          type="warning"
          variant="tonal"
        >
          {{ run.error }}
        </v-alert>

        <template v-if="run.findings.length > 0">
          <v-divider />

          <div class="text-title-small px-4 pt-4">{{ t('health.findings.title') }}</div>

          <HealthCheckFindings :findings="run.findings" />

          <p class="text-body-small text-medium-emphasis px-4 pb-4">{{ t('health.stateText.advice') }}</p>
        </template>
      </v-card-text>

      <v-card-actions>
        <v-btn
          v-if="run.hasFixJournal"
          download="jnl.fix"
          :href="getFixJournalUrl(run.instanceId, run.id)"
          prepend-icon="mdi-download"
          :text="t('health.fixJournal')"
          variant="text"
        />

        <v-spacer />
        <v-btn :text="t('actions.close')" @click="open = false" />
      </v-card-actions>
    </v-card>
  </v-dialog>
</template>

<script lang="ts" setup>
  // Types
  import type { HealthCheckRun } from '@/api/types'

  // Composables
  import { computed } from 'vue'
  import { useI18n } from 'vue-i18n'

  // API, components and utilities
  import { getFixJournalUrl } from '@/api/healthChecks'
  import HealthCheckFindings from '@/components/instances/health/HealthCheckFindings.vue'
  import { getHealthCheckStatusLook } from '@/utils/healthCheckStatus'

  /**
   * The report of a health check that ended.
   */
  const props = defineProps<{
    run: HealthCheckRun | null
  }>()

  const open = defineModel<boolean>({ default: false })

  const { t, d } = useI18n()

  const look = computed(() => getHealthCheckStatusLook(props.run?.status ?? 'Failed'))
  const checkNames = computed(() => props.run?.checks.map(check => t(`health.checks.${check}`)).join(', ') ?? '')
</script>

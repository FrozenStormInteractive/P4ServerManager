<template>
  <v-list class="health-check-findings py-0" density="compact" lines="two">
    <v-list-item
      v-for="(finding, index) in findings"
      :key="index"
      :subtitle="finding.message"
      :title="finding.table ?? t('health.findings.general')"
    >
      <template #prepend>
        <v-icon color="error" icon="mdi-alert-circle-outline" />
      </template>

      <template #append>
        <v-chip class="ml-2" label size="x-small" :text="t(`health.checks.${finding.check}`)" />
      </template>
    </v-list-item>
  </v-list>
</template>

<script lang="ts" setup>
  // Types
  import type { HealthCheckFinding } from '@/api/types'

  // Composables
  import { useI18n } from 'vue-i18n'

  /**
   * The problems a health check found, as p4d told them.
   */
  defineProps<{
    findings: HealthCheckFinding[]
  }>()

  const { t } = useI18n()
</script>

<style scoped>
  /* The message of p4d is the report: it is never cut. */
  .health-check-findings :deep(.v-list-item-subtitle) {
    -webkit-line-clamp: unset;
    overflow-wrap: anywhere;
    white-space: normal;
  }
</style>

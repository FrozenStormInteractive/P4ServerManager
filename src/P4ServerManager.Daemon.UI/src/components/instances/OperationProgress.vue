<template>
  <v-card :title="title">
    <v-list v-if="snapshot" density="compact">
      <v-list-item
        v-for="(step, index) in snapshot.steps"
        :key="index"
        :class="{ 'text-disabled': step.status === 'Pending' || step.status === 'Skipped' }"
        :data-step="step.key"
        :subtitle="step.status === 'Skipped' ? t('operations.skipped') : step.message ?? undefined"
        :title="translate(`operations.steps.${step.key}`, step.title)"
      >
        <template #prepend>
          <div class="d-flex justify-center mr-4" style="width: 24px">
            <v-progress-circular
              v-if="step.status === 'Running'"
              color="primary"
              indeterminate
              size="20"
              width="2"
            />

            <v-icon v-else :color="icons[step.status].color" :icon="icons[step.status].icon" />
          </div>
        </template>

        <v-progress-linear
          v-if="step.status === 'Running' && snapshot.percent !== null"
          class="mt-2"
          color="primary"
          :model-value="snapshot.percent"
          rounded
        />
      </v-list-item>
    </v-list>

    <v-card-text v-if="error" class="pt-0">
      <v-alert density="compact" type="error" variant="tonal">{{ error }}</v-alert>
    </v-card-text>
  </v-card>
</template>

<script lang="ts" setup>
  // Types
  import type { Instance, OperationSnapshot, OperationStepStatus } from '@/api/types'

  // Composables
  import { computed } from 'vue'
  import { useI18n } from 'vue-i18n'

  const props = defineProps<{
    instance: Instance
    /**
     * Null when the daemon has run no operation on the instance since it started: only its last error is known, and
     * it comes from its provisioning.
     */
    snapshot: OperationSnapshot | null
  }>()

  const { t, te } = useI18n()

  const icons: Record<Exclude<OperationStepStatus, 'Running'>, { icon: string, color: string }> = {
    Pending: { icon: 'mdi-circle-outline', color: 'grey' },
    Succeeded: { icon: 'mdi-check-circle', color: 'success' },
    Failed: { icon: 'mdi-alert-circle', color: 'error' },
    Skipped: { icon: 'mdi-minus-circle-outline', color: 'grey' },
  }

  const title = computed(() => {
    const kind = props.snapshot?.kind ?? 'provision'
    return translate(`operations.kinds.${kind}`, kind)
  })

  const error = computed(() => props.snapshot?.error ?? (props.instance.state === 'Failed' ? props.instance.lastError : null))

  /**
   * The daemon may run steps this version of the UI does not know: they keep the title the daemon sends.
   */
  function translate (key: string, fallback: string): string {
    return te(key) ? t(key) : fallback
  }
</script>

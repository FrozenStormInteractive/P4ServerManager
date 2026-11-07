<template>
  <v-dialog v-model="open" max-width="480">
    <v-card v-if="run" :title="t('health.deleteDialog.title')">
      <v-card-text>
        <p class="text-body-medium">
          {{ t('health.deleteDialog.text', { date: d(new Date(run.startedAt), 'long') }) }}
        </p>

        <v-alert
          v-if="submitError"
          class="mt-4"
          density="compact"
          type="error"
          variant="tonal"
        >
          {{ t('health.deleteDialog.error', { message: submitError }) }}
        </v-alert>
      </v-card-text>

      <v-card-actions>
        <v-spacer />
        <v-btn :disabled="submitting" :text="t('actions.cancel')" @click="open = false" />

        <v-btn
          color="error"
          :loading="submitting"
          :text="t('actions.delete')"
          variant="flat"
          @click="submit"
        />
      </v-card-actions>
    </v-card>
  </v-dialog>
</template>

<script lang="ts" setup>
  // Types
  import type { HealthCheckRun } from '@/api/types'

  // Composables
  import { ref, watch } from 'vue'
  import { useI18n } from 'vue-i18n'

  // Stores
  import { useHealthChecksStore } from '@/stores/healthChecks'

  /**
   * Deletes the report of a health check, with its fix journal.
   */
  const props = defineProps<{
    run: HealthCheckRun | null
  }>()

  const open = defineModel<boolean>({ default: false })

  const { t, d } = useI18n()
  const healthChecks = useHealthChecksStore()

  const submitting = ref(false)
  const submitError = ref<string | null>(null)

  watch(open, value => {
    if (value) {
      submitError.value = null
    }
  })

  async function submit () {
    if (!props.run) {
      return
    }

    submitting.value = true
    submitError.value = null
    try {
      await healthChecks.remove(props.run.instanceId, props.run.id)
    } catch (error) {
      submitError.value = error instanceof Error ? error.message : String(error)
      return
    } finally {
      submitting.value = false
    }

    open.value = false
  }
</script>

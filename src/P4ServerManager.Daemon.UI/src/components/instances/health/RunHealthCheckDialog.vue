<template>
  <v-dialog v-model="open" max-width="560">
    <v-card :title="t('health.runDialog.title', { name: instance.name })">
      <v-card-text class="text-body-medium">
        <p class="mb-2">{{ t('health.runDialog.text') }}</p>

        <v-checkbox
          v-for="check in allChecks"
          :key="check"
          v-model="selected"
          color="primary"
          density="compact"
          :hint="t(`health.checkHints.${check}`)"
          :label="t(`health.checks.${check}`)"
          :name="check"
          persistent-hint
          :value="check"
        />

        <v-alert
          class="mt-4"
          density="compact"
          type="warning"
          variant="tonal"
        >
          {{ t(instance.runtimeStatus === 'Running' ? 'health.runDialog.warningRunning' : 'health.runDialog.warningStopped') }}
        </v-alert>

        <v-alert
          v-if="submitError"
          class="mt-4"
          density="compact"
          type="error"
          variant="tonal"
        >
          {{ t('health.runDialog.error', { message: submitError }) }}
        </v-alert>
      </v-card-text>

      <v-card-actions>
        <v-spacer />
        <v-btn :disabled="submitting" :text="t('actions.cancel')" @click="open = false" />

        <v-btn
          color="primary"
          :disabled="selected.length === 0"
          :loading="submitting"
          :text="t('health.runDialog.submit')"
          variant="flat"
          @click="submit"
        />
      </v-card-actions>
    </v-card>
  </v-dialog>
</template>

<script lang="ts" setup>
  // Types
  import type { HealthCheckKind, Instance } from '@/api/types'

  // Composables
  import { ref, watch } from 'vue'
  import { useI18n } from 'vue-i18n'

  // Stores
  import { useHealthChecksStore } from '@/stores/healthChecks'

  /**
   * Chooses the checks of a health check, and tells what it means for the users of the server.
   */
  const props = defineProps<{
    instance: Instance
  }>()

  const open = defineModel<boolean>({ default: false })

  const { t } = useI18n()
  const healthChecks = useHealthChecksStore()

  const allChecks: HealthCheckKind[] = ['Validate', 'CrossCheck']
  const selected = ref<HealthCheckKind[]>([...allChecks])
  const submitting = ref(false)
  const submitError = ref<string | null>(null)

  watch(open, value => {
    if (value) {
      selected.value = [...allChecks]
      submitError.value = null
    }
  })

  async function submit () {
    submitting.value = true
    submitError.value = null
    try {
      await healthChecks.start(props.instance.id, selected.value)
    } catch (error) {
      submitError.value = error instanceof Error ? error.message : String(error)
      return
    } finally {
      submitting.value = false
    }

    open.value = false
  }
</script>

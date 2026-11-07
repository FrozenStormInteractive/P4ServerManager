<template>
  <v-dialog v-model="open" max-width="560">
    <v-card :title="t('convertToUnicode.title', { name: instance.name })">
      <v-card-text class="text-body-medium">
        <p class="mb-4">{{ t('convertToUnicode.text') }}</p>

        <p class="mb-4">{{ t('convertToUnicode.clients') }}</p>

        <p>{{ t('convertToUnicode.invalidMetadata') }}</p>

        <v-alert
          class="mt-4"
          density="compact"
          type="warning"
          variant="tonal"
        >
          {{ t(instance.runtimeStatus === 'Running' ? 'convertToUnicode.warningRunning' : 'convertToUnicode.warningStopped') }}
        </v-alert>

        <v-alert
          v-if="submitError"
          class="mt-4"
          density="compact"
          type="error"
          variant="tonal"
        >
          {{ t('convertToUnicode.error', { message: submitError }) }}
        </v-alert>
      </v-card-text>

      <v-card-actions>
        <v-spacer />
        <v-btn :disabled="submitting" :text="t('actions.cancel')" @click="open = false" />

        <v-btn
          color="primary"
          :loading="submitting"
          :text="t('actions.convertToUnicode')"
          variant="flat"
          @click="submit"
        />
      </v-card-actions>
    </v-card>
  </v-dialog>
</template>

<script lang="ts" setup>
  // Types
  import type { Instance } from '@/api/types'

  // Composables
  import { ref, watch } from 'vue'
  import { useI18n } from 'vue-i18n'

  // Stores
  import { useInstancesStore } from '@/stores/instances'

  /**
   * Confirms the conversion of an instance to Unicode mode, which cannot be undone.
   */
  const props = defineProps<{
    instance: Instance
  }>()

  const open = defineModel<boolean>({ default: false })

  const { t } = useI18n()
  const instances = useInstancesStore()

  const submitting = ref(false)
  const submitError = ref<string | null>(null)

  watch(open, value => {
    if (value) {
      submitError.value = null
    }
  })

  async function submit () {
    submitting.value = true
    submitError.value = null
    try {
      await instances.convertToUnicode(props.instance.id)
    } catch (error) {
      submitError.value = error instanceof Error ? error.message : String(error)
      return
    } finally {
      submitting.value = false
    }

    open.value = false
  }
</script>

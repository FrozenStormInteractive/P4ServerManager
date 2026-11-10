<template>
  <v-dialog v-model="open" max-width="560">
    <v-card :title="t('changeServerId.title', { name: instance.name })">
      <v-card-text class="text-body-medium">
        <p class="mb-2">{{ t('changeServerId.text') }}</p>

        <p class="mb-4 text-medium-emphasis">
          {{ t('changeServerId.current', { serverId: instance.serverId ?? t('instance.unknownServerId') }) }}
        </p>

        <v-form id="change-server-id-form" ref="form" @submit.prevent="submit">
          <v-text-field
            v-model="serverId"
            autofocus
            :error-messages="serverErrors"
            :hint="t('changeServerId.superUserNote')"
            :label="t('changeServerId.serverId')"
            name="serverId"
            persistent-hint
            :rules="rules"
          />
        </v-form>

        <v-alert
          class="mt-4"
          density="compact"
          type="warning"
          variant="tonal"
        >
          {{ t(instance.runtimeStatus === 'Running' ? 'changeServerId.warningRunning' : 'changeServerId.warningStopped') }}
        </v-alert>

        <v-alert
          v-if="submitError"
          class="mt-4"
          density="compact"
          type="error"
          variant="tonal"
        >
          {{ t('changeServerId.error', { message: submitError }) }}
        </v-alert>
      </v-card-text>

      <v-card-actions>
        <v-spacer />
        <v-btn :disabled="submitting" :text="t('actions.cancel')" @click="open = false" />

        <v-btn
          color="primary"
          form="change-server-id-form"
          :loading="submitting"
          :text="t('actions.save')"
          type="submit"
          variant="flat"
        />
      </v-card-actions>
    </v-card>
  </v-dialog>
</template>

<script lang="ts" setup>
  // Types
  import type { Instance } from '@/api/types'
  import type { VForm } from 'vuetify/components'

  // Composables
  import { ref, useTemplateRef, watch } from 'vue'
  import { useI18n } from 'vue-i18n'

  // Stores and utilities
  import { ApiError } from '@/api/client'
  import { useInstancesStore } from '@/stores/instances'

  type Rule = (value: unknown) => true | string

  /**
   * Asks for the new server ID of an instance. The daemon then restarts p4d, or starts it for a moment, to rename its
   * server spec.
   */
  const props = defineProps<{
    instance: Instance
  }>()

  const open = defineModel<boolean>({ default: false })

  const { t } = useI18n()
  const instances = useInstancesStore()
  const form = useTemplateRef<VForm>('form')

  const SERVER_ID_PATTERN = /^[A-Z][\w-]{0,31}$/i

  const serverId = ref('')
  const serverErrors = ref<string[]>([])
  const submitError = ref<string | null>(null)
  const submitting = ref(false)

  const rules: Rule[] = [
    value => !!value || t('changeServerId.rules.required'),
    value => SERVER_ID_PATTERN.test(String(value)) || t('changeServerId.rules.format'),
    value => value !== props.instance.serverId || t('changeServerId.rules.unchanged'),
  ]

  // A server error no longer applies once the field changes.
  watch(serverId, () => {
    serverErrors.value = []
  })

  watch(open, value => {
    if (value) {
      serverId.value = props.instance.serverId ?? props.instance.name
      serverErrors.value = []
      submitError.value = null
    }
  }, { immediate: true })

  async function submit () {
    const result = await form.value?.validate()
    if (!result?.valid) {
      return
    }

    submitting.value = true
    submitError.value = null
    serverErrors.value = []
    try {
      await instances.changeServerId(props.instance.id, serverId.value)
    } catch (error) {
      if (error instanceof ApiError && error.fieldErrors.serverId) {
        serverErrors.value = error.fieldErrors.serverId
      } else {
        submitError.value = error instanceof Error ? error.message : String(error)
      }
      return
    } finally {
      submitting.value = false
    }

    open.value = false
  }
</script>

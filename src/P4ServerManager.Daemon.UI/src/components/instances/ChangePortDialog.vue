<template>
  <v-dialog v-model="open" max-width="560">
    <v-card :title="t('changePort.title', { name: instance.name })">
      <v-card-text class="text-body-medium">
        <p class="mb-2">{{ t('changePort.text') }}</p>

        <p class="mb-4 text-medium-emphasis">
          {{ t('changePort.current', { port: formatPort(instance) }) }}
        </p>

        <v-form id="change-port-form" ref="form" @submit.prevent="submit">
          <v-text-field
            v-model.number="port"
            autofocus
            :error-messages="portErrors"
            :hint="t(hasSuperUser ? 'changePort.superUserNote' : 'changePort.noSuperUserNote')"
            :label="t('changePort.port')"
            :max="MAX_PORT"
            :min="MIN_PORT"
            name="port"
            persistent-hint
            :rules="rules"
            type="number"
          />
        </v-form>

        <v-alert
          v-if="warning"
          class="mt-4"
          density="compact"
          type="warning"
          variant="tonal"
        >
          {{ t(warning) }}
        </v-alert>

        <v-alert
          v-if="submitError"
          class="mt-4"
          density="compact"
          type="error"
          variant="tonal"
        >
          {{ t('changePort.error', { message: submitError }) }}
        </v-alert>
      </v-card-text>

      <v-card-actions>
        <v-spacer />
        <v-btn :disabled="submitting" :text="t('actions.cancel')" @click="open = false" />

        <v-btn
          color="primary"
          form="change-port-form"
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
  import { computed, ref, useTemplateRef, watch } from 'vue'
  import { useI18n } from 'vue-i18n'

  // Stores and utilities
  import { ApiError } from '@/api/client'
  import { useInstancesStore } from '@/stores/instances'
  import { formatPort } from '@/utils/instancePort'
  import { getSuperUserProblem } from '@/utils/superUser'

  type Rule = (value: unknown) => true | string

  /**
   * Asks for the new port of an instance. The daemon then restarts p4d on it and, with a usable super user, updates the
   * address of its server spec.
   */
  const props = defineProps<{
    instance: Instance
  }>()

  const open = defineModel<boolean>({ default: false })

  const { t } = useI18n()
  const instances = useInstancesStore()
  const form = useTemplateRef<VForm>('form')

  const MIN_PORT = 1024
  const MAX_PORT = 65_535

  const port = ref<number | string>('')
  const portErrors = ref<string[]>([])
  const submitError = ref<string | null>(null)
  const submitting = ref(false)

  const hasSuperUser = computed(() => getSuperUserProblem(props.instance) === null)

  // A stopped server without a super user only gets its new port: it is not started.
  const warning = computed(() => {
    if (props.instance.runtimeStatus === 'Running') {
      return 'changePort.warningRunning'
    }
    return hasSuperUser.value ? 'changePort.warningStopped' : null
  })

  const rules: Rule[] = [
    value => (value !== '' && value !== null && value !== undefined) || t('changePort.rules.required'),
    value => {
      const number = Number(value)
      return (Number.isInteger(number) && number >= MIN_PORT && number <= MAX_PORT) || t('changePort.rules.range')
    },
    value => Number(value) !== props.instance.port || t('changePort.rules.unchanged'),
  ]

  // A server error no longer applies once the field changes.
  watch(port, () => {
    portErrors.value = []
  })

  watch(open, value => {
    if (value) {
      port.value = props.instance.port
      portErrors.value = []
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
    portErrors.value = []
    try {
      await instances.changePort(props.instance.id, Number(port.value))
    } catch (error) {
      if (error instanceof ApiError && error.fieldErrors.port) {
        portErrors.value = error.fieldErrors.port
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

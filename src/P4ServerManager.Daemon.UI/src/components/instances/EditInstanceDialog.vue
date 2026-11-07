<template>
  <v-dialog v-model="open" max-width="560">
    <v-card :title="t('editInstance.title', { name: instance.name })">
      <v-card-text>
        <v-form id="edit-instance-form" ref="form" @submit.prevent="submit">
          <v-text-field
            v-model="fields.name"
            autofocus
            class="mb-2"
            :error-messages="serverErrors.name"
            :hint="t('editInstance.nameHint')"
            :label="t('createInstance.name')"
            name="name"
            persistent-hint
            :rules="rules.name"
          />

          <v-textarea
            v-model="fields.description"
            auto-grow
            :counter="MAX_DESCRIPTION_LENGTH"
            :error-messages="serverErrors.description"
            :label="t('createInstance.description')"
            name="description"
            rows="1"
            :rules="rules.description"
          />

          <v-alert
            v-if="submitError"
            class="mt-4"
            density="compact"
            type="error"
            variant="tonal"
          >
            {{ t('editInstance.error', { message: submitError }) }}
          </v-alert>
        </v-form>
      </v-card-text>

      <v-card-actions>
        <v-spacer />
        <v-btn :disabled="submitting" :text="t('actions.cancel')" @click="open = false" />

        <v-btn
          color="primary"
          form="edit-instance-form"
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
  import type { Instance, UpdateInstanceRequest } from '@/api/types'
  import type { VForm } from 'vuetify/components'

  // Composables
  import { reactive, ref, useTemplateRef, watch } from 'vue'
  import { useI18n } from 'vue-i18n'

  // Stores and utilities
  import { ApiError } from '@/api/client'
  import { useInstancesStore } from '@/stores/instances'

  type Rule = (value: unknown) => true | string

  const props = defineProps<{
    instance: Instance
  }>()

  const open = defineModel<boolean>({ default: false })

  const { t } = useI18n()
  const instances = useInstancesStore()
  const form = useTemplateRef<VForm>('form')

  const NAME_PATTERN = /^[A-Z][\w-]{0,31}$/i
  const MAX_DESCRIPTION_LENGTH = 512

  const fields = reactive({ name: '', description: '' })
  const serverErrors = ref<Record<string, string[]>>({})
  const submitError = ref<string | null>(null)
  const submitting = ref(false)

  const rules: Record<string, Rule[]> = {
    name: [
      value => !!value || t('createInstance.rules.nameRequired'),
      value => NAME_PATTERN.test(String(value)) || t('createInstance.rules.nameFormat'),
    ],
    description: [
      value => String(value ?? '').length <= MAX_DESCRIPTION_LENGTH || t('createInstance.rules.descriptionLength'),
    ],
  }

  // A server error no longer applies once its field changes.
  watch(() => fields.name, () => clearServerError('name'))
  watch(() => fields.description, () => clearServerError('description'))

  watch(open, value => {
    if (value) {
      fields.name = props.instance.name
      fields.description = props.instance.description ?? ''
      serverErrors.value = {}
      submitError.value = null
    }
  }, { immediate: true })

  function clearServerError (key: string) {
    if (serverErrors.value[key]) {
      const { [key]: _, ...rest } = serverErrors.value
      serverErrors.value = rest
    }
  }

  async function submit () {
    const result = await form.value?.validate()
    if (!result?.valid) {
      return
    }

    // Only what changed is sent. An empty description removes the current one.
    const description = fields.description.trim()
    const request: UpdateInstanceRequest = {
      ...(fields.name === props.instance.name ? {} : { name: fields.name }),
      ...(description === (props.instance.description ?? '') ? {} : { description }),
    }
    if (Object.keys(request).length === 0) {
      open.value = false
      return
    }

    submitting.value = true
    submitError.value = null
    serverErrors.value = {}
    try {
      await instances.update(props.instance.id, request)
    } catch (error) {
      if (error instanceof ApiError) {
        const known = new Set(['name', 'description'])
        serverErrors.value = error.fieldErrors
        const others = Object.entries(error.fieldErrors)
          .filter(([key]) => !known.has(key))
          .flatMap(([, messages]) => messages)
        if (others.length > 0 || Object.keys(error.fieldErrors).length === 0) {
          submitError.value = [error.message, ...others].join(' ')
        }
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

<template>
  <v-dialog v-model="open" max-width="560">
    <v-card :title="t('superUser.dialogTitle', { name: instance.name })">
      <v-card-text>
        <p class="text-body-medium mb-4">{{ t('superUser.intro') }}</p>

        <v-form id="super-user-form" ref="form" @submit.prevent="submit">
          <v-text-field
            v-model="fields.userName"
            autocomplete="off"
            autofocus
            class="mb-2"
            :error-messages="serverErrors.userName"
            :label="t('superUser.userName')"
            name="userName"
            :rules="rules.userName"
          />

          <v-text-field
            v-model="fields.password"
            autocomplete="new-password"
            :error-messages="serverErrors.password"
            :label="t('superUser.password')"
            name="password"
            :rules="rules.password"
            :type="showPassword ? 'text' : 'password'"
          >
            <template #append-inner>
              <v-icon-btn
                :active="showPassword"
                :aria-label="t('createInstance.showPassword')"
                :aria-pressed="showPassword"
                :icon="showPassword ? 'mdi-eye-off' : 'mdi-eye'"
                size="small"
                variant="text"
                @click="showPassword = !showPassword"
              />
            </template>
          </v-text-field>

          <p v-if="instance.runtimeStatus !== 'Running'" class="text-body-small text-medium-emphasis mt-2">
            {{ t('superUser.stoppedNote') }}
          </p>

          <v-alert
            v-if="submitError"
            class="mt-4"
            density="compact"
            type="error"
            variant="tonal"
          >
            {{ t('superUser.error', { message: submitError }) }}
          </v-alert>
        </v-form>
      </v-card-text>

      <v-card-actions>
        <v-spacer />
        <v-btn :disabled="submitting" :text="t('actions.cancel')" @click="open = false" />

        <v-btn
          color="primary"
          form="super-user-form"
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

  const USER_NAME_PATTERN = /^[A-Z][\w.-]{0,63}$/i
  const FIELDS = new Set(['userName', 'password'])

  const fields = reactive({ userName: '', password: '' })
  const serverErrors = ref<Record<string, string[]>>({})
  const submitError = ref<string | null>(null)
  const submitting = ref(false)
  const showPassword = ref(false)

  // The password already exists in P4: it has no strength rule here.
  const rules: Record<string, Rule[]> = {
    userName: [
      value => !!value || t('createInstance.rules.userNameRequired'),
      value => USER_NAME_PATTERN.test(String(value)) || t('createInstance.rules.userNameFormat'),
    ],
    password: [
      value => !!value || t('superUser.rules.passwordRequired'),
    ],
  }

  // A server error no longer applies once its field changes.
  watch(() => fields.userName, () => clearServerError('userName'))
  watch(() => fields.password, () => clearServerError('password'))

  watch(open, value => {
    if (value) {
      fields.userName = props.instance.superUser?.userName ?? ''
      fields.password = ''
      serverErrors.value = {}
      submitError.value = null
      showPassword.value = false
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

    submitting.value = true
    submitError.value = null
    serverErrors.value = {}
    try {
      await instances.setSuperUser(props.instance.id, { userName: fields.userName, password: fields.password })
    } catch (error) {
      if (error instanceof ApiError) {
        serverErrors.value = error.fieldErrors
        const others = Object.entries(error.fieldErrors)
          .filter(([key]) => !FIELDS.has(key))
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

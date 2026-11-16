<template>
  <v-dialog v-model="open" max-width="480">
    <v-card :title="t('auth.changePassword.title')">
      <v-card-text>
        <p class="text-body-medium mb-4">{{ t('auth.changePassword.intro') }}</p>

        <v-form id="change-password-form" ref="form" @submit.prevent="submit">
          <v-text-field
            v-model="fields.currentPassword"
            autocomplete="current-password"
            autofocus
            class="mb-2"
            :error-messages="serverErrors.currentPassword"
            :label="t('auth.changePassword.current')"
            name="currentPassword"
            :rules="[required]"
            type="password"
          />

          <v-text-field
            v-model="fields.newPassword"
            autocomplete="new-password"
            class="mb-2"
            :error-messages="serverErrors.newPassword"
            :hint="t('auth.passwordHint')"
            :label="t('auth.changePassword.new')"
            name="newPassword"
            persistent-hint
            :rules="rules.newPassword"
            type="password"
          />

          <v-text-field
            v-model="fields.confirmation"
            autocomplete="new-password"
            :label="t('auth.confirmPassword')"
            name="confirmation"
            :rules="rules.confirmation"
            type="password"
          />

          <v-alert
            v-if="submitError"
            class="mt-4"
            density="compact"
            type="error"
            variant="tonal"
          >
            {{ submitError }}
          </v-alert>
        </v-form>
      </v-card-text>

      <v-card-actions>
        <v-spacer />
        <v-btn :disabled="submitting" :text="t('actions.cancel')" @click="open = false" />

        <v-btn
          color="primary"
          form="change-password-form"
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
  import type { VForm } from 'vuetify/components'

  // Composables
  import { reactive, ref, useTemplateRef, watch } from 'vue'
  import { useI18n } from 'vue-i18n'

  // Stores and utilities
  import { ApiError } from '@/api/client'
  import { useAuthStore } from '@/stores/auth'
  import { useNotificationsStore } from '@/stores/notifications'
  import { submitErrorMessage } from '@/utils/authErrors'
  import { confirmationRules, newPasswordRules } from '@/utils/passwordRules'

  /**
   * Changes the password of the signed-in user. Its other sessions end; this one goes on.
   */
  const open = defineModel<boolean>({ default: false })

  const { t } = useI18n()
  const auth = useAuthStore()
  const notifications = useNotificationsStore()
  const form = useTemplateRef<VForm>('form')

  const fields = reactive({ currentPassword: '', newPassword: '', confirmation: '' })
  const serverErrors = ref<Record<string, string[]>>({})
  const submitError = ref<string | null>(null)
  const submitting = ref(false)

  const rules = {
    newPassword: newPasswordRules(t),
    confirmation: confirmationRules(t, () => fields.newPassword),
  }

  watch(open, value => {
    if (value) {
      fields.currentPassword = ''
      fields.newPassword = ''
      fields.confirmation = ''
      serverErrors.value = {}
      submitError.value = null
    }
  }, { immediate: true })

  watch(() => fields.currentPassword, () => {
    serverErrors.value = { ...serverErrors.value, currentPassword: [] }
  })

  function required (value: unknown): true | string {
    return !!value || t('auth.rules.required')
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
      await auth.changePassword({ currentPassword: fields.currentPassword, newPassword: fields.newPassword })
    } catch (error) {
      if (error instanceof ApiError && Object.keys(error.fieldErrors).length > 0) {
        serverErrors.value = error.fieldErrors
      } else {
        submitError.value = submitErrorMessage(error, t)
      }
      return
    } finally {
      submitting.value = false
    }

    notifications.notify(t('auth.changePassword.done'))
    open.value = false
  }
</script>

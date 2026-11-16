<template>
  <v-container class="fill-height justify-center" max-width="480">
    <v-card class="w-100" :subtitle="t('app.title')" :title="t('auth.setup.title')">
      <template #prepend>
        <v-icon color="primary" icon="mdi-account-key-outline" size="32" />
      </template>

      <v-card-text>
        <p class="text-body-medium mb-4">{{ t('auth.setup.intro') }}</p>

        <v-form id="setup-form" ref="form" @submit.prevent="submit">
          <v-text-field
            v-model="fields.userName"
            autocomplete="username"
            autofocus
            class="mb-2"
            :error-messages="serverErrors.userName"
            :label="t('auth.userName')"
            name="userName"
            :rules="rules.userName"
          />

          <v-text-field
            v-model="fields.password"
            autocomplete="new-password"
            class="mb-2"
            :error-messages="serverErrors.password"
            :hint="t('auth.passwordHint')"
            :label="t('auth.password')"
            name="password"
            persistent-hint
            :rules="rules.password"
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
        <v-btn
          block
          color="primary"
          form="setup-form"
          :loading="submitting"
          :text="t('auth.setup.submit')"
          type="submit"
          variant="flat"
        />
      </v-card-actions>
    </v-card>
  </v-container>
</template>

<script lang="ts" setup>
  // Types
  import type { VForm } from 'vuetify/components'

  // Composables
  import { reactive, ref, useTemplateRef } from 'vue'
  import { useI18n } from 'vue-i18n'
  import { useRouter } from 'vue-router'

  // Stores and utilities
  import { ApiError } from '@/api/client'
  import { useAuthStore } from '@/stores/auth'
  import { submitErrorMessage } from '@/utils/authErrors'
  import { confirmationRules, newPasswordRules, usernameRules } from '@/utils/passwordRules'

  /**
   * Creates the first account, while the daemon has none, then signs it in. Once an account exists, the router no
   * longer shows this page.
   */
  const { t } = useI18n()
  const auth = useAuthStore()
  const router = useRouter()
  const form = useTemplateRef<VForm>('form')

  const fields = reactive({ userName: 'admin', password: '', confirmation: '' })
  const serverErrors = ref<Record<string, string[]>>({})
  const submitError = ref<string | null>(null)
  const submitting = ref(false)

  const rules = {
    userName: usernameRules(t),
    password: newPasswordRules(t),
    confirmation: confirmationRules(t, () => fields.password),
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
      await auth.setup({ userName: fields.userName, password: fields.password })
    } catch (error) {
      if (error instanceof ApiError && Object.keys(error.fieldErrors).length > 0) {
        serverErrors.value = error.fieldErrors
      } else if (error instanceof ApiError && error.status === 409) {
        // Another browser created the first account in the meantime.
        auth.sessionEnded()
        await router.replace({ name: 'login' })
      } else {
        submitError.value = submitErrorMessage(error, t)
      }
      return
    } finally {
      submitting.value = false
    }

    await router.replace({ name: 'dashboard' })
  }
</script>

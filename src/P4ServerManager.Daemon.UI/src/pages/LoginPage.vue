<template>
  <v-container class="fill-height justify-center" max-width="440">
    <v-card class="w-100" :subtitle="t('auth.login.subtitle')" :title="t('app.title')">
      <template #prepend>
        <v-icon color="primary" icon="mdi-server-security" size="32" />
      </template>

      <v-card-text>
        <v-form id="login-form" ref="form" @submit.prevent="submit">
          <v-text-field
            v-model="fields.userName"
            autocomplete="username"
            autofocus
            class="mb-2"
            :label="t('auth.userName')"
            name="userName"
            :rules="[required]"
          />

          <v-text-field
            v-model="fields.password"
            autocomplete="current-password"
            :label="t('auth.password')"
            name="password"
            :rules="[required]"
            type="password"
          />

          <v-checkbox
            v-model="fields.rememberMe"
            density="compact"
            hide-details
            :label="t('auth.login.rememberMe')"
            name="rememberMe"
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
          form="login-form"
          :loading="submitting"
          :text="t('auth.login.submit')"
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
  import { useRoute, useRouter } from 'vue-router'

  // Stores
  import { useAuthStore } from '@/stores/auth'
  import { submitErrorMessage } from '@/utils/authErrors'

  /**
   * Signs a user in, then goes back to the page the user wanted, or to the dashboard.
   */
  const { t } = useI18n()
  const auth = useAuthStore()
  const route = useRoute()
  const router = useRouter()
  const form = useTemplateRef<VForm>('form')

  const fields = reactive({ userName: '', password: '', rememberMe: false })
  const submitError = ref<string | null>(null)
  const submitting = ref(false)

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
    try {
      await auth.login({ ...fields })
    } catch (error) {
      submitError.value = submitErrorMessage(error, t)
      return
    } finally {
      submitting.value = false
    }

    const redirect = typeof route.query.redirect === 'string' && route.query.redirect.startsWith('/')
      ? route.query.redirect
      : '/'
    await router.replace(redirect)
  }
</script>

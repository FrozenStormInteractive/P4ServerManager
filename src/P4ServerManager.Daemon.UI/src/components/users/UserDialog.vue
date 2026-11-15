<template>
  <v-dialog v-model="open" max-width="480">
    <v-card :title="user ? t('users.passwordTitle', { name: user.userName }) : t('users.createTitle')">
      <v-card-text>
        <p v-if="user" class="text-body-medium mb-4">{{ t('users.passwordIntro') }}</p>

        <v-form id="user-form" ref="form" @submit.prevent="submit">
          <v-text-field
            v-if="!user"
            v-model="fields.userName"
            autocomplete="off"
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
            :autofocus="!!user"
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
        <v-spacer />
        <v-btn :disabled="submitting" :text="t('actions.cancel')" @click="open = false" />

        <v-btn
          color="primary"
          form="user-form"
          :loading="submitting"
          :text="user ? t('actions.save') : t('actions.create')"
          type="submit"
          variant="flat"
        />
      </v-card-actions>
    </v-card>
  </v-dialog>
</template>

<script lang="ts" setup>
  // Types
  import type { User } from '@/api/types'
  import type { VForm } from 'vuetify/components'

  // Composables
  import { reactive, ref, useTemplateRef, watch } from 'vue'
  import { useI18n } from 'vue-i18n'

  // Stores and utilities
  import { ApiError } from '@/api/client'
  import { useNotificationsStore } from '@/stores/notifications'
  import { useUsersStore } from '@/stores/users'
  import { submitErrorMessage } from '@/utils/authErrors'
  import { confirmationRules, newPasswordRules, usernameRules } from '@/utils/passwordRules'

  /**
   * Adds an account, or sets a new password on one when `user` is given.
   */
  const props = defineProps<{
    user: User | null
  }>()

  const open = defineModel<boolean>({ default: false })

  const { t } = useI18n()
  const users = useUsersStore()
  const notifications = useNotificationsStore()
  const form = useTemplateRef<VForm>('form')

  const fields = reactive({ userName: '', password: '', confirmation: '' })
  const serverErrors = ref<Record<string, string[]>>({})
  const submitError = ref<string | null>(null)
  const submitting = ref(false)

  const rules = {
    userName: usernameRules(t),
    password: newPasswordRules(t),
    confirmation: confirmationRules(t, () => fields.password),
  }

  watch(open, value => {
    if (value) {
      fields.userName = ''
      fields.password = ''
      fields.confirmation = ''
      serverErrors.value = {}
      submitError.value = null
    }
  }, { immediate: true })

  watch(() => fields.userName, () => {
    serverErrors.value = { ...serverErrors.value, userName: [] }
  })

  async function submit () {
    const result = await form.value?.validate()
    if (!result?.valid) {
      return
    }

    submitting.value = true
    submitError.value = null
    serverErrors.value = {}
    try {
      if (props.user) {
        await users.setPassword(props.user.id, fields.password)
        notifications.notify(t('users.passwordSet', { name: props.user.userName }))
      } else {
        const user = await users.create({ userName: fields.userName, password: fields.password })
        notifications.notify(t('users.created', { name: user.userName }))
      }
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

    open.value = false
  }
</script>

<template>
  <v-app-bar border="b" flat>
    <template #prepend>
      <v-app-bar-nav-icon
        v-if="mobile"
        :aria-label="t('navigation.toggle')"
        @click="app.drawer = !app.drawer"
      />
    </template>

    <v-app-bar-title>{{ t('app.title') }}</v-app-bar-title>

    <template #append>
      <v-btn
        class="mr-2"
        color="primary"
        prepend-icon="mdi-plus"
        :text="t('actions.newServer')"
        variant="flat"
        @click="app.openCreateDialog()"
      />

      <v-menu location="bottom end">
        <template #activator="{ props: menu }">
          <v-btn
            v-bind="menu"
            :aria-label="t('auth.menu.label')"
            class="mr-2"
            icon="mdi-account-circle-outline"
          />
        </template>

        <v-list density="compact" min-width="220">
          <v-list-item prepend-icon="mdi-account-outline" :subtitle="t('auth.menu.signedInAs')" :title="auth.user?.userName" />

          <v-divider class="my-1" />

          <v-list-item
            prepend-icon="mdi-form-textbox-password"
            :title="t('auth.changePassword.title')"
            @click="changePasswordOpen = true"
          />

          <v-list-item prepend-icon="mdi-logout" :title="t('actions.signOut')" @click="signOut" />
        </v-list>
      </v-menu>
    </template>
  </v-app-bar>

  <ChangePasswordDialog v-model="changePasswordOpen" />
</template>

<script lang="ts" setup>
  // Composables
  import { ref } from 'vue'
  import { useI18n } from 'vue-i18n'
  import { useRouter } from 'vue-router'
  import { useDisplay } from 'vuetify'

  // Components and stores
  import ChangePasswordDialog from '@/components/auth/ChangePasswordDialog.vue'
  import { useAppStore } from '@/stores/app'
  import { useAuthStore } from '@/stores/auth'

  const { t } = useI18n()
  const { mobile } = useDisplay()
  const app = useAppStore()
  const auth = useAuthStore()
  const router = useRouter()

  const changePasswordOpen = ref(false)

  async function signOut () {
    try {
      await auth.logout()
    } finally {
      await router.replace({ name: 'login' })
    }
  }
</script>

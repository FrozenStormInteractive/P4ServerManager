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
    </template>
  </v-app-bar>
</template>

<script lang="ts" setup>
  // Composables
  import { useI18n } from 'vue-i18n'
  import { useDisplay } from 'vuetify'

  // Stores
  import { useAppStore } from '@/stores/app'

  const { t } = useI18n()
  const { mobile } = useDisplay()
  const app = useAppStore()
</script>

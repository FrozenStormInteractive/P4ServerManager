<template>
  <v-snackbar-queue
    v-model="notifications.queue"
    closable
    :close-text="t('actions.close')"
    location="bottom right"
  />
</template>

<script lang="ts" setup>
  // Composables
  import { useI18n } from 'vue-i18n'

  // Stores
  import { useNotificationsStore } from '@/stores/notifications'

  const { t } = useI18n()
  const notifications = useNotificationsStore()
</script>

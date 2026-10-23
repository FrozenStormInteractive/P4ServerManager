<template>
  <v-container max-width="1200">
    <v-empty-state
      :action-text="t('notFound.back')"
      icon="mdi-map-marker-question-outline"
      :text="t('notFound.text')"
      :title="t('notFound.title')"
      @click:action="router.push({ name: 'dashboard' })"
    />
  </v-container>
</template>

<script lang="ts" setup>
  // Composables
  import { useI18n } from 'vue-i18n'
  import { useRouter } from 'vue-router'

  const { t } = useI18n()
  const router = useRouter()
</script>

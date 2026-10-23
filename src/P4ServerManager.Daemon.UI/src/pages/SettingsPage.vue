<template>
  <v-container max-width="1200">
    <h1 class="text-headline-small mb-6">{{ t('settings.title') }}</h1>

    <v-empty-state icon="mdi-cog-outline" :text="t('settings.placeholder')" />
  </v-container>
</template>

<script lang="ts" setup>
  // Composables
  import { useI18n } from 'vue-i18n'

  const { t } = useI18n()
</script>

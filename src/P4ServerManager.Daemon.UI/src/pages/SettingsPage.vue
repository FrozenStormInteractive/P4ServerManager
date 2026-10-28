<template>
  <v-navigation-drawer v-model="drawer" width="232">
    <v-list aria-label="settings" density="compact" nav>
      <v-list-subheader>{{ t('settings.title') }}</v-list-subheader>

      <v-list-item
        v-for="section in sections"
        :key="section.name"
        :prepend-icon="section.icon"
        :title="t(section.title)"
        :to="{ name: section.name }"
      />
    </v-list>
  </v-navigation-drawer>

  <v-container max-width="1200">
    <div class="d-flex align-center ga-2 mb-6">
      <v-btn
        v-if="mobile"
        :aria-label="t('settings.openSections')"
        icon="mdi-menu-open"
        variant="text"
        @click="drawer = !drawer"
      />

      <h1 class="text-headline-small">{{ current ? t(current.title) : t('settings.title') }}</h1>
    </div>

    <router-view />
  </v-container>
</template>

<script lang="ts" setup>
  // Composables
  import { computed, ref } from 'vue'
  import { useI18n } from 'vue-i18n'
  import { useRoute } from 'vue-router'
  import { useDisplay } from 'vuetify'

  /**
   * Lists the sections of the settings, each a child route, in a drawer of its own beside the navigation of the app.
   * On small screens the drawer is temporary and opens from the button before the title.
   */
  const { t } = useI18n()
  const { mobile } = useDisplay()
  const route = useRoute()

  // Left null, the drawer opens on wide screens and stays closed on small ones.
  const drawer = ref<boolean | null>(null)

  const sections = [
    { name: 'settings-builds', title: 'settings.sections.builds', icon: 'mdi-package-variant-closed' },
  ] as const

  const current = computed(() => sections.find(section => section.name === route.name))
</script>

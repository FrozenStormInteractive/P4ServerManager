<template>
  <v-navigation-drawer v-model="app.drawer">
    <v-list nav>
      <v-list-item
        v-for="page in pages"
        :key="page.name"
        :prepend-icon="page.icon"
        :title="t(page.title)"
        :to="{ name: page.name }"
      />
    </v-list>

    <v-divider class="mx-2" />

    <v-list density="compact" nav>
      <v-list-subheader>{{ t('navigation.instances') }}</v-list-subheader>

      <v-list-item
        v-for="instance in instances.sortedInstances"
        :key="instance.id"
        prepend-icon="mdi-server-outline"
        :title="instance.name"
        :to="{ name: 'instance', params: { id: instance.id } }"
      >
        <template #append>
          <InstanceStatusIndicator :instance="instance" />
        </template>
      </v-list-item>

      <v-list-item
        v-if="instances.loaded && instances.sortedInstances.length === 0"
        class="text-medium-emphasis"
        :title="t('navigation.noInstances')"
      />
    </v-list>
  </v-navigation-drawer>
</template>

<script lang="ts" setup>
  // Composables
  import { useI18n } from 'vue-i18n'

  // Components and stores
  import InstanceStatusIndicator from '@/components/instances/InstanceStatusIndicator.vue'
  import { useAppStore } from '@/stores/app'
  import { useInstancesStore } from '@/stores/instances'

  const { t } = useI18n()
  const app = useAppStore()
  const instances = useInstancesStore()

  const pages = [
    { name: 'dashboard', title: 'navigation.dashboard', icon: 'mdi-view-dashboard-outline' },
    { name: 'instances', title: 'navigation.instances', icon: 'mdi-server' },
    { name: 'settings', title: 'navigation.settings', icon: 'mdi-cog-outline' },
  ] as const
</script>

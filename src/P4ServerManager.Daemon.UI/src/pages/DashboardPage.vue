<template>
  <v-container max-width="1200">
    <h1 class="text-headline-small mb-6">{{ t('dashboard.title') }}</h1>

    <v-row>
      <v-col v-for="stat in stats" :key="stat.label" cols="6" md="3">
        <v-card variant="tonal">
          <v-card-text>
            <div class="d-flex align-center">
              <v-icon class="mr-3" :color="stat.color" :icon="stat.icon" />

              <div>
                <div class="text-headline-small">{{ stat.value }}</div>
                <div class="text-body-medium text-medium-emphasis">{{ t(stat.label) }}</div>
              </div>
            </div>
          </v-card-text>
        </v-card>
      </v-col>
    </v-row>

    <v-card v-if="instances.sortedInstances.length > 0" class="mt-6" :title="t('instances.title')">
      <v-list>
        <v-list-item
          v-for="instance in instances.sortedInstances"
          :key="instance.id"
          :subtitle="describe(instance)"
          :title="instance.name"
          :to="{ name: 'instance', params: { id: instance.id } }"
        >
          <template #append>
            <InstanceStatusChip :instance="instance" />
          </template>
        </v-list-item>
      </v-list>
    </v-card>

    <v-empty-state
      v-else-if="instances.loaded"
      :action-text="t('dashboard.createServer')"
      class="mt-6"
      icon="mdi-server-plus-outline"
      :text="t('dashboard.emptyText')"
      :title="t('dashboard.emptyTitle')"
      @click:action="app.openCreateDialog()"
    />
  </v-container>
</template>

<script lang="ts" setup>
  // Types
  import type { Instance } from '@/api/types'

  // Composables
  import { computed } from 'vue'
  import { useI18n } from 'vue-i18n'

  // Components, stores and utilities
  import InstanceStatusChip from '@/components/instances/InstanceStatusChip.vue'
  import { useAppStore } from '@/stores/app'
  import { useInstancesStore } from '@/stores/instances'
  import { formatPort } from '@/utils/instancePort'
  import { getInstanceStatus } from '@/utils/instanceStatus'

  const { t } = useI18n()
  const app = useAppStore()
  const instances = useInstancesStore()

  const stats = computed(() => {
    const statuses = instances.sortedInstances.map(instance => getInstanceStatus(instance))
    const count = (status: string) => statuses.filter(s => s === status).length
    return [
      { label: 'dashboard.total', value: statuses.length, icon: 'mdi-server', color: 'primary' },
      { label: 'dashboard.running', value: count('running'), icon: 'mdi-play-circle-outline', color: 'success' },
      { label: 'dashboard.stopped', value: count('stopped'), icon: 'mdi-stop-circle-outline', color: 'grey' },
      { label: 'dashboard.failed', value: count('failed'), icon: 'mdi-alert-circle-outline', color: 'error' },
    ]
  })

  function describe (instance: Instance) {
    const version = instance.version ? `${instance.version}/${instance.build}` : instance.release
    return t('dashboard.instanceSummary', { version, port: formatPort(instance) })
  }
</script>

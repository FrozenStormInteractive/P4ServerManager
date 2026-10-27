<template>
  <v-container max-width="1200">
    <div class="d-flex align-center mb-6">
      <h1 class="text-headline-small">{{ t('instances.title') }}</h1>
      <v-spacer />

      <v-btn
        color="primary"
        prepend-icon="mdi-plus"
        :text="t('actions.newServer')"
        variant="flat"
        @click="app.openCreateDialog()"
      />
    </div>

    <v-alert
      v-if="instances.error"
      class="mb-4"
      density="compact"
      type="error"
      variant="tonal"
    >
      {{ t('instances.loadError', { message: instances.error }) }}
    </v-alert>

    <LegacyProfilesCard />

    <v-card>
      <v-data-table
        :headers="headers"
        hover
        item-value="id"
        :items="instances.sortedInstances"
        :loading="instances.loading && !instances.loaded"
        :no-data-text="t('instances.noData')"
        @click:row="openInstance"
      >
        <template #item.status="{ item }">
          <InstanceStatusChip :instance="item" />
        </template>

        <template #item.version="{ item }">
          {{ item.version ? `${item.version}/${item.build}` : item.release }}
        </template>
      </v-data-table>
    </v-card>
  </v-container>
</template>

<script lang="ts" setup>
  // Types
  import type { Instance } from '@/api/types'

  // Composables
  import { computed } from 'vue'
  import { useI18n } from 'vue-i18n'
  import { useRouter } from 'vue-router'

  // Components and stores
  import InstanceStatusChip from '@/components/instances/InstanceStatusChip.vue'
  import LegacyProfilesCard from '@/components/instances/LegacyProfilesCard.vue'
  import { useAppStore } from '@/stores/app'
  import { useInstancesStore } from '@/stores/instances'
  import { formatPort } from '@/utils/instancePort'

  const { t } = useI18n()
  const router = useRouter()
  const app = useAppStore()
  const instances = useInstancesStore()

  const headers = computed(() => [
    { title: t('instances.headers.name'), key: 'name' },
    { title: t('instances.headers.status'), key: 'status', sortable: false },
    { title: t('instances.headers.version'), key: 'version' },
    { title: t('instances.headers.port'), key: 'port', value: (item: Instance) => formatPort(item) },
    { title: t('instances.headers.root'), key: 'rootPath' },
  ])

  function openInstance (_event: Event, { item }: { item: Instance }) {
    void router.push({ name: 'instance', params: { id: item.id } })
  }
</script>

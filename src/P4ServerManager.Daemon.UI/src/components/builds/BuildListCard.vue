<template>
  <v-card prepend-icon="mdi-package-variant-closed" :title="t('builds.title')">
    <template #append>
      <div class="d-flex ga-2">
        <v-btn
          prepend-icon="mdi-cloud-download-outline"
          :text="t('builds.fetch')"
          variant="text"
          @click="downloadOpen = true"
        />

        <v-btn
          color="primary"
          prepend-icon="mdi-upload"
          :text="t('actions.import')"
          variant="tonal"
          @click="importOpen = true"
        />
      </div>
    </template>

    <v-card-text class="pb-0">{{ t('builds.text') }}</v-card-text>

    <BuildFetchQueue />

    <v-alert
      v-if="builds.error"
      class="ma-4"
      density="compact"
      type="error"
      variant="tonal"
    >
      {{ t('builds.loadError', { message: builds.error }) }}
    </v-alert>

    <v-alert
      v-if="builds.status?.lastDownloadError"
      class="ma-4"
      density="compact"
      type="warning"
      variant="tonal"
    >
      {{ t('builds.downloadError', { message: builds.status.lastDownloadError }) }}
    </v-alert>

    <v-alert
      v-if="actionError"
      class="ma-4"
      closable
      density="compact"
      type="error"
      variant="tonal"
      @click:close="actionError = null"
    >
      {{ actionError }}
    </v-alert>

    <v-data-table
      :headers="headers"
      hide-default-footer
      hover
      item-value="id"
      :items="builds.sortedBuilds"
      :items-per-page="-1"
      :loading="!builds.loaded && !builds.error"
      :no-data-text="t('builds.empty')"
    >
      <template #item.version="{ item }">
        <div class="d-flex align-center ga-2">
          <span>{{ item.version }}/{{ item.build }}</span>
          <span class="text-medium-emphasis">({{ item.release }})</span>

          <v-chip
            v-if="item.extracted"
            color="success"
            label
            size="x-small"
            :text="t('builds.extracted')"
          />
        </div>
      </template>

      <template #item.source="{ item }">
        {{ t(`builds.sources.${item.source}`) }}
      </template>

      <template #item.size="{ item }">
        <span v-if="item.archiveSize !== null">{{ formatBytes(item.archiveSize, locale) }}</span>
        <span v-else class="text-medium-emphasis">{{ t('builds.notArchived') }}</span>
      </template>

      <template #item.usedBy="{ item }">
        <template v-if="usersOf(item).length > 0">
          <router-link
            v-for="(instance, index) in usersOf(item)"
            :key="instance.id"
            :to="{ name: 'instance', params: { id: instance.id } }"
          >
            {{ instance.name }}<template v-if="index < usersOf(item).length - 1">, </template>
          </router-link>
        </template>

        <span v-else class="text-medium-emphasis">{{ t('builds.unused') }}</span>
      </template>

      <template #item.addedAt="{ item }">
        {{ d(new Date(item.addedAt), 'long') }}
      </template>

      <template #item.protected="{ item }">
        <v-switch
          color="primary"
          density="compact"
          :disabled="busy === item.id"
          hide-details
          inset
          :model-value="item.protected"
          :title="t('builds.protectHint')"
          @update:model-value="value => setProtected(item, !!value)"
        />
      </template>

      <template #item.actions="{ item }">
        <!-- A disabled button gets no pointer events: the tooltip hangs on the span around it. -->
        <v-tooltip :disabled="usersOf(item).length === 0" location="start" :text="t('builds.inUse')">
          <template #activator="{ props }">
            <span v-bind="props" data-delete>
              <v-btn
                :aria-label="t('actions.delete')"
                :disabled="usersOf(item).length > 0"
                icon="mdi-delete-outline"
                variant="text"
                @click="confirmDelete(item)"
              />
            </span>
          </template>
        </v-tooltip>
      </template>
    </v-data-table>
  </v-card>

  <ImportBuildDialog v-model="importOpen" />
  <DownloadBuildDialog v-model="downloadOpen" />
  <DeleteBuildDialog v-model="deleteOpen" :build="selected" />
</template>

<script lang="ts" setup>
  // Types
  import type { Build, Instance } from '@/api/types'

  // Composables
  import { computed, ref } from 'vue'
  import { useI18n } from 'vue-i18n'

  import BuildFetchQueue from '@/components/builds/BuildFetchQueue.vue'
  // Components, stores and utilities
  import DeleteBuildDialog from '@/components/builds/DeleteBuildDialog.vue'
  import DownloadBuildDialog from '@/components/builds/DownloadBuildDialog.vue'
  import ImportBuildDialog from '@/components/builds/ImportBuildDialog.vue'
  import { useBuildsStore } from '@/stores/builds'
  import { useInstancesStore } from '@/stores/instances'
  import { formatBytes } from '@/utils/bytes'

  /**
   * The builds of the archive, newest first, with the servers that use them.
   */
  const { t, d, locale } = useI18n()
  const builds = useBuildsStore()
  const instances = useInstancesStore()

  const importOpen = ref(false)
  const downloadOpen = ref(false)
  const deleteOpen = ref(false)
  const selected = ref<Build | null>(null)
  const busy = ref<string | null>(null)
  const actionError = ref<string | null>(null)

  const headers = computed(() => [
    { title: t('builds.headers.version'), key: 'version', sortable: false },
    { title: t('builds.headers.source'), key: 'source', sortable: false },
    { title: t('builds.headers.size'), key: 'size', sortable: false },
    { title: t('builds.headers.usedBy'), key: 'usedBy', sortable: false },
    { title: t('builds.headers.addedAt'), key: 'addedAt', sortable: false },
    { title: t('builds.headers.protected'), key: 'protected', sortable: false },
    { title: '', key: 'actions', sortable: false, align: 'end' as const },
  ])

  /**
   * The servers that run a build, read from the instances, which the daemon keeps up to date.
   */
  function usersOf (build: Build): Instance[] {
    return instances.sortedInstances.filter(instance =>
      instance.version === build.version && instance.build === build.build)
  }

  async function setProtected (build: Build, value: boolean) {
    busy.value = build.id
    actionError.value = null
    try {
      await builds.setProtected(build.id, value)
    } catch (error) {
      actionError.value = t('builds.protectError', { message: error instanceof Error ? error.message : String(error) })
    } finally {
      busy.value = null
    }
  }

  function confirmDelete (build: Build) {
    selected.value = build
    deleteOpen.value = true
  }
</script>

<template>
  <v-card prepend-icon="mdi-cloud-upload-outline" :title="t('settings.sections.backups')">
    <template #append>
      <v-btn
        color="primary"
        prepend-icon="mdi-plus"
        :text="t('backupDestinations.add')"
        variant="tonal"
        @click="openDialog(null)"
      />
    </template>

    <v-card-text class="pb-0">{{ t('backupDestinations.text') }}</v-card-text>

    <v-alert
      v-if="backups.destinationsError"
      class="ma-4"
      density="compact"
      type="error"
      variant="tonal"
    >
      {{ t('backupDestinations.loadError', { message: backups.destinationsError }) }}
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
      :items="backups.destinations"
      :items-per-page="-1"
      :loading="!backups.destinationsLoaded && !backups.destinationsError"
      :no-data-text="t('backupDestinations.empty')"
    >
      <template #item.kind="{ item }">
        {{ t(`backupDestinations.kinds.${item.kind}`) }}
      </template>

      <template #item.location="{ item }">
        <span class="text-medium-emphasis" style="word-break: break-all">{{ item.location }}</span>
      </template>

      <template #item.actions="{ item }">
        <div class="d-flex justify-end">
          <v-btn
            :aria-label="t('restores.points.browse')"
            icon="mdi-archive-search-outline"
            size="small"
            variant="text"
            @click="browse(item)"
          />

          <v-btn
            :aria-label="t('backupDestinations.test')"
            icon="mdi-connection"
            :loading="testing === item.id"
            size="small"
            variant="text"
            @click="test(item)"
          />

          <v-btn
            :aria-label="t('actions.edit')"
            icon="mdi-pencil-outline"
            size="small"
            variant="text"
            @click="openDialog(item)"
          />

          <v-btn
            :aria-label="t('backupDestinations.remove')"
            color="error"
            icon="mdi-delete-outline"
            size="small"
            variant="text"
            @click="askRemove(item)"
          />
        </div>
      </template>
    </v-data-table>

    <BackupDestinationDialog v-model="dialogOpen" :destination="selected" />

    <BackupPointsDialog v-model="pointsOpen" :destination="selected" />

    <v-dialog v-model="removeOpen" max-width="480">
      <v-card :title="t('backupDestinations.removeTitle', { name: selected?.name ?? '' })">
        <v-card-text>{{ t('backupDestinations.removeText') }}</v-card-text>

        <v-card-actions>
          <v-spacer />
          <v-btn :disabled="removing" :text="t('actions.cancel')" @click="removeOpen = false" />

          <v-btn
            color="error"
            :loading="removing"
            :text="t('backupDestinations.remove')"
            variant="flat"
            @click="remove"
          />
        </v-card-actions>
      </v-card>
    </v-dialog>
  </v-card>
</template>

<script lang="ts" setup>
  // Types
  import type { BackupDestination } from '@/api/types'

  // Composables
  import { computed, ref } from 'vue'
  import { useI18n } from 'vue-i18n'

  // Components and stores
  import BackupDestinationDialog from '@/components/backups/BackupDestinationDialog.vue'
  import BackupPointsDialog from '@/components/backups/BackupPointsDialog.vue'
  import { useBackupsStore } from '@/stores/backups'
  import { useNotificationsStore } from '@/stores/notifications'

  /**
   * The backup destinations of the daemon: added, edited, tested and removed here. The page loads them.
   */
  const { t } = useI18n()
  const backups = useBackupsStore()
  const notifications = useNotificationsStore()

  const dialogOpen = ref(false)
  const removeOpen = ref(false)
  const pointsOpen = ref(false)
  const selected = ref<BackupDestination | null>(null)
  const testing = ref<string | null>(null)
  const removing = ref(false)
  const actionError = ref<string | null>(null)

  const headers = computed(() => [
    { title: t('backupDestinations.headers.name'), key: 'name', sortable: false },
    { title: t('backupDestinations.headers.kind'), key: 'kind', sortable: false },
    { title: t('backupDestinations.headers.location'), key: 'location', sortable: false },
    { title: t('backupDestinations.headers.instances'), key: 'instanceCount', sortable: false },
    { title: '', key: 'actions', sortable: false, align: 'end' as const },
  ])

  function openDialog (destination: BackupDestination | null) {
    selected.value = destination
    dialogOpen.value = true
  }

  function browse (destination: BackupDestination) {
    selected.value = destination
    pointsOpen.value = true
  }

  function askRemove (destination: BackupDestination) {
    selected.value = destination
    removeOpen.value = true
  }

  async function test (destination: BackupDestination) {
    testing.value = destination.id
    actionError.value = null
    try {
      const result = await backups.testDestination(destination.id)
      if (result.succeeded) {
        const capabilities = result.capabilities.length > 0
          ? result.capabilities.join(', ')
          : t('backupDestinations.noCapabilities')
        notifications.notify(t('backupDestinations.testSucceeded', { name: destination.name, capabilities }))
      } else {
        actionError.value = t('backupDestinations.testFailed', { name: destination.name, error: result.error ?? '' })
      }
    } catch (error) {
      actionError.value = error instanceof Error ? error.message : String(error)
    } finally {
      testing.value = null
    }
  }

  async function remove () {
    const destination = selected.value
    if (!destination) {
      return
    }

    removing.value = true
    actionError.value = null
    try {
      await backups.removeDestination(destination.id)
      notifications.notify(t('backupDestinations.removed', { name: destination.name }))
      removeOpen.value = false
    } catch (error) {
      actionError.value = error instanceof Error ? error.message : String(error)
      removeOpen.value = false
    } finally {
      removing.value = false
    }
  }
</script>

<template>
  <v-dialog v-model="open" max-width="900" scrollable>
    <v-card v-if="destination" :title="t('restores.points.title', { name: destination.name })">
      <v-card-text>
        <p class="mb-4">{{ t('restores.points.text') }}</p>

        <v-alert
          v-if="loadError"
          class="mb-4"
          density="compact"
          type="error"
          variant="tonal"
        >
          {{ t('restores.points.loadError', { message: loadError }) }}
        </v-alert>

        <v-data-table
          :headers="headers"
          hide-default-footer
          item-value="key"
          :items="sortedPoints"
          :items-per-page="-1"
          :loading="loading"
          :no-data-text="t('restores.points.empty')"
        >
          <template #item.createdAt="{ item }">
            {{ d(new Date(item.createdAt), 'long') }}
          </template>

          <template #item.kind="{ item }">
            {{ t(`backups.kinds.${item.kind}`) }}
          </template>

          <template #item.p4dVersion="{ item }">
            {{ item.p4dVersion ?? '-' }}
          </template>

          <template #item.actions="{ item }">
            <div class="d-flex justify-end">
              <v-btn
                prepend-icon="mdi-backup-restore"
                size="small"
                :text="t('restores.points.restore')"
                variant="text"
                @click="restore(item)"
              />
            </div>
          </template>
        </v-data-table>
      </v-card-text>

      <v-card-actions>
        <v-spacer />
        <v-btn :text="t('actions.close')" @click="open = false" />
      </v-card-actions>
    </v-card>

    <RestoreDialog v-model="restoreOpen" :source="restoreSource" @started="open = false" />
  </v-dialog>
</template>

<script lang="ts" setup>
  // Types
  import type { BackupDestination, BackupPoint } from '@/api/types'
  import type { RestoreSource } from '@/utils/restore'

  // Composables
  import { computed, ref, watch } from 'vue'
  import { useI18n } from 'vue-i18n'

  // Components and stores
  import RestoreDialog from '@/components/backups/RestoreDialog.vue'
  import { useBackupsStore } from '@/stores/backups'

  /**
   * The backup points stored at a destination, read from the destination itself, by instance: also the ones of the
   * instances this daemon never knew, such as after a reinstallation. A point becomes a new instance from here.
   */
  const props = defineProps<{
    destination: BackupDestination | null
  }>()

  const open = defineModel<boolean>({ default: false })

  const { t, d } = useI18n()
  const backups = useBackupsStore()

  const points = ref<BackupPoint[]>([])
  const loading = ref(false)
  const loadError = ref<string | null>(null)
  const restoreOpen = ref(false)
  const restoreSource = ref<RestoreSource | null>(null)

  // By instance, then the newest first, as the daemon lists them.
  const sortedPoints = computed(() => points.value.toSorted((a, b) => a.sourceName.localeCompare(b.sourceName)))

  const headers = computed(() => [
    { title: t('restores.points.instance'), key: 'sourceName', sortable: false },
    { title: t('restores.points.created'), key: 'createdAt', sortable: false },
    { title: t('restores.points.kind'), key: 'kind', sortable: false },
    { title: t('restores.points.checkpoint'), key: 'checkpointNumber', sortable: false },
    { title: t('restores.points.journal'), key: 'journalNumber', sortable: false },
    { title: t('restores.points.p4d'), key: 'p4dVersion', sortable: false },
    { title: '', key: 'actions', sortable: false, align: 'end' as const },
  ])

  watch(open, value => {
    if (value) {
      void load()
    }
  }, { immediate: true })

  async function load () {
    const destination = props.destination
    if (!destination) {
      return
    }

    loading.value = true
    loadError.value = null
    points.value = []
    try {
      points.value = await backups.listPoints(destination.id)
    } catch (error) {
      loadError.value = error instanceof Error ? error.message : String(error)
    } finally {
      loading.value = false
    }
  }

  function restore (point: BackupPoint) {
    const destination = props.destination
    if (!destination) {
      return
    }
    restoreSource.value = {
      destinationId: destination.id,
      destinationName: destination.name,
      snapshotKey: point.key,
      sourceName: point.sourceName,
      createdAt: point.createdAt,
      kind: point.kind,
    }
    restoreOpen.value = true
  }
</script>

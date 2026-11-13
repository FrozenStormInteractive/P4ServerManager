<template>
  <v-dialog v-model="open" max-width="680" scrollable>
    <v-card v-if="run" :title="t('backups.run.title', { date: d(new Date(run.startedAt), 'long') })">
      <v-card-text>
        <div class="d-flex flex-wrap ga-2 mb-4">
          <v-chip
            :color="getBackupStatusLook(run.status).color"
            label
            :prepend-icon="getBackupStatusLook(run.status).icon"
            size="small"
            :text="t(`backups.statuses.${run.status}`)"
          />

          <v-chip label size="small" :text="t(`backups.kinds.${run.kind}`)" />
        </div>

        <v-list density="compact">
          <v-list-item
            v-if="run.checkpointNumber !== null"
            :subtitle="String(run.checkpointNumber)"
            :title="t('backups.run.checkpoint')"
          />

          <v-list-item
            v-if="run.journalNumber !== null"
            :subtitle="String(run.journalNumber)"
            :title="t('backups.run.journal')"
          />

          <v-list-item
            :subtitle="t('backups.run.filesValue', { count: run.fileCount, size: formatBytes(run.totalBytes, locale) })"
            :title="t('backups.run.files')"
          />
        </v-list>

        <v-alert
          v-if="run.error"
          class="mb-4"
          density="compact"
          type="error"
          variant="tonal"
        >
          {{ run.error }}
        </v-alert>

        <v-alert
          v-for="warning in run.warnings"
          :key="warning"
          class="mb-4"
          data-testid="run-warning"
          density="compact"
          type="warning"
          variant="tonal"
        >
          {{ warning }}
        </v-alert>

        <v-alert
          v-if="actionError"
          class="mb-4"
          closable
          density="compact"
          type="error"
          variant="tonal"
          @click:close="actionError = null"
        >
          {{ actionError }}
        </v-alert>

        <v-card
          v-for="destination in run.destinations"
          :key="destination.destinationId"
          class="mb-3"
          :data-destination="destination.destinationName"
          variant="outlined"
        >
          <v-card-item>
            <template #prepend>
              <v-icon
                :color="getBackupDestinationStatusLook(destination.status).color"
                :icon="getBackupDestinationStatusLook(destination.status).icon"
              />
            </template>

            <v-card-title class="text-title-small">{{ destination.destinationName }}</v-card-title>

            <v-card-subtitle class="text-wrap">
              <template v-if="destination.status === 'Succeeded'">
                {{ t('backups.run.uploaded', {
                  count: destination.uploadedObjects,
                  size: formatBytes(destination.uploadedBytes, locale),
                }) }}
              </template>

              <template v-else>
                {{ t(`backups.destinationStatuses.${destination.status}`) }}
              </template>
            </v-card-subtitle>
          </v-card-item>

          <v-card-text v-if="destination.error || destination.lastVerification" class="pt-0">
            <p v-if="destination.error" class="text-error mb-2">{{ destination.error }}</p>

            <template v-if="destination.lastVerification">
              <p class="mb-1" data-testid="verification">{{ describeVerification(destination.lastVerification) }}</p>

              <ul v-if="destination.lastVerification.problems.length > 0" class="text-body-small ml-4">
                <li v-for="problem in destination.lastVerification.problems" :key="problem">{{ problem }}</li>
              </ul>
            </template>
          </v-card-text>

          <v-card-actions v-if="destination.snapshotKey">
            <v-btn
              :disabled="!canVerify"
              :loading="verifying === destination.destinationId"
              prepend-icon="mdi-check-decagram-outline"
              :text="t('backups.run.verify')"
              variant="text"
              @click="verify(destination, false)"
            />

            <v-btn
              :disabled="!canVerify"
              prepend-icon="mdi-magnify-scan"
              :text="t('backups.run.verifyDeep')"
              variant="text"
              @click="verify(destination, true)"
            />

            <v-spacer />

            <v-btn
              v-if="instance"
              color="error"
              :disabled="!canVerify"
              prepend-icon="mdi-backup-restore"
              :text="t('restores.restore')"
              variant="text"
              @click="restore(destination)"
            />
          </v-card-actions>
        </v-card>

        <p class="text-body-small text-medium-emphasis mb-0">{{ t('backups.run.verifyHint') }}</p>
      </v-card-text>

      <v-card-actions>
        <v-spacer />
        <v-btn :text="t('actions.close')" @click="open = false" />
      </v-card-actions>
    </v-card>

    <RestoreDialog
      v-model="restoreOpen"
      :instance="instance"
      :source="restoreSource"
      @started="open = false"
    />
  </v-dialog>
</template>

<script lang="ts" setup>
  // Types
  import type { BackupRun, BackupRunDestination, BackupVerification, Instance } from '@/api/types'
  import type { RestoreSource } from '@/utils/restore'

  // Composables
  import { ref } from 'vue'
  import { useI18n } from 'vue-i18n'

  // Components, stores and utilities
  import RestoreDialog from '@/components/backups/RestoreDialog.vue'
  import { useBackupsStore } from '@/stores/backups'
  import { useNotificationsStore } from '@/stores/notifications'
  import { getBackupDestinationStatusLook, getBackupStatusLook } from '@/utils/backupStatus'
  import { formatBytes } from '@/utils/bytes'

  /**
   * What a backup did at each of its destinations, and the verification of its points. A verification runs in the
   * daemon: its outcome comes back with the backup. A point is restored from here, over the instance or as a new one.
   */
  const props = defineProps<{
    run: BackupRun | null
    canVerify: boolean
    /**
     * The instance the backup was made from, whose data a point can replace.
     */
    instance?: Instance | null
  }>()

  const open = defineModel<boolean>({ default: false })

  const { t, d, locale } = useI18n()
  const backups = useBackupsStore()
  const notifications = useNotificationsStore()

  const verifying = ref<string | null>(null)
  const actionError = ref<string | null>(null)
  const restoreOpen = ref(false)
  const restoreSource = ref<RestoreSource | null>(null)

  function restore (destination: BackupRunDestination) {
    const run = props.run
    if (!run || !destination.snapshotKey) {
      return
    }
    restoreSource.value = {
      destinationId: destination.destinationId,
      destinationName: destination.destinationName,
      snapshotKey: destination.snapshotKey,
      sourceName: run.instanceName,
      createdAt: run.startedAt,
      kind: run.kind,
    }
    restoreOpen.value = true
  }

  function describeVerification (verification: BackupVerification): string {
    const date = d(new Date(verification.verifiedAt), 'long')
    if (verification.error) {
      return t('backups.run.verificationError', { date, error: verification.error })
    }
    if (verification.succeeded) {
      return t(verification.deep ? 'backups.run.verifiedDeep' : 'backups.run.verified', {
        date,
        count: verification.checkedObjects,
      })
    }
    return t('backups.run.problems', {
      date,
      problems: verification.problemCount,
      count: verification.checkedObjects,
    })
  }

  async function verify (destination: BackupRunDestination, deep: boolean) {
    const run = props.run
    if (!run) {
      return
    }

    verifying.value = destination.destinationId
    actionError.value = null
    try {
      await backups.verify(run.instanceId, run.id, destination.destinationId, deep)
      notifications.notify(t('backups.run.verifyStarted', { name: destination.destinationName }), 'info')
    } catch (error) {
      actionError.value = error instanceof Error ? error.message : String(error)
    } finally {
      verifying.value = null
    }
  }
</script>

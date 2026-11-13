// Types
import type { BackupDestinationStatus, BackupRunStatus, RestoreRunStatus } from '@/api/types'

export interface StatusLook {
  color: string
  icon: string
}

const runLooks: Record<BackupRunStatus, StatusLook> = {
  Running: { color: 'info', icon: 'mdi-progress-upload' },
  Succeeded: { color: 'success', icon: 'mdi-check-circle' },
  PartiallySucceeded: { color: 'warning', icon: 'mdi-alert' },
  Failed: { color: 'error', icon: 'mdi-alert-circle' },
  Canceled: { color: 'grey', icon: 'mdi-cancel' },
  Interrupted: { color: 'warning', icon: 'mdi-power-plug-off' },
}

const destinationLooks: Record<BackupDestinationStatus, StatusLook> = {
  Pending: { color: 'grey', icon: 'mdi-clock-outline' },
  Uploading: { color: 'info', icon: 'mdi-progress-upload' },
  Succeeded: { color: 'success', icon: 'mdi-check' },
  Failed: { color: 'error', icon: 'mdi-alert-circle' },
}

const restoreLooks: Record<RestoreRunStatus, StatusLook> = {
  Running: { color: 'info', icon: 'mdi-progress-download' },
  Succeeded: { color: 'success', icon: 'mdi-check-circle' },
  SucceededWithProblems: { color: 'warning', icon: 'mdi-alert' },
  Failed: { color: 'error', icon: 'mdi-alert-circle' },
  Canceled: { color: 'grey', icon: 'mdi-cancel' },
  Interrupted: { color: 'warning', icon: 'mdi-power-plug-off' },
}

/**
 * Color and icon of the status of a restore.
 */
export function getRestoreStatusLook (status: RestoreRunStatus): StatusLook {
  return restoreLooks[status]
}

/**
 * Color and icon of the status of a backup.
 */
export function getBackupStatusLook (status: BackupRunStatus): StatusLook {
  return runLooks[status]
}

/**
 * Color and icon of what a backup did at one destination.
 */
export function getBackupDestinationStatusLook (status: BackupDestinationStatus): StatusLook {
  return destinationLooks[status]
}

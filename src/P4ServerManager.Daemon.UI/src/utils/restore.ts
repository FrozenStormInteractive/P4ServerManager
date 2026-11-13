// Types
import type { BackupKind } from '@/api/types'

/**
 * A backup point to restore, wherever it was picked: from the history of an instance or from a destination.
 */
export interface RestoreSource {
  destinationId: string
  destinationName: string
  snapshotKey: string
  sourceName: string
  createdAt: string
  kind: BackupKind
}

// Types
import type {
  BackupDestination,
  BackupDestinationTestResult,
  BackupKind,
  BackupPoint,
  BackupRun,
  CreateBackupDestinationRequest,
  Instance,
  RestoreInstanceRequest,
  RestoreRun,
  UpdateBackupDestinationRequest,
} from './types'

// Utilities
import { request } from './client'

const destinationsUrl = '/api/backup-destinations'

function destinationUrl (id: string): string {
  return `${destinationsUrl}/${encodeURIComponent(id)}`
}

function instanceUrl (instanceId: string): string {
  return `/api/instances/${encodeURIComponent(instanceId)}`
}

export function listBackupDestinations (): Promise<BackupDestination[]> {
  return request('GET', destinationsUrl)
}

/**
 * Adds a destination. The daemon creates its repository, or attaches the one already there when the passphrase opens
 * it.
 */
export function createBackupDestination (body: CreateBackupDestinationRequest): Promise<BackupDestination> {
  return request('POST', destinationsUrl, body)
}

export function updateBackupDestination (id: string, body: UpdateBackupDestinationRequest): Promise<BackupDestination> {
  return request('PUT', destinationUrl(id), body)
}

/**
 * Removes a destination from the daemon. Its backups stay on the storage.
 */
export async function deleteBackupDestination (id: string): Promise<void> {
  await request('DELETE', destinationUrl(id))
}

export function testBackupDestination (id: string): Promise<BackupDestinationTestResult> {
  return request('POST', `${destinationUrl(id)}/test`)
}

export async function getInstanceBackupDestinations (instanceId: string): Promise<string[]> {
  const body = await request<{ destinationIds: string[] }>('GET', `${instanceUrl(instanceId)}/backup-destinations`)
  return body.destinationIds
}

export async function setInstanceBackupDestinations (instanceId: string, destinationIds: string[]): Promise<string[]> {
  const body = await request<{ destinationIds: string[] }>(
    'PUT',
    `${instanceUrl(instanceId)}/backup-destinations`,
    { destinationIds },
  )
  return body.destinationIds
}

/**
 * The backups of an instance, the latest first.
 */
export function listBackups (instanceId: string): Promise<BackupRun[]> {
  return request('GET', `${instanceUrl(instanceId)}/backups`)
}

/**
 * Begins a backup. The daemon answers once it has begun, and the hub tells its progress.
 */
export function startBackup (instanceId: string, kind: BackupKind, destinationIds?: string[]): Promise<BackupRun> {
  return request('POST', `${instanceUrl(instanceId)}/backups`, { kind, destinationIds: destinationIds ?? null })
}

export async function cancelBackup (instanceId: string, runId: string): Promise<void> {
  await request('POST', `${instanceUrl(instanceId)}/backups/${encodeURIComponent(runId)}/cancel`)
}

/**
 * Begins the verification of a backup at one of its destinations. The outcome comes with the backup, through the hub.
 */
export function verifyBackup (instanceId: string, runId: string, destinationId: string, deep: boolean): Promise<BackupRun> {
  return request(
    'POST',
    `${instanceUrl(instanceId)}/backups/${encodeURIComponent(runId)}/verify`,
    { destinationId, deep },
  )
}

/**
 * The backup points stored at a destination, newest first, read from the destination itself.
 */
export function listBackupPoints (destinationId: string, sourceId?: string): Promise<BackupPoint[]> {
  const query = sourceId ? `?sourceId=${encodeURIComponent(sourceId)}` : ''
  return request('GET', `${destinationUrl(destinationId)}/points${query}`)
}

/**
 * The restores of an instance, the latest first.
 */
export function listRestores (instanceId: string): Promise<RestoreRun[]> {
  return request('GET', `${instanceUrl(instanceId)}/restores`)
}

/**
 * Begins to replace the data of an instance with a backup point. The hub tells its progress.
 */
export function startRestore (instanceId: string, destinationId: string, snapshotKey: string): Promise<RestoreRun> {
  return request('POST', `${instanceUrl(instanceId)}/restores`, { destinationId, snapshotKey })
}

export async function cancelRestore (instanceId: string, runId: string): Promise<void> {
  await request('POST', `${instanceUrl(instanceId)}/restores/${encodeURIComponent(runId)}/cancel`)
}

/**
 * Makes a new instance from a backup point. The daemon answers once the instance is created; its restore runs as its
 * provisioning.
 */
export function restoreInstance (body: RestoreInstanceRequest): Promise<Instance> {
  return request('POST', '/api/instances/restore', body)
}

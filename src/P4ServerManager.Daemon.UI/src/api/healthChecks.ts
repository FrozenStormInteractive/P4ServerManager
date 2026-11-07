// Types
import type { HealthCheckKind, HealthCheckRun } from './types'

// Utilities
import { request } from './client'

function checksUrl (instanceId: string): string {
  return `/api/instances/${encodeURIComponent(instanceId)}/health-checks`
}

/**
 * The health checks of the instance, with what they found, the latest first.
 */
export function listHealthChecks (instanceId: string): Promise<HealthCheckRun[]> {
  return request('GET', checksUrl(instanceId))
}

/**
 * Begins a health check. The daemon answers once it has begun, and the hub tells its progress.
 */
export function startHealthCheck (instanceId: string, checks: HealthCheckKind[]): Promise<HealthCheckRun> {
  return request('POST', checksUrl(instanceId), { checks })
}

/**
 * Asks a running health check to stop. It ends shortly after, and keeps what it found until then.
 */
export async function cancelHealthCheck (instanceId: string, runId: string): Promise<void> {
  await request('POST', `${checksUrl(instanceId)}/${encodeURIComponent(runId)}/cancel`)
}

export async function deleteHealthCheck (instanceId: string, runId: string): Promise<void> {
  await request('DELETE', `${checksUrl(instanceId)}/${encodeURIComponent(runId)}`)
}

/**
 * Where to download the fix journal of a health check.
 */
export function getFixJournalUrl (instanceId: string, runId: string): string {
  return `${checksUrl(instanceId)}/${encodeURIComponent(runId)}/fix-journal`
}

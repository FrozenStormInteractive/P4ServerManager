// Types
import type {
  CreateInstanceRequest,
  Instance,
  InstanceAction,
  InstanceActionResult,
  InstanceSelection,
  OperationSnapshot,
  UpdateInstanceRequest,
  UpgradeInstanceRequest,
} from './types'

// Utilities
import { ApiError, request } from './client'

export function listInstances (): Promise<Instance[]> {
  return request('GET', '/api/instances')
}

export function createInstance (body: CreateInstanceRequest): Promise<Instance> {
  return request('POST', '/api/instances', body)
}

export function updateInstance (id: string, body: UpdateInstanceRequest): Promise<Instance> {
  return request('PATCH', `/api/instances/${encodeURIComponent(id)}`, body)
}

/**
 * Progress of the last long operation on the instance, or null when the daemon has run none since it started.
 */
export async function getOperation (id: string): Promise<OperationSnapshot | null> {
  try {
    return await request<OperationSnapshot>('GET', `/api/instances/${encodeURIComponent(id)}/operation`)
  } catch (error) {
    if (error instanceof ApiError && error.status === 404) {
      return null
    }
    throw error
  }
}

export function startInstance (id: string): Promise<Instance> {
  return request('POST', `/api/instances/${encodeURIComponent(id)}/start`)
}

export function stopInstance (id: string): Promise<Instance> {
  return request('POST', `/api/instances/${encodeURIComponent(id)}/stop`)
}

/**
 * Deletes the instance. With `deleteFiles`, its logs and the root the daemon chose for it are deleted too; a root
 * directory given at creation is always kept.
 */
export async function deleteInstance (id: string, deleteFiles: boolean): Promise<void> {
  await request('DELETE', `/api/instances/${encodeURIComponent(id)}?deleteFiles=${deleteFiles}`)
}

export function restartInstance (id: string): Promise<Instance> {
  return request('POST', `/api/instances/${encodeURIComponent(id)}/restart`)
}

/**
 * Enables the maintenance mode of the instance. The daemon answers once p4d runs in it: restarted if it ran, started if it
 * was stopped.
 */
export function enableMaintenanceMode (id: string): Promise<Instance> {
  return request('POST', `/api/instances/${encodeURIComponent(id)}/maintenance/enable`)
}

/**
 * Disables the maintenance mode of the instance. A running p4d is restarted in normal mode; a stopped one stays stopped.
 */
export function disableMaintenanceMode (id: string): Promise<Instance> {
  return request('POST', `/api/instances/${encodeURIComponent(id)}/maintenance/disable`)
}

/**
 * Begins the upgrade of the instance to the latest build of a release. The daemon answers once it has begun: the
 * instance is `Upgrading` until it ends, and its operation tells its progress.
 */
export function upgradeInstance (id: string, body: UpgradeInstanceRequest): Promise<Instance> {
  return request('POST', `/api/instances/${encodeURIComponent(id)}/upgrade`, body)
}

/**
 * Begins the conversion of the instance to Unicode mode. The daemon answers once it has begun: the instance is
 * `ConvertingToUnicode` until it ends, and its operation tells its progress.
 */
export function convertInstanceToUnicode (id: string): Promise<Instance> {
  return request('POST', `/api/instances/${encodeURIComponent(id)}/convert-to-unicode`)
}

/**
 * Starts, stops or restarts several instances in parallel. The daemon answers once they are all done, with one result
 * per instance, by name: one failure does not stop the others.
 */
export function runInstancesAction (action: InstanceAction, selection: InstanceSelection): Promise<InstanceActionResult[]> {
  return request('POST', `/api/instances/${action}`, selection)
}

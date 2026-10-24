// Types
import type { CreateInstanceRequest, Instance, OperationSnapshot } from './types'

// Utilities
import { ApiError, request } from './client'

export function listInstances (): Promise<Instance[]> {
  return request('GET', '/api/instances')
}

export function createInstance (body: CreateInstanceRequest): Promise<Instance> {
  return request('POST', '/api/instances', body)
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

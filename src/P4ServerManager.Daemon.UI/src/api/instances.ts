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

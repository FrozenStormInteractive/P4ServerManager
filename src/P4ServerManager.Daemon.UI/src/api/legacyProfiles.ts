// Types
import type { ImportLegacyProfileRequest, Instance, LegacyProfile } from './types'

// Utilities
import { request } from './client'

/**
 * Server profiles of the HelixCoreServer Synology package found by the daemon, by file name.
 */
export function listLegacyProfiles (): Promise<LegacyProfile[]> {
  return request('GET', '/api/legacy-profiles')
}

/**
 * Imports the server of a profile as an instance. The import runs in the background, like a provisioning.
 */
export function importLegacyProfile (id: string, body: ImportLegacyProfileRequest): Promise<Instance> {
  return request('POST', `/api/legacy-profiles/${encodeURIComponent(id)}/import`, body)
}

/**
 * Hides a profile for good. Its server is left untouched.
 */
export async function dismissLegacyProfile (id: string): Promise<void> {
  await request('DELETE', `/api/legacy-profiles/${encodeURIComponent(id)}`)
}

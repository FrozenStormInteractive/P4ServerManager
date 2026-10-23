// Types
import type { P4dVersion } from './types'

// Utilities
import { request } from './client'

/**
 * p4d releases available for the platform of the daemon, newest first.
 */
export function listVersions (): Promise<P4dVersion[]> {
  return request('GET', '/api/p4d/versions')
}

// Types
import type { DirectoryListing } from './types'

// Utilities
import { request } from './client'

/**
 * Subfolders of a folder of the machine of the daemon. Without a path, those of the first root of the file system.
 * Nothing is created on the machine: a folder picked that does not exist yet is created by the service it is given to.
 */
export function listDirectories (path?: string | null): Promise<DirectoryListing> {
  const query = path ? `?path=${encodeURIComponent(path)}` : ''
  return request('GET', `/api/filesystem/directories${query}`)
}

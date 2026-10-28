// Types
import type { Build, BuildArchivePolicy, BuildArchiveStatus } from './types'

// Utilities
import { request, upload } from './client'

/**
 * Builds of the archive, newest first.
 */
export function listBuilds (): Promise<Build[]> {
  return request('GET', '/api/p4d/builds')
}

/**
 * Imports a build from p4d and p4, or from the server package of Perforce that holds them. The daemon compresses it
 * before it answers.
 */
export function importBuild (files: { p4d: File, p4: File } | { package: File }): Promise<Build> {
  const form = new FormData()
  for (const [field, file] of Object.entries(files)) {
    form.append(field, file, file.name)
  }
  return upload('/api/p4d/builds/import', form)
}

/**
 * Queues the download of the current build of a release from the Perforce FTP.
 */
export function downloadBuild (release: string): Promise<void> {
  return request('POST', '/api/p4d/builds/download', { release })
}

export function updateBuild (id: string, body: { protected?: boolean }): Promise<Build> {
  return request('PATCH', `/api/p4d/builds/${encodeURIComponent(id)}`, body)
}

export function deleteBuild (id: string): Promise<void> {
  return request('DELETE', `/api/p4d/builds/${encodeURIComponent(id)}`)
}

export function getArchivePolicy (): Promise<BuildArchivePolicy> {
  return request('GET', '/api/p4d/archive/policy')
}

export function updateArchivePolicy (policy: BuildArchivePolicy): Promise<BuildArchivePolicy> {
  return request('PUT', '/api/p4d/archive/policy', policy)
}

export function getArchiveStatus (): Promise<BuildArchiveStatus> {
  return request('GET', '/api/p4d/archive/status')
}

/**
 * Queues a check of the Perforce FTP for new builds of the releases of the policy.
 */
export function checkArchive (): Promise<void> {
  return request('POST', '/api/p4d/archive/check')
}

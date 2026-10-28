// Types
import type { BuildsHub } from '@/api/hub'
import type { Build, BuildArchivePolicy, BuildArchiveStatus } from '@/api/types'

// Utilities
import { defineStore } from 'pinia'
import { computed, ref } from 'vue'

// API
import * as api from '@/api/builds'
import { connectBuildsHub } from '@/api/hub'

/**
 * The builds of the archive and the status of the archive worker, kept up to date by the `/hubs/builds` hub while a
 * page that shows them is open.
 */
export const useBuildsStore = defineStore('builds', () => {
  const builds = ref<Record<string, Build>>({})
  const status = ref<BuildArchiveStatus | null>(null)
  const loaded = ref(false)
  const error = ref<string | null>(null)
  let hub: BuildsHub | null = null

  /**
   * Newest first: by version, then by build number.
   */
  const sortedBuilds = computed(() => Object.values(builds.value).toSorted((a, b) =>
    b.version.localeCompare(a.version, undefined, { numeric: true })
    || Number(b.build) - Number(a.build)))

  function upsertBuild (build: Build) {
    builds.value[build.id] = build
  }

  async function fetchAll () {
    try {
      const [list, current] = await Promise.all([api.listBuilds(), api.getArchiveStatus()])
      builds.value = Object.fromEntries(list.map(build => [build.id, build]))
      status.value = current
      loaded.value = true
      error.value = null
    } catch (fetchError) {
      error.value = fetchError instanceof Error ? fetchError.message : String(fetchError)
    }
  }

  async function importBuild (files: { p4d: File, p4: File } | { package: File }): Promise<Build> {
    const build = await api.importBuild(files)
    upsertBuild(build)
    return build
  }

  async function setProtected (id: string, value: boolean) {
    upsertBuild(await api.updateBuild(id, { protected: value }))
  }

  async function remove (id: string) {
    await api.deleteBuild(id)
    delete builds.value[id]
  }

  async function savePolicy (policy: BuildArchivePolicy): Promise<BuildArchivePolicy> {
    const saved = await api.updateArchivePolicy(policy)
    status.value = await api.getArchiveStatus()
    return saved
  }

  function connect () {
    hub ??= connectBuildsHub({
      onBuildUpdated: upsertBuild,
      onBuildDeleted: id => {
        delete builds.value[id]
      },
      onArchiveStatusChanged: value => {
        status.value = value
      },
      onConnected: () => void fetchAll(),
    })
  }

  async function disconnect () {
    const current = hub
    hub = null
    await current?.stop()
  }

  return {
    builds,
    status,
    loaded,
    error,
    sortedBuilds,
    upsertBuild,
    fetchAll,
    importBuild,
    setProtected,
    remove,
    savePolicy,
    connect,
    disconnect,
  }
})

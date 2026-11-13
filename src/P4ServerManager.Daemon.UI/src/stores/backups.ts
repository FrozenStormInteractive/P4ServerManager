// Types
import type {
  BackupDestination,
  BackupKind,
  BackupRun,
  CreateBackupDestinationRequest,
  UpdateBackupDestinationRequest,
} from '@/api/types'

// Utilities
import { defineStore } from 'pinia'
import { ref } from 'vue'

// API
import * as api from '@/api/backups'

/**
 * The answer of a request can come after a push of the hub that is newer than it. A backup that ended never runs
 * again, so a snapshot that says it runs is an old one.
 */
function isOutdated (known: BackupRun | undefined, incoming: BackupRun): boolean {
  return !!known && known.status !== 'Running' && incoming.status === 'Running'
}

/**
 * The backup destinations of the daemon, and the backups of the instances that were looked at, kept up to date by the
 * `/hubs/instances` hub. The instances store listens to the hub, and hands the backups to this store.
 */
export const useBackupsStore = defineStore('backups', () => {
  const destinations = ref<BackupDestination[]>([])
  const destinationsLoaded = ref(false)
  const destinationsError = ref<string | null>(null)

  const runs = ref<Record<string, BackupRun[]>>({})
  const runsLoaded = ref<Record<string, boolean>>({})
  const runsErrors = ref<Record<string, string | null>>({})
  const instanceDestinations = ref<Record<string, string[]>>({})
  const watched = new Set<string>()

  function getRuns (instanceId: string): BackupRun[] {
    return runs.value[instanceId] ?? []
  }

  function getRunningRun (instanceId: string): BackupRun | null {
    return getRuns(instanceId).find(run => run.status === 'Running') ?? null
  }

  function getInstanceDestinations (instanceId: string): string[] {
    return instanceDestinations.value[instanceId] ?? []
  }

  async function fetchDestinations () {
    try {
      destinations.value = await api.listBackupDestinations()
      destinationsLoaded.value = true
      destinationsError.value = null
    } catch (error) {
      destinationsError.value = error instanceof Error ? error.message : String(error)
    }
  }

  async function createDestination (body: CreateBackupDestinationRequest): Promise<BackupDestination> {
    const destination = await api.createBackupDestination(body)
    await fetchDestinations()
    return destination
  }

  async function updateDestination (id: string, body: UpdateBackupDestinationRequest): Promise<BackupDestination> {
    const destination = await api.updateBackupDestination(id, body)
    await fetchDestinations()
    return destination
  }

  async function removeDestination (id: string) {
    await api.deleteBackupDestination(id)
    destinations.value = destinations.value.filter(destination => destination.id !== id)
  }

  function testDestination (id: string) {
    return api.testBackupDestination(id)
  }

  function setRun (run: BackupRun) {
    if (isOutdated(getRuns(run.instanceId).find(other => other.id === run.id), run)) {
      return
    }
    const others = getRuns(run.instanceId).filter(other => other.id !== run.id)
    runs.value[run.instanceId] = [...others, run].toSorted((a, b) => b.startedAt.localeCompare(a.startedAt))
  }

  function removeInstance (instanceId: string) {
    watched.delete(instanceId)
    delete runs.value[instanceId]
    delete runsLoaded.value[instanceId]
    delete runsErrors.value[instanceId]
    delete instanceDestinations.value[instanceId]
  }

  async function fetchRuns (instanceId: string) {
    watched.add(instanceId)
    try {
      const [fetched, selected] = await Promise.all([
        api.listBackups(instanceId),
        api.getInstanceBackupDestinations(instanceId),
      ])
      const known = getRuns(instanceId)
      runs.value[instanceId] = fetched.map(run => {
        const local = known.find(other => other.id === run.id)
        return local && isOutdated(local, run) ? local : run
      })
      instanceDestinations.value[instanceId] = selected
      runsLoaded.value[instanceId] = true
      runsErrors.value[instanceId] = null
    } catch (error) {
      runsErrors.value[instanceId] = error instanceof Error ? error.message : String(error)
    }
  }

  /**
   * Reads again the instances already looked at: the hub may have missed events while it was disconnected.
   */
  async function refresh () {
    await Promise.all([...watched].map(instanceId => fetchRuns(instanceId)))
  }

  async function setInstanceDestinations (instanceId: string, destinationIds: string[]) {
    instanceDestinations.value[instanceId] = await api.setInstanceBackupDestinations(instanceId, destinationIds)
    await fetchDestinations()
  }

  async function start (instanceId: string, kind: BackupKind, destinationIds?: string[]) {
    setRun(await api.startBackup(instanceId, kind, destinationIds))
  }

  async function cancel (instanceId: string, runId: string) {
    await api.cancelBackup(instanceId, runId)
  }

  async function verify (instanceId: string, runId: string, destinationId: string, deep: boolean) {
    await api.verifyBackup(instanceId, runId, destinationId, deep)
  }

  return {
    destinations,
    destinationsLoaded,
    destinationsError,
    runs,
    runsLoaded,
    runsErrors,
    instanceDestinations,
    getRuns,
    getRunningRun,
    getInstanceDestinations,
    fetchDestinations,
    createDestination,
    updateDestination,
    removeDestination,
    testDestination,
    setRun,
    removeInstance,
    fetchRuns,
    refresh,
    setInstanceDestinations,
    start,
    cancel,
    verify,
  }
})

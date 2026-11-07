// Types
import type { HealthCheckFinding, HealthCheckKind, HealthCheckRun } from '@/api/types'

// Utilities
import { defineStore } from 'pinia'
import { ref } from 'vue'

// API
import * as api from '@/api/healthChecks'

/**
 * What the reports tell of a database.
 */
export interface InstanceHealth {
  status: 'unknown' | 'healthy' | 'problems'
  /**
   * The problems of the latest result of each check.
   */
  findings: HealthCheckFinding[]
  /**
   * The latest run that tells something of the database.
   */
  latestRun: HealthCheckRun | null
  /**
   * The run whose fix journal goes with the findings.
   */
  fixJournalRun: HealthCheckRun | null
  /**
   * The checks no run has told anything about yet.
   */
  uncheckedChecks: HealthCheckKind[]
}

const allChecks: HealthCheckKind[] = ['Validate', 'CrossCheck']

/**
 * The answer of a request can come after a push of the hub that is newer than it. A run that ended never runs again, so
 * a snapshot that says so is an old one, whichever comes last.
 */
function isOutdated (known: HealthCheckRun | undefined, incoming: HealthCheckRun): boolean {
  return !!known && known.status !== 'Running' && incoming.status === 'Running'
}

/**
 * The health checks of the instances that were looked at, kept up to date by the `/hubs/instances` hub. The instances
 * store listens to the hub, and hands the changes to this store.
 */
export const useHealthChecksStore = defineStore('healthChecks', () => {
  const runs = ref<Record<string, HealthCheckRun[]>>({})
  const loaded = ref<Record<string, boolean>>({})
  const errors = ref<Record<string, string | null>>({})
  const watched = new Set<string>()

  function getRuns (instanceId: string): HealthCheckRun[] {
    return runs.value[instanceId] ?? []
  }

  function getRunningRun (instanceId: string): HealthCheckRun | null {
    return getRuns(instanceId).find(run => run.status === 'Running') ?? null
  }

  /**
   * The health of an instance, from the latest result of each check: a table validation that finds nothing says nothing
   * of the inconsistencies a consistency check found before it. A run that could not run to its end says nothing either.
   */
  function getHealth (instanceId: string): InstanceHealth {
    const conclusive = getRuns(instanceId).filter(run => run.status === 'Healthy' || run.status === 'ProblemsFound')
    const latestOf = (check: HealthCheckKind) => conclusive.find(run => run.checks.includes(check))
    const findings = allChecks.flatMap(check =>
      latestOf(check)?.findings.filter(finding => finding.check === check) ?? [],
    )
    const crossCheck = latestOf('CrossCheck')

    return {
      status: conclusive.length === 0 ? 'unknown' : (findings.length > 0 ? 'problems' : 'healthy'),
      findings,
      latestRun: conclusive[0] ?? null,
      fixJournalRun: crossCheck?.hasFixJournal ? crossCheck : null,
      uncheckedChecks: allChecks.filter(check => !latestOf(check)),
    }
  }

  function setRun (run: HealthCheckRun) {
    if (isOutdated(getRuns(run.instanceId).find(other => other.id === run.id), run)) {
      return
    }
    const others = getRuns(run.instanceId).filter(other => other.id !== run.id)
    runs.value[run.instanceId] = [...others, run].toSorted((a, b) => b.startedAt.localeCompare(a.startedAt))
  }

  function removeRun (instanceId: string, runId: string) {
    runs.value[instanceId] = getRuns(instanceId).filter(run => run.id !== runId)
  }

  function removeInstance (instanceId: string) {
    watched.delete(instanceId)
    delete runs.value[instanceId]
    delete loaded.value[instanceId]
    delete errors.value[instanceId]
  }

  async function fetchRuns (instanceId: string) {
    watched.add(instanceId)
    try {
      const fetched = await api.listHealthChecks(instanceId)
      const known = getRuns(instanceId)
      runs.value[instanceId] = fetched.map(run => {
        const local = known.find(other => other.id === run.id)
        return local && isOutdated(local, run) ? local : run
      })
      loaded.value[instanceId] = true
      errors.value[instanceId] = null
    } catch (error) {
      errors.value[instanceId] = error instanceof Error ? error.message : String(error)
    }
  }

  /**
   * Reads again the instances already looked at: the hub may have missed events while it was disconnected.
   */
  async function refresh () {
    await Promise.all([...watched].map(instanceId => fetchRuns(instanceId)))
  }

  async function start (instanceId: string, checks: HealthCheckKind[]) {
    setRun(await api.startHealthCheck(instanceId, checks))
  }

  async function cancel (instanceId: string, runId: string) {
    await api.cancelHealthCheck(instanceId, runId)
  }

  /**
   * Deletes a report. It leaves the list of reports, whether the daemon deletes it or keeps it hidden for a while.
   */
  async function remove (instanceId: string, runId: string) {
    await api.deleteHealthCheck(instanceId, runId)

    // The daemon may only have hidden it: it still tells the health of the instance.
    await fetchRuns(instanceId)
  }

  return {
    runs,
    loaded,
    errors,
    getRuns,
    getRunningRun,
    getHealth,
    setRun,
    removeRun,
    removeInstance,
    fetchRuns,
    refresh,
    start,
    cancel,
    remove,
  }
})

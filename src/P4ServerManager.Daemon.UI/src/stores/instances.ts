// Types
import type { InstancesHub } from '@/api/hub'
import type {
  BackupRun,
  CreateInstanceRequest,
  HealthCheckRun,
  Instance,
  InstanceAction,
  InstanceActionResult,
  InstanceSelection,
  OperationSnapshot,
  RestoreRun,
  SetSuperUserRequest,
  UpdateInstanceRequest,
} from '@/api/types'

// Utilities
import { defineStore } from 'pinia'
import { computed, ref } from 'vue'

// API, plugins and stores
import { connectInstancesHub } from '@/api/hub'
import * as api from '@/api/instances'
import i18n from '@/plugins/i18n'
import { useBackupsStore } from '@/stores/backups'
import { useHealthChecksStore } from '@/stores/healthChecks'
import { useNotificationsStore } from '@/stores/notifications'
import { formatPort } from '@/utils/instancePort'

/**
 * The instances of the daemon, kept up to date by the `/hubs/instances` hub.
 */
export const useInstancesStore = defineStore('instances', () => {
  const instances = ref<Record<string, Instance>>({})
  const operations = ref<Record<string, OperationSnapshot>>({})
  const loaded = ref(false)
  const loading = ref(false)
  const error = ref<string | null>(null)
  let hub: InstancesHub | null = null
  // Number of updates the hub pushed for each instance. A response that arrives after a push is older than it: an
  // operation may end, and the hub tell it, before the response that began it reaches the page.
  const pushes: Record<string, number> = {}

  const sortedInstances = computed(() =>
    Object.values(instances.value).toSorted((a, b) => a.name.localeCompare(b.name)),
  )

  function getInstance (id: string): Instance | undefined {
    return instances.value[id]
  }

  function upsertInstance (instance: Instance) {
    instances.value[instance.id] = instance
  }

  function applyPushedInstance (instance: Instance) {
    pushes[instance.id] = (pushes[instance.id] ?? 0) + 1
    upsertInstance(instance)
  }

  /**
   * Sends a request about an instance, and keeps the instance it returns unless the hub pushed a newer one meanwhile.
   */
  async function applyResponse (id: string, request: () => Promise<Instance>) {
    const before = pushes[id] ?? 0
    const instance = await request()
    if ((pushes[id] ?? 0) === before) {
      upsertInstance(instance)
    }
  }

  function removeInstance (id: string) {
    // Counted as a push: the answer to a request sent before must not bring the instance back.
    pushes[id] = (pushes[id] ?? 0) + 1
    delete instances.value[id]
    delete operations.value[id]
    useHealthChecksStore().removeInstance(id)
    useBackupsStore().removeInstance(id)
  }

  function setOperation (snapshot: OperationSnapshot) {
    const previous = operations.value[snapshot.instanceId]
    operations.value[snapshot.instanceId] = snapshot
    if (previous?.status === 'Running' && snapshot.status === 'Succeeded') {
      switch (snapshot.kind) {
        case 'upgrade': {
          notifyUpgraded(snapshot)
          break
        }
        case 'convert-to-unicode': {
          notifyConvertedToUnicode(snapshot)
          break
        }
        case 'change-server-id': {
          notifyServerIdChanged(snapshot)
          break
        }
        case 'change-port': {
          notifyPortChanged(snapshot)
          break
        }
      }
    }
  }

  /**
   * Tells every client that a conversion to Unicode mode ended well. A failure is kept as the last error of the
   * instance instead.
   */
  function notifyConvertedToUnicode (snapshot: OperationSnapshot) {
    const instance = instances.value[snapshot.instanceId]
    if (instance) {
      useNotificationsStore().notify(i18n.global.t('convertToUnicode.converted', { name: instance.name }), 'success')
    }
  }

  /**
   * Tells every client that a change of the server ID ended well. The new server ID is already known: the daemon pushes
   * the instance once it is set. A failure is kept as the last error of the instance instead.
   */
  function notifyServerIdChanged (snapshot: OperationSnapshot) {
    const instance = instances.value[snapshot.instanceId]
    if (instance) {
      useNotificationsStore().notify(
        i18n.global.t('changeServerId.changed', { name: instance.name, serverId: instance.serverId ?? '' }),
        'success',
      )
    }
  }

  /**
   * Tells every client that a change of the port ended well. The new port is already known: the daemon pushes the
   * instance once it is saved. A failure is kept as the last error of the instance instead.
   */
  function notifyPortChanged (snapshot: OperationSnapshot) {
    const instance = instances.value[snapshot.instanceId]
    if (instance) {
      useNotificationsStore().notify(
        i18n.global.t('changePort.changed', { name: instance.name, port: formatPort(instance) }),
        'success',
      )
    }
  }

  /**
   * Tells every client that an upgrade ended well, for a few seconds: once done, it leaves nothing on the page. A
   * failure is kept as the last error of the instance instead. The new build is already known: the daemon pushes the
   * instance once it is pinned to it, before the database is upgraded.
   */
  function notifyUpgraded (snapshot: OperationSnapshot) {
    const instance = instances.value[snapshot.instanceId]
    if (!instance) {
      return
    }
    const params = { name: instance.name, version: `${instance.version}/${instance.build}` }
    const upToDate = snapshot.outcome === 'up-to-date'
    const { t } = i18n.global
    useNotificationsStore().notify(
      upToDate ? t('upgradeInstance.upToDate', params) : t('upgradeInstance.upgraded', params),
      upToDate ? 'info' : 'success',
    )
  }

  function setBackup (run: BackupRun) {
    const backups = useBackupsStore()
    const wasRunning = backups.getRuns(run.instanceId).some(r => r.id === run.id && r.status === 'Running')
    backups.setRun(run)
    if (wasRunning && run.status !== 'Running') {
      notifyBackupEnded(run)
    }
  }

  function setRestore (run: RestoreRun) {
    const backups = useBackupsStore()
    const wasRunning = backups.getRestores(run.instanceId).some(r => r.id === run.id && r.status === 'Running')
    backups.setRestore(run)
    if (wasRunning && run.status !== 'Running') {
      notifyRestoreEnded(run)
    }
  }

  /**
   * Tells every client how a restore ended: it may run for hours, with no one looking at its page.
   */
  function notifyRestoreEnded (run: RestoreRun) {
    const { t } = i18n.global
    const params = { name: run.instanceName }
    const colors: Partial<Record<RestoreRun['status'], 'success' | 'warning' | 'error' | 'info'>> = {
      Succeeded: 'success',
      SucceededWithProblems: 'warning',
      Failed: 'error',
      Canceled: 'info',
      Interrupted: 'warning',
    }
    useNotificationsStore().notify(t(`restores.notifications.${run.status}`, params), colors[run.status] ?? 'info')
  }

  /**
   * Tells every client how a backup ended: it may run for hours, with no one looking at its page.
   */
  function notifyBackupEnded (run: BackupRun) {
    const { t } = i18n.global
    const params = { name: run.instanceName }
    const colors: Partial<Record<BackupRun['status'], 'success' | 'warning' | 'error' | 'info'>> = {
      Succeeded: 'success',
      PartiallySucceeded: 'warning',
      Failed: 'error',
      Canceled: 'info',
      Interrupted: 'warning',
    }
    useNotificationsStore().notify(t(`backups.notifications.${run.status}`, params), colors[run.status] ?? 'info')
  }

  function setHealthCheck (run: HealthCheckRun) {
    const healthChecks = useHealthChecksStore()
    const wasRunning = healthChecks.getRuns(run.instanceId).some(r => r.id === run.id && r.status === 'Running')
    healthChecks.setRun(run)
    if (wasRunning && run.status !== 'Running') {
      notifyHealthCheckEnded(run)
    }
  }

  /**
   * Tells every client how a health check ended: it may run for hours, with no one looking at its page.
   */
  function notifyHealthCheckEnded (run: HealthCheckRun) {
    const name = instances.value[run.instanceId]?.name
    if (!name) {
      return
    }
    const { t } = i18n.global
    const notifications = useNotificationsStore()
    switch (run.status) {
      case 'Healthy': {
        notifications.notify(t('health.notifications.healthy', { name }), 'success')
        break
      }
      case 'ProblemsFound': {
        const count = run.findings.length
        notifications.notify(t('health.notifications.problems', { name, count }, count), 'error')
        break
      }
      case 'Failed': {
        notifications.notify(t('health.notifications.failed', { name }), 'warning')
        break
      }
      default: {
        break
      }
    }
  }

  async function fetchAll () {
    loading.value = true
    try {
      const list = await api.listInstances()
      instances.value = Object.fromEntries(list.map(instance => [instance.id, instance]))
      error.value = null
      loaded.value = true
    } catch (error_) {
      error.value = error_ instanceof Error ? error_.message : String(error_)
    } finally {
      loading.value = false
    }
  }

  async function fetchOperation (id: string) {
    const snapshot = await api.getOperation(id)
    if (snapshot) {
      setOperation(snapshot)
    }
  }

  async function create (request: CreateInstanceRequest): Promise<Instance> {
    const instance = await api.createInstance(request)
    upsertInstance(instance)
    return instance
  }

  async function update (id: string, request: UpdateInstanceRequest) {
    await applyResponse(id, () => api.updateInstance(id, request))
  }

  async function setSuperUser (id: string, request: SetSuperUserRequest) {
    await applyResponse(id, () => api.setSuperUser(id, request))
  }

  async function checkSuperUser (id: string) {
    await applyResponse(id, () => api.checkSuperUser(id))
  }

  async function start (id: string) {
    await applyResponse(id, () => api.startInstance(id))
  }

  async function stop (id: string) {
    await applyResponse(id, () => api.stopInstance(id))
  }

  async function restart (id: string) {
    await applyResponse(id, () => api.restartInstance(id))
  }

  async function enableMaintenance (id: string) {
    await applyResponse(id, () => api.enableMaintenanceMode(id))
  }

  async function disableMaintenance (id: string) {
    await applyResponse(id, () => api.disableMaintenanceMode(id))
  }

  /**
   * Begins the upgrade of the instance to the latest build of `release`. The hub tells its progress.
   */
  /**
   * Upgrades an instance to the latest build of a release, or to an exact build of it from the archive.
   */
  async function upgrade (id: string, release: string, build?: string) {
    await applyResponse(id, () => api.upgradeInstance(id, build ? { release, build } : { release }))
  }

  /**
   * Begins the conversion of the instance to Unicode mode. The hub tells its progress.
   */
  async function convertToUnicode (id: string) {
    await applyResponse(id, () => api.convertInstanceToUnicode(id))
  }

  /**
   * Begins the change of the server ID of the instance. The hub tells its progress.
   */
  async function changeServerId (id: string, serverId: string) {
    await applyResponse(id, () => api.changeInstanceServerId(id, { serverId }))
  }

  /**
   * Begins the change of the port of the instance. The hub tells its progress.
   */
  async function changePort (id: string, port: number) {
    await applyResponse(id, () => api.changeInstancePort(id, { port }))
  }

  /**
   * Starts, stops or restarts several instances, or all of them, and keeps the states returned by the daemon.
   */
  async function runAction (action: InstanceAction, selection: InstanceSelection): Promise<InstanceActionResult[]> {
    const results = await api.runInstancesAction(action, selection)
    for (const result of results) {
      upsertInstance(result.instance)
    }
    return results
  }

  /**
   * Begins the deletion of an instance. It goes on in the daemon: the instance stays, `Deleting`, until the hub tells
   * that it is gone, or pushes it back with its last error when the deletion fails.
   */
  async function remove (id: string, deleteFiles: boolean) {
    await applyResponse(id, () => api.deleteInstance(id, deleteFiles))
  }

  function connect () {
    hub ??= connectInstancesHub({
      onInstanceUpdated: applyPushedInstance,
      onInstanceDeleted: removeInstance,
      onOperationProgress: setOperation,
      onHealthCheckUpdated: setHealthCheck,
      onHealthCheckDeleted: (instanceId, runId) => useHealthChecksStore().removeRun(instanceId, runId),
      onBackupUpdated: setBackup,
      onRestoreUpdated: setRestore,
      onConnected: () => {
        void fetchAll()
        void useHealthChecksStore().refresh()
        void useBackupsStore().refresh()
      },
    })
  }

  async function disconnect () {
    await hub?.stop()
    hub = null
  }

  return {
    instances,
    operations,
    loaded,
    loading,
    error,
    sortedInstances,
    getInstance,
    upsertInstance,
    removeInstance,
    setOperation,
    setHealthCheck,
    fetchAll,
    fetchOperation,
    create,
    update,
    setSuperUser,
    checkSuperUser,
    start,
    stop,
    restart,
    enableMaintenance,
    disableMaintenance,
    upgrade,
    convertToUnicode,
    changeServerId,
    changePort,
    runAction,
    remove,
    connect,
    disconnect,
  }
})

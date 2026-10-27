// Types
import type { InstancesHub } from '@/api/hub'
import type {
  CreateInstanceRequest,
  Instance,
  InstanceAction,
  InstanceActionResult,
  InstanceSelection,
  OperationSnapshot,
  UpdateInstanceRequest,
} from '@/api/types'

// Utilities
import { defineStore } from 'pinia'
import { computed, ref } from 'vue'

// API, plugins and stores
import { connectInstancesHub } from '@/api/hub'
import * as api from '@/api/instances'
import i18n from '@/plugins/i18n'
import { useNotificationsStore } from '@/stores/notifications'

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

  const sortedInstances = computed(() =>
    Object.values(instances.value).toSorted((a, b) => a.name.localeCompare(b.name)),
  )

  function getInstance (id: string): Instance | undefined {
    return instances.value[id]
  }

  function upsertInstance (instance: Instance) {
    instances.value[instance.id] = instance
  }

  function removeInstance (id: string) {
    delete instances.value[id]
    delete operations.value[id]
  }

  function setOperation (snapshot: OperationSnapshot) {
    const previous = operations.value[snapshot.instanceId]
    operations.value[snapshot.instanceId] = snapshot
    if (previous?.status === 'Running' && snapshot.status === 'Succeeded' && snapshot.kind === 'upgrade') {
      notifyUpgraded(snapshot)
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
    upsertInstance(await api.updateInstance(id, request))
  }

  async function start (id: string) {
    upsertInstance(await api.startInstance(id))
  }

  async function stop (id: string) {
    upsertInstance(await api.stopInstance(id))
  }

  async function restart (id: string) {
    upsertInstance(await api.restartInstance(id))
  }

  /**
   * Begins the upgrade of the instance to the latest build of `release`. The hub tells its progress.
   */
  async function upgrade (id: string, release: string) {
    upsertInstance(await api.upgradeInstance(id, { release }))
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

  async function remove (id: string, deleteFiles: boolean) {
    await api.deleteInstance(id, deleteFiles)
    removeInstance(id)
  }

  function connect () {
    hub ??= connectInstancesHub({
      onInstanceUpdated: upsertInstance,
      onInstanceDeleted: removeInstance,
      onOperationProgress: setOperation,
      onConnected: () => void fetchAll(),
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
    fetchAll,
    fetchOperation,
    create,
    update,
    start,
    stop,
    restart,
    upgrade,
    runAction,
    remove,
    connect,
    disconnect,
  }
})

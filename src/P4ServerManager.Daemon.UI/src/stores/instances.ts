// Types
import type { InstancesHub } from '@/api/hub'
import type { CreateInstanceRequest, Instance, OperationSnapshot } from '@/api/types'

// Utilities
import { defineStore } from 'pinia'
import { computed, ref } from 'vue'

// API
import { connectInstancesHub } from '@/api/hub'
import * as api from '@/api/instances'

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

  function setOperation (snapshot: OperationSnapshot) {
    operations.value[snapshot.instanceId] = snapshot
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

  async function start (id: string) {
    upsertInstance(await api.startInstance(id))
  }

  async function stop (id: string) {
    upsertInstance(await api.stopInstance(id))
  }

  async function restart (id: string) {
    upsertInstance(await api.restartInstance(id))
  }

  function connect () {
    hub ??= connectInstancesHub({
      onInstanceUpdated: upsertInstance,
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
    setOperation,
    fetchAll,
    fetchOperation,
    create,
    start,
    stop,
    restart,
    connect,
    disconnect,
  }
})

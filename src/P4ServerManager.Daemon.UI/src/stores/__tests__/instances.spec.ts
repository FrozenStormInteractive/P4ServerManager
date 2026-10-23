// Types
import type { InstancesHubHandlers } from '@/api/hub'

// Utilities
import { http, HttpResponse } from 'msw'
import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { connectInstancesHub } from '@/api/hub'
import { useInstancesStore } from '@/stores/instances'
import { createInstance, createSnapshot } from '@/testing/fixtures'
import { server } from '@/testing/server'

vi.mock('@/api/hub', () => ({
  connectInstancesHub: vi.fn(),
}))

describe('instances store', () => {
  let handlers: InstancesHubHandlers

  beforeEach(() => {
    setActivePinia(createPinia())
    vi.mocked(connectInstancesHub).mockReset().mockImplementation(h => {
      handlers = h
      return { stop: vi.fn(async () => {}) }
    })
  })

  it('adds and updates the instances pushed by the hub, sorted by name', () => {
    const store = useInstancesStore()
    store.connect()

    handlers.onInstanceUpdated(createInstance({ id: 'b', name: 'zeta' }))
    handlers.onInstanceUpdated(createInstance({ id: 'a', name: 'alpha', state: 'Provisioning' }))
    handlers.onInstanceUpdated(createInstance({ id: 'a', name: 'alpha', state: 'Ready' }))

    expect(store.sortedInstances.map(i => i.name)).toEqual(['alpha', 'zeta'])
    expect(store.getInstance('a')?.state).toBe('Ready')
  })

  it('keeps the last operation snapshot of each instance', () => {
    const store = useInstancesStore()
    store.connect()

    handlers.onOperationProgress(createSnapshot('a', { percent: 10 }))
    handlers.onOperationProgress(createSnapshot('a', { percent: 80 }))

    expect(store.operations.a?.percent).toBe(80)
  })

  it('reloads the instances once connected, as events may have been missed', async () => {
    server.use(http.get('/api/instances', () => HttpResponse.json([createInstance({ id: 'c', name: 'reloaded' })])))
    const store = useInstancesStore()
    store.connect()
    handlers.onInstanceUpdated(createInstance({ id: 'stale', name: 'stale' }))

    handlers.onConnected()

    await vi.waitFor(() => expect(store.sortedInstances.map(i => i.name)).toEqual(['reloaded']))
    expect(store.loaded).toBe(true)
  })

  it('connects to the hub only once', () => {
    const store = useInstancesStore()

    store.connect()
    store.connect()

    expect(connectInstancesHub).toHaveBeenCalledTimes(1)
  })

  it('reports an error when the instances cannot be loaded', async () => {
    server.use(http.get('/api/instances', () => HttpResponse.json({ title: 'Boom' }, { status: 500 })))
    const store = useInstancesStore()

    await store.fetchAll()

    expect(store.error).toBe('Boom')
    expect(store.loaded).toBe(false)
  })
})

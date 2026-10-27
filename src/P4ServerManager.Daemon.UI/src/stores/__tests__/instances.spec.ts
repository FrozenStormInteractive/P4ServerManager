// Types
import type { InstancesHubHandlers } from '@/api/hub'

// Utilities
import { http, HttpResponse } from 'msw'
import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { connectInstancesHub } from '@/api/hub'
import { useInstancesStore } from '@/stores/instances'
import { useNotificationsStore } from '@/stores/notifications'
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

  describe('at the end of an upgrade', () => {
    function finish (outcome: string | null, kind = 'upgrade') {
      const store = useInstancesStore()
      store.connect()
      handlers.onInstanceUpdated(createInstance({ id: 'a', name: 'main', version: '2025.2', build: '3086021' }))
      handlers.onOperationProgress(createSnapshot('a', { kind, status: 'Running' }))
      handlers.onOperationProgress(createSnapshot('a', { kind, status: 'Succeeded', outcome }))
      return useNotificationsStore().queue
    }

    it('tells that the instance runs its new build', () => {
      expect(finish('upgraded')).toEqual([{ text: 'main now runs p4d 2025.2/3086021.', color: 'success' }])
    })

    it('tells that the instance already ran the latest build', () => {
      expect(finish('up-to-date')).toEqual([{
        text: 'main already runs the latest build, p4d 2025.2/3086021. Nothing was changed.',
        color: 'info',
      }])
    })

    it('tells nothing at the end of another operation', () => {
      expect(finish(null, 'provision')).toEqual([])
    })

    it('tells nothing about an upgrade that had already ended when the page loaded', () => {
      const store = useInstancesStore()
      store.connect()
      handlers.onInstanceUpdated(createInstance({ id: 'a' }))

      handlers.onOperationProgress(createSnapshot('a', { kind: 'upgrade', status: 'Succeeded', outcome: 'upgraded' }))

      expect(useNotificationsStore().queue).toEqual([])
    })
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

  it('restarts an instance and keeps the state returned by the daemon', async () => {
    server.use(http.post('/api/instances/a/restart', () =>
      HttpResponse.json(createInstance({ id: 'a', name: 'alpha', lastError: null }), { status: 202 })))
    const store = useInstancesStore()
    store.upsertInstance(createInstance({ id: 'a', name: 'alpha', lastError: 'Old error' }))

    await store.restart('a')

    expect(store.getInstance('a')?.lastError).toBeNull()
  })

  it('restarts several instances and keeps the states returned by the daemon', async () => {
    let body: unknown
    server.use(http.post('/api/instances/restart', async ({ request }) => {
      body = await request.json()
      return HttpResponse.json([
        { instance: createInstance({ id: 'a', name: 'alpha', lastError: null }), outcome: 'Succeeded', error: null },
        { instance: createInstance({ id: 'b', name: 'beta', lastError: 'Failed' }), outcome: 'Failed', error: 'Failed' },
      ])
    }))
    const store = useInstancesStore()
    store.upsertInstance(createInstance({ id: 'a', name: 'alpha', lastError: 'Old error' }))
    store.upsertInstance(createInstance({ id: 'b', name: 'beta', lastError: null }))

    const results = await store.runAction('restart', { instanceIds: ['a', 'b'] })

    expect(body).toEqual({ instanceIds: ['a', 'b'] })
    expect(results.map(r => r.outcome)).toEqual(['Succeeded', 'Failed'])
    expect(store.getInstance('a')?.lastError).toBeNull()
    expect(store.getInstance('b')?.lastError).toBe('Failed')
  })

  it('sends the changed settings and keeps the instance returned by the daemon', async () => {
    let body: unknown
    server.use(http.patch('/api/instances/a', async ({ request }) => {
      body = await request.json()
      return HttpResponse.json(createInstance({ id: 'a', name: 'alpha', autoStart: false }))
    }))
    const store = useInstancesStore()
    store.upsertInstance(createInstance({ id: 'a', name: 'alpha', autoStart: true }))

    await store.update('a', { autoStart: false })

    expect(body).toEqual({ autoStart: false })
    expect(store.getInstance('a')?.autoStart).toBe(false)
  })

  it('forgets an instance deleted by another client', () => {
    const store = useInstancesStore()
    store.connect()
    handlers.onInstanceUpdated(createInstance({ id: 'a', name: 'alpha' }))
    handlers.onOperationProgress(createSnapshot('a'))

    handlers.onInstanceDeleted('a')

    expect(store.getInstance('a')).toBeUndefined()
    expect(store.operations.a).toBeUndefined()
  })

  it('forgets an instance once the daemon has deleted it', async () => {
    server.use(http.delete('/api/instances/a', () => new HttpResponse(null, { status: 204 })))
    const store = useInstancesStore()
    store.upsertInstance(createInstance({ id: 'a', name: 'alpha' }))

    await store.remove('a', false)

    expect(store.getInstance('a')).toBeUndefined()
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

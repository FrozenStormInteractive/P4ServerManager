// Types
import type { BuildsHubHandlers } from '@/api/hub'

// Utilities
import { http, HttpResponse } from 'msw'
import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { connectBuildsHub } from '@/api/hub'
import { useBuildsStore } from '@/stores/builds'
import { createArchiveStatus, createBuild } from '@/testing/fixtures'
import { server } from '@/testing/server'

vi.mock('@/api/hub', () => ({
  connectBuildsHub: vi.fn(),
}))

describe('builds store', () => {
  let handlers: BuildsHubHandlers
  let stop: ReturnType<typeof vi.fn<() => Promise<void>>>

  beforeEach(() => {
    setActivePinia(createPinia())
    stop = vi.fn<() => Promise<void>>(async () => {})
    vi.mocked(connectBuildsHub).mockReset().mockImplementation(h => {
      handlers = h
      return { stop }
    })
  })

  it('keeps the builds pushed by the hub, newest first', () => {
    const store = useBuildsStore()
    store.connect()

    handlers.onBuildUpdated(createBuild({ id: 'a', version: '2024.2', build: '2700000' }))
    handlers.onBuildUpdated(createBuild({ id: 'b', version: '2025.1', build: '2800000' }))
    handlers.onBuildUpdated(createBuild({ id: 'c', version: '2025.1', build: '2900000' }))
    handlers.onBuildDeleted('b')

    expect(store.sortedBuilds.map(b => b.id)).toEqual(['c', 'a'])
  })

  it('follows the status of the archive worker', () => {
    const store = useBuildsStore()
    store.connect()

    handlers.onArchiveStatusChanged(createArchiveStatus({ running: true, activity: 'Checking r25.1' }))

    expect(store.status?.activity).toBe('Checking r25.1')
  })

  it('reads everything again once connected', async () => {
    server.use(
      http.get('/api/p4d/builds', () => HttpResponse.json([createBuild()])),
      http.get('/api/p4d/archive/status', () => HttpResponse.json(createArchiveStatus())),
    )
    const store = useBuildsStore()
    store.connect()

    handlers.onConnected()
    await vi.waitFor(() => expect(store.loaded).toBe(true))

    expect(store.sortedBuilds).toHaveLength(1)
  })

  it('connects once and stops on disconnect', async () => {
    const store = useBuildsStore()
    store.connect()
    store.connect()

    await store.disconnect()

    expect(connectBuildsHub).toHaveBeenCalledTimes(1)
    expect(stop).toHaveBeenCalledTimes(1)
  })
})

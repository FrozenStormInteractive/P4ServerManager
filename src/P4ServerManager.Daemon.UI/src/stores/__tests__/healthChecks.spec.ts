// Utilities
import { http, HttpResponse } from 'msw'
import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it } from 'vitest'
import { useHealthChecksStore } from '@/stores/healthChecks'
import { createHealthCheckRun } from '@/testing/fixtures'
import { server } from '@/testing/server'

const instanceId = '5f0079c4-12bf-4f3e-b7f5-3d8677fb10bc'
const checksUrl = `/api/instances/${instanceId}/health-checks`

const running = createHealthCheckRun(instanceId, { status: 'Running', finishedAt: null })
const ended = createHealthCheckRun(instanceId)

describe('useHealthChecksStore', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
  })

  it('keeps the end of a check when the answer that began it comes after it', async () => {
    const store = useHealthChecksStore()
    // A short check ends, and the hub tells it, before the browser reads the answer to its request.
    server.use(http.post(checksUrl, () => {
      store.setRun(ended)
      return HttpResponse.json(running, { status: 202 })
    }))

    await store.start(instanceId, ['Validate'])

    expect(store.getRuns(instanceId)).toEqual([ended])
    expect(store.getRunningRun(instanceId)).toBeNull()
  })

  it('keeps the end of a check when a list read before it comes after it', async () => {
    const store = useHealthChecksStore()
    store.setRun(running)
    server.use(http.get(checksUrl, () => {
      store.setRun(ended)
      return HttpResponse.json([running])
    }))

    await store.fetchRuns(instanceId)

    expect(store.getRunningRun(instanceId)).toBeNull()
  })

  it('follows a check from running to its end', () => {
    const store = useHealthChecksStore()

    store.setRun(running)
    expect(store.getRunningRun(instanceId)).not.toBeNull()

    store.setRun(ended)
    expect(store.getRunningRun(instanceId)).toBeNull()
  })

  it('reads again an instance whose first read has not answered yet', async () => {
    const store = useHealthChecksStore()
    let reads = 0
    server.use(http.get(checksUrl, () => {
      reads++
      return HttpResponse.json(reads === 1 ? [running] : [ended])
    }))

    const first = store.fetchRuns(instanceId)
    await store.refresh()
    await first

    expect(reads).toBe(2)
  })
})

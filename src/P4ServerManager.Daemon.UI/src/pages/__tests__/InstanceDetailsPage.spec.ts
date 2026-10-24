// Types
import type { RuntimeStatus } from '@/api/types'

// Utilities
import { flushPromises } from '@vue/test-utils'
import { http, HttpResponse } from 'msw'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import InstanceDetailsPage from '@/pages/InstanceDetailsPage.vue'
import { useInstancesStore } from '@/stores/instances'
import { createInstance } from '@/testing/fixtures'
import { mountWithPlugins } from '@/testing/mount'
import { server } from '@/testing/server'

const instance = createInstance()

// The state of the instance comes from the daemon only: a page loaded in the middle of an operation, or opened by
// another client, shows it the same way.
async function mountWith (runtimeStatus: RuntimeStatus) {
  const { wrapper, pinia } = await mountWithPlugins(InstanceDetailsPage, { props: { id: instance.id } })
  useInstancesStore(pinia).upsertInstance({ ...instance, runtimeStatus })
  await flushPromises()

  return wrapper.findAll('button').map(button => ({
    text: button.text(),
    loading: button.classes().includes('v-btn--loading'),
    disabled: button.attributes('disabled') !== undefined,
  }))
}

describe('InstanceDetailsPage', () => {
  beforeEach(() => {
    server.use(http.get(`/api/instances/${instance.id}/operation`, () => HttpResponse.json({}, { status: 404 })))
  })

  afterEach(() => {
    document.body.innerHTML = ''
  })

  it('offers a restart and a stop while p4d runs', async () => {
    expect(await mountWith('Running')).toEqual([
      { text: 'Restart', loading: false, disabled: false },
      { text: 'Stop', loading: false, disabled: false },
    ])
  })

  it('shows the restart in progress, even while p4d is stopped by it', async () => {
    expect(await mountWith('Restarting')).toEqual([
      { text: 'Restart', loading: true, disabled: true },
      { text: 'Stop', loading: false, disabled: true },
    ])
  })

  it('shows the stop in progress', async () => {
    expect(await mountWith('Stopping')).toEqual([
      { text: 'Restart', loading: false, disabled: true },
      { text: 'Stop', loading: true, disabled: true },
    ])
  })

  it('shows the start in progress', async () => {
    expect(await mountWith('Starting')).toEqual([
      { text: 'Start', loading: true, disabled: true },
    ])
  })

  it('offers a start while p4d is stopped', async () => {
    expect(await mountWith('Stopped')).toEqual([
      { text: 'Start', loading: false, disabled: false },
    ])
  })
})

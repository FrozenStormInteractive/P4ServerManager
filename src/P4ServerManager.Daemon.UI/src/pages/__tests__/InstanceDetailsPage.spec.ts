// Types
import type { InstanceState, RuntimeStatus } from '@/api/types'

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
async function mountWith (runtimeStatus: RuntimeStatus, state: InstanceState = 'Ready') {
  const { wrapper, pinia } = await mountWithPlugins(InstanceDetailsPage, { props: { id: instance.id } })
  useInstancesStore(pinia).upsertInstance({ ...instance, state, runtimeStatus })
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
      { text: 'Delete', loading: false, disabled: false },
    ])
  })

  it('shows the restart in progress, even while p4d is stopped by it', async () => {
    expect(await mountWith('Restarting')).toEqual([
      { text: 'Restart', loading: true, disabled: true },
      { text: 'Stop', loading: false, disabled: true },
      { text: 'Delete', loading: false, disabled: true },
    ])
  })

  it('shows the stop in progress', async () => {
    expect(await mountWith('Stopping')).toEqual([
      { text: 'Restart', loading: false, disabled: true },
      { text: 'Stop', loading: true, disabled: true },
      { text: 'Delete', loading: false, disabled: true },
    ])
  })

  it('shows the start in progress', async () => {
    expect(await mountWith('Starting')).toEqual([
      { text: 'Start', loading: true, disabled: true },
      { text: 'Delete', loading: false, disabled: true },
    ])
  })

  it('offers a start while p4d is stopped', async () => {
    expect(await mountWith('Stopped')).toEqual([
      { text: 'Start', loading: false, disabled: false },
      { text: 'Delete', loading: false, disabled: false },
    ])
  })

  it('shows the deletion in progress', async () => {
    expect(await mountWith('Deleting')).toEqual([
      { text: 'Start', loading: false, disabled: true },
      { text: 'Delete', loading: true, disabled: true },
    ])
  })

  it('offers to delete a failed instance', async () => {
    expect(await mountWith('Stopped', 'Failed')).toEqual([
      { text: 'Start', loading: false, disabled: true },
      { text: 'Delete', loading: false, disabled: false },
    ])
  })

  it('asks before it stops the server', async () => {
    let requests = 0
    server.use(http.post(`/api/instances/${instance.id}/stop`, () => {
      requests++
      return HttpResponse.json({ ...instance, runtimeStatus: 'Stopped' }, { status: 202 })
    }))
    const { wrapper, pinia } = await mountWithPlugins(InstanceDetailsPage, {
      props: { id: instance.id },
      attachTo: document.body,
    })
    useInstancesStore(pinia).upsertInstance({ ...instance, runtimeStatus: 'Running' })
    await flushPromises()
    const stopButtons = () => [...document.querySelectorAll('button')].filter(b => b.textContent?.trim() === 'Stop')

    await wrapper.findAll('button').find(b => b.text() === 'Stop')!.trigger('click')
    await flushPromises()

    expect(document.body.textContent).toContain(`Stop ${instance.name}?`)
    expect(requests).toBe(0)

    stopButtons().at(-1)!.click()
    await flushPromises()

    expect(requests).toBe(1)
    expect(useInstancesStore(pinia).getInstance(instance.id)?.runtimeStatus).toBe('Stopped')
  })

  it('turns the auto-start off and shows the setting saved by the daemon', async () => {
    let body: unknown
    server.use(http.patch(`/api/instances/${instance.id}`, async ({ request }) => {
      body = await request.json()
      return HttpResponse.json({ ...instance, autoStart: false })
    }))
    const { wrapper, pinia } = await mountWithPlugins(InstanceDetailsPage, { props: { id: instance.id } })
    useInstancesStore(pinia).upsertInstance({ ...instance, autoStart: true })
    await flushPromises()
    const autoStart = wrapper.find<HTMLInputElement>('input[name="autoStart"]')
    expect(autoStart.element.checked).toBe(true)

    await autoStart.setValue(false)
    await flushPromises()

    expect(body).toEqual({ autoStart: false })
    expect(useInstancesStore(pinia).getInstance(instance.id)?.autoStart).toBe(false)
    expect(autoStart.element.checked).toBe(false)
  })

  it('shows the SSL port and the fingerprint of the certificate', async () => {
    const fingerprint = 'A5:C8:59:F1:AC:B9:85:90:D9:6D:4A:3F:70:2A:F9:2C:F6:CB:12:F8'
    const { wrapper, pinia } = await mountWithPlugins(InstanceDetailsPage, { props: { id: instance.id } })
    useInstancesStore(pinia).upsertInstance({ ...instance, ssl: true, sslFingerprint: fingerprint })
    await flushPromises()

    expect(wrapper.text()).toContain('ssl:1666')
    expect(wrapper.text()).toContain(fingerprint)
  })

  it('does not offer to delete an instance being provisioned', async () => {
    expect(await mountWith('Unknown', 'Provisioning')).toEqual([
      { text: 'Start', loading: false, disabled: true },
      { text: 'Delete', loading: false, disabled: true },
    ])
  })
})

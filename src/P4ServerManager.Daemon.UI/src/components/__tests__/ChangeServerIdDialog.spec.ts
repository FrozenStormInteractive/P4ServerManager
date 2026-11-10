// Types
import type { Instance } from '@/api/types'

// Utilities
import { flushPromises } from '@vue/test-utils'
import { http, HttpResponse } from 'msw'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import ChangeServerIdDialog from '@/components/instances/ChangeServerIdDialog.vue'
import { useInstancesStore } from '@/stores/instances'
import { createInstance } from '@/testing/fixtures'
import { mountWithPlugins } from '@/testing/mount'
import { server } from '@/testing/server'

const instance = createInstance({ serverId: 'main', runtimeStatus: 'Running' })

function field () {
  const element = document.querySelector<HTMLInputElement>('[name="serverId"]')
  if (!element) {
    throw new Error('No server ID field.')
  }
  return element
}

async function fill (value: string) {
  const element = field()
  element.value = value
  element.dispatchEvent(new Event('input'))
  await flushPromises()
}

async function submit () {
  document.querySelector('#change-server-id-form')!.dispatchEvent(new Event('submit', { cancelable: true }))
  await flushPromises()
}

async function mountDialog (value: Instance = instance) {
  const result = await mountWithPlugins(ChangeServerIdDialog, {
    props: { modelValue: true, instance: value },
    attachTo: document.body,
  })
  useInstancesStore(result.pinia).upsertInstance(value)
  await flushPromises()
  return result
}

describe('ChangeServerIdDialog', () => {
  let requests: unknown[]

  beforeEach(() => {
    requests = []
    server.use(http.put(`/api/instances/${instance.id}/server-id`, async ({ request }) => {
      requests.push(await request.json())
      return HttpResponse.json({ ...instance, runtimeStatus: 'ChangingServerId' }, { status: 202 })
    }))
  })

  afterEach(() => {
    document.body.innerHTML = ''
  })

  it('starts from the current server ID and tells that a running server restarts', async () => {
    await mountDialog()

    expect(field().value).toBe('main')
    expect(document.body.textContent).toContain('Current server ID: main')
    expect(document.body.textContent).toContain('The server is restarted')
  })

  it('tells that a stopped server is started for a moment', async () => {
    await mountDialog({ ...instance, runtimeStatus: 'Stopped' })

    expect(document.body.textContent).toContain('then stopped again')
  })

  it('sends the new server ID and shows the change in progress', async () => {
    const { wrapper, pinia } = await mountDialog()

    await fill('edge')
    await submit()

    await vi.waitFor(() => expect(requests).toEqual([{ serverId: 'edge' }]))
    await flushPromises()
    expect(useInstancesStore(pinia).getInstance(instance.id)?.runtimeStatus).toBe('ChangingServerId')
    expect(wrapper.emitted('update:modelValue')).toEqual([[false]])
  })

  it('refuses the current server ID and a malformed one', async () => {
    await mountDialog()

    await submit()
    expect(document.body.textContent).toContain('Enter a server ID other than the current one.')

    await fill('1-edge')
    await submit()
    expect(document.body.textContent).toContain('Start with a letter, then up to 31 letters, digits')
    expect(requests).toEqual([])
  })

  it('shows the field error of the daemon on the field', async () => {
    server.use(http.put(`/api/instances/${instance.id}/server-id`, () => HttpResponse.json({
      title: 'One or more validation errors occurred.',
      status: 400,
      errors: { serverId: ['The server ID starts with a letter and has up to 32 letters, digits, \'-\' or \'_\'.'] },
    }, { status: 400 })))
    const { wrapper } = await mountDialog()

    await fill('edge')
    await submit()

    await vi.waitFor(() => expect(document.body.textContent).toContain('The server ID starts with a letter'))
    expect(wrapper.emitted('update:modelValue')).toBeUndefined()
  })

  it('shows why the daemon refused the change', async () => {
    server.use(http.put(`/api/instances/${instance.id}/server-id`, () =>
      HttpResponse.json({ detail: 'Another operation is in progress on this instance.' }, { status: 409 })))
    await mountDialog()

    await fill('edge')
    await submit()

    await vi.waitFor(() => expect(document.body.textContent)
      .toContain('The server ID could not be changed: Another operation is in progress on this instance.'))
  })
})

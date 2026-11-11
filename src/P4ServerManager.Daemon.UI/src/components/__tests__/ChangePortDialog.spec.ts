// Types
import type { Instance } from '@/api/types'

// Utilities
import { flushPromises } from '@vue/test-utils'
import { http, HttpResponse } from 'msw'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import ChangePortDialog from '@/components/instances/ChangePortDialog.vue'
import { useInstancesStore } from '@/stores/instances'
import { createInstance } from '@/testing/fixtures'
import { mountWithPlugins } from '@/testing/mount'
import { server } from '@/testing/server'

const instance = createInstance({ port: 1666, runtimeStatus: 'Running' })

function field () {
  const element = document.querySelector<HTMLInputElement>('[name="port"]')
  if (!element) {
    throw new Error('No port field.')
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
  document.querySelector('#change-port-form')!.dispatchEvent(new Event('submit', { cancelable: true }))
  await flushPromises()
}

async function mountDialog (value: Instance = instance) {
  const result = await mountWithPlugins(ChangePortDialog, {
    props: { modelValue: true, instance: value },
    attachTo: document.body,
  })
  useInstancesStore(result.pinia).upsertInstance(value)
  await flushPromises()
  return result
}

describe('ChangePortDialog', () => {
  let requests: unknown[]

  beforeEach(() => {
    requests = []
    server.use(http.put(`/api/instances/${instance.id}/port`, async ({ request }) => {
      requests.push(await request.json())
      return HttpResponse.json({ ...instance, runtimeStatus: 'ChangingPort' }, { status: 202 })
    }))
  })

  afterEach(() => {
    document.body.innerHTML = ''
  })

  it('starts from the current port and tells that a running server restarts', async () => {
    await mountDialog()

    expect(field().value).toBe('1666')
    expect(document.body.textContent).toContain('Current port: 1666')
    expect(document.body.textContent).toContain('The server is restarted')
    expect(document.body.textContent).toContain('updated as the stored super user')
  })

  it('tells that a stopped server is started for a moment to update its spec', async () => {
    await mountDialog({ ...instance, runtimeStatus: 'Stopped' })

    expect(document.body.textContent).toContain('then stopped again')
  })

  it('tells that the spec is left as it is without a super user, and does not start a stopped server', async () => {
    await mountDialog({ ...instance, runtimeStatus: 'Stopped', superUser: null })

    expect(document.body.textContent).toContain('the address of the server spec is left as it is')
    expect(document.body.textContent).not.toContain('then stopped again')
  })

  it('sends the new port and shows the change in progress', async () => {
    const { wrapper, pinia } = await mountDialog()

    await fill('1777')
    await submit()

    await vi.waitFor(() => expect(requests).toEqual([{ port: 1777 }]))
    await flushPromises()
    expect(useInstancesStore(pinia).getInstance(instance.id)?.runtimeStatus).toBe('ChangingPort')
    expect(wrapper.emitted('update:modelValue')).toEqual([[false]])
  })

  it('refuses the current port and one out of range', async () => {
    await mountDialog()

    await submit()
    expect(document.body.textContent).toContain('Enter a port other than the current one.')

    await fill('80')
    await submit()
    expect(document.body.textContent).toContain('Enter a port between 1024 and 65535.')
    expect(requests).toEqual([])
  })

  it('shows the field error of the daemon on the field', async () => {
    server.use(http.put(`/api/instances/${instance.id}/port`, () => HttpResponse.json({
      title: 'One or more validation errors occurred.',
      status: 400,
      errors: { port: ['Another instance uses port 1777.'] },
    }, { status: 400 })))
    const { wrapper } = await mountDialog()

    await fill('1777')
    await submit()

    await vi.waitFor(() => expect(document.body.textContent).toContain('Another instance uses port 1777.'))
    expect(wrapper.emitted('update:modelValue')).toBeUndefined()
  })

  it('shows why the daemon refused the change', async () => {
    server.use(http.put(`/api/instances/${instance.id}/port`, () =>
      HttpResponse.json({ detail: 'Another operation is in progress on this instance.' }, { status: 409 })))
    await mountDialog()

    await fill('1777')
    await submit()

    await vi.waitFor(() => expect(document.body.textContent)
      .toContain('The port could not be changed: Another operation is in progress on this instance.'))
  })
})

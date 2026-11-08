// Utilities
import { flushPromises } from '@vue/test-utils'
import { http, HttpResponse } from 'msw'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import SuperUserDialog from '@/components/instances/SuperUserDialog.vue'
import { useInstancesStore } from '@/stores/instances'
import { createInstance } from '@/testing/fixtures'
import { mountWithPlugins } from '@/testing/mount'
import { server } from '@/testing/server'

const instance = createInstance({
  superUser: { userName: 'admin', status: 'InvalidCredentials', checkedAt: null, error: 'Password invalid.' },
})

function field (name: string) {
  const element = document.querySelector<HTMLInputElement>(`[name="${CSS.escape(name)}"]`)
  if (!element) {
    throw new Error(`No field named ${name}.`)
  }
  return element
}

async function fill (name: string, value: string) {
  const element = field(name)
  element.value = value
  element.dispatchEvent(new Event('input'))
  await flushPromises()
}

async function submit () {
  document.querySelector('#super-user-form')!.dispatchEvent(new Event('submit', { cancelable: true }))
  await flushPromises()
}

async function mountDialog (overrides = {}) {
  const value = { ...instance, ...overrides }
  const result = await mountWithPlugins(SuperUserDialog, {
    props: { modelValue: true, instance: value },
    attachTo: document.body,
  })
  useInstancesStore(result.pinia).upsertInstance(value)
  await flushPromises()
  return result
}

describe('SuperUserDialog', () => {
  let requests: unknown[]

  beforeEach(() => {
    requests = []
    server.use(http.put(`/api/instances/${instance.id}/super-user`, async ({ request }) => {
      const body = await request.json() as { userName: string }
      requests.push(body)
      return HttpResponse.json({
        ...instance,
        superUser: { userName: body.userName, status: 'Valid', checkedAt: '2026-11-08T10:00:00.000Z', error: null },
      })
    }))
  })

  afterEach(() => {
    document.body.innerHTML = ''
  })

  it('starts from the stored user name and an empty password', async () => {
    await mountDialog()

    expect(field('userName').value).toBe('admin')
    expect(field('password').value).toBe('')
    expect(field('password').type).toBe('password')
  })

  it('sends the account, even with a weak password, and shows the result of the daemon', async () => {
    const { wrapper, pinia } = await mountDialog()

    await fill('password', 'weak')
    await submit()

    await vi.waitFor(() => expect(requests).toEqual([{ userName: 'admin', password: 'weak' }]))
    await flushPromises()
    expect(useInstancesStore(pinia).getInstance(instance.id)?.superUser?.status).toBe('Valid')
    expect(wrapper.emitted('update:modelValue')).toEqual([[false]])
  })

  it('needs a password', async () => {
    const { wrapper } = await mountDialog()

    await submit()

    await vi.waitFor(() => expect(document.body.textContent).toContain('Enter the password.'))
    expect(requests).toEqual([])
    expect(wrapper.emitted('update:modelValue')).toBeUndefined()
  })

  it('shows the refusal of the server on its field and stays open', async () => {
    server.use(http.put(`/api/instances/${instance.id}/super-user`, () => HttpResponse.json(
      { title: 'One or more validation errors occurred.', errors: { userName: ['The user ops has admin access: super access is needed.'] } },
      { status: 400 },
    )))
    const { wrapper } = await mountDialog()

    await fill('userName', 'ops')
    await fill('password', 'secret')
    await submit()

    await vi.waitFor(() => expect(document.body.textContent).toContain('super access is needed'))
    expect(wrapper.emitted('update:modelValue')).toBeUndefined()
  })

  it('tells that the account is checked once a stopped server runs', async () => {
    await mountDialog({ runtimeStatus: 'Stopped' })

    expect(document.body.textContent).toContain('checked once it runs')
  })
})

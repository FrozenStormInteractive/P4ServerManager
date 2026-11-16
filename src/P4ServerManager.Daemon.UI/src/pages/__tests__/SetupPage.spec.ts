// Utilities
import { flushPromises } from '@vue/test-utils'
import { http, HttpResponse } from 'msw'
import { afterEach, describe, expect, it, vi } from 'vitest'
import SetupPage from '@/pages/SetupPage.vue'
import { mountWithPlugins } from '@/testing/mount'
import { server } from '@/testing/server'

const user = { id: 'u1', userName: 'admin', createdAt: '2026-11-14T10:00:00.000Z', lastSignInAt: null }

async function fill (name: string, value: string) {
  const element = document.querySelector<HTMLInputElement>(`[name="${CSS.escape(name)}"]`)!
  element.value = value
  element.dispatchEvent(new Event('input'))
  await flushPromises()
}

async function submit () {
  document.querySelector('#setup-form')!.dispatchEvent(new Event('submit', { cancelable: true }))
  await flushPromises()
}

describe('SetupPage', () => {
  afterEach(() => {
    document.body.innerHTML = ''
  })

  it('creates the first account, then opens the dashboard', async () => {
    const requests: unknown[] = []
    server.use(http.post('/api/auth/setup', async ({ request }) => {
      requests.push(await request.json())
      return HttpResponse.json({ setupRequired: false, user }, { status: 201 })
    }))
    const { router } = await mountWithPlugins(SetupPage, { attachTo: document.body })
    const replace = vi.spyOn(router, 'replace').mockResolvedValue(undefined)

    await fill('password', 'correct horse battery')
    await fill('confirmation', 'correct horse battery')
    await submit()

    await vi.waitFor(() => expect(replace).toHaveBeenCalledWith({ name: 'dashboard' }))
    expect(requests).toEqual([{ userName: 'admin', password: 'correct horse battery' }])
  })

  it('checks the length and the confirmation of the password before sending it', async () => {
    const { wrapper } = await mountWithPlugins(SetupPage, { attachTo: document.body })

    await fill('password', 'short')
    await fill('confirmation', 'other')
    await submit()

    await vi.waitFor(() => expect(wrapper.text()).toContain('Between 10 and 128 characters.'))
    expect(wrapper.text()).toContain('The passwords do not match.')
  })

  it('goes to the sign-in page when another browser created the account first', async () => {
    server.use(http.post('/api/auth/setup', () => HttpResponse.json(
      { status: 409, detail: 'The first account already exists. Sign in instead.' },
      { status: 409 },
    )))
    const { router } = await mountWithPlugins(SetupPage, { attachTo: document.body })
    const replace = vi.spyOn(router, 'replace').mockResolvedValue(undefined)

    await fill('password', 'correct horse battery')
    await fill('confirmation', 'correct horse battery')
    await submit()

    await vi.waitFor(() => expect(replace).toHaveBeenCalledWith({ name: 'login' }))
  })
})

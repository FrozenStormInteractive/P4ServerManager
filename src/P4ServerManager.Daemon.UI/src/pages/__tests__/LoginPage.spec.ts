// Utilities
import { flushPromises } from '@vue/test-utils'
import { http, HttpResponse } from 'msw'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { REQUEST_HEADER } from '@/api/client'
import LoginPage from '@/pages/LoginPage.vue'
import { useAuthStore } from '@/stores/auth'
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
  document.querySelector('#login-form')!.dispatchEvent(new Event('submit', { cancelable: true }))
  await flushPromises()
}

describe('LoginPage', () => {
  afterEach(() => {
    document.body.innerHTML = ''
  })

  it('signs in with the header of the web interface, then goes to the page asked for', async () => {
    const requests: { body: unknown, header: string | null }[] = []
    server.use(http.post('/api/auth/login', async ({ request }) => {
      requests.push({ body: await request.json(), header: request.headers.get(REQUEST_HEADER) })
      return HttpResponse.json({ setupRequired: false, user })
    }))
    const { router, pinia } = await mountWithPlugins(LoginPage, { attachTo: document.body })
    const replace = vi.spyOn(router, 'replace').mockResolvedValue(undefined)
    await router.push({ name: 'login', query: { redirect: '/instances' } })

    await fill('userName', 'admin')
    await fill('password', 'correct horse battery')
    await submit()

    await vi.waitFor(() => expect(replace).toHaveBeenCalledWith('/instances'))
    expect(requests).toEqual([{
      body: { userName: 'admin', password: 'correct horse battery', rememberMe: false },
      header: '1',
    }])
    expect(useAuthStore(pinia).user?.userName).toBe('admin')
  })

  it('shows the error of the daemon and stays on the page', async () => {
    server.use(http.post('/api/auth/login', () => HttpResponse.json(
      { title: 'Unauthorized', status: 401, detail: 'The user name or the password is not correct.' },
      { status: 401 },
    )))
    const { wrapper, router } = await mountWithPlugins(LoginPage, { attachTo: document.body })
    const replace = vi.spyOn(router, 'replace')

    await fill('userName', 'admin')
    await fill('password', 'wrong')
    await submit()

    await vi.waitFor(() => expect(wrapper.text()).toContain('The user name or the password is not correct.'))
    expect(replace).not.toHaveBeenCalled()
  })

  it('asks to wait after too many attempts', async () => {
    server.use(http.post('/api/auth/login', () => HttpResponse.json({ status: 429 }, { status: 429 })))
    const { wrapper } = await mountWithPlugins(LoginPage, { attachTo: document.body })

    await fill('userName', 'admin')
    await fill('password', 'wrong')
    await submit()

    await vi.waitFor(() => expect(wrapper.text()).toContain('Too many attempts'))
  })

  it('ignores a redirection to another site', async () => {
    server.use(http.post('/api/auth/login', () => HttpResponse.json({ setupRequired: false, user })))
    const { router } = await mountWithPlugins(LoginPage, { attachTo: document.body })
    const replace = vi.spyOn(router, 'replace').mockResolvedValue(undefined)
    await router.push({ name: 'login', query: { redirect: 'https://example.com/' } })

    await fill('userName', 'admin')
    await fill('password', 'correct horse battery')
    await submit()

    await vi.waitFor(() => expect(replace).toHaveBeenCalledWith('/'))
  })
})

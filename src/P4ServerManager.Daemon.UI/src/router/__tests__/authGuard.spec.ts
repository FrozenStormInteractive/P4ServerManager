// Types
import type { AuthSession } from '@/api/types'

// Utilities
import { http, HttpResponse } from 'msw'
import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it } from 'vitest'
import { createMemoryHistory, createRouter } from 'vue-router'
import { authGuard, routes } from '@/router'
import { server } from '@/testing/server'

const user = { id: 'u1', userName: 'admin', isAdministrator: true, createdAt: '2026-11-14T10:00:00.000Z', lastSignInAt: null }

function serveSession (session: AuthSession) {
  server.use(http.get('/api/auth/session', () => HttpResponse.json(session)))
}

async function navigate (path: string) {
  const router = createRouter({ history: createMemoryHistory(), routes })
  router.beforeEach(authGuard)
  await router.push(path)
  return router.currentRoute.value
}

describe('authGuard', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
  })

  it('sends to the setup page while the daemon has no account', async () => {
    serveSession({ setupRequired: true, user: null })

    expect((await navigate('/instances')).name).toBe('setup')
  })

  it('sends to the sign-in page without a session, back to the page asked for afterwards', async () => {
    serveSession({ setupRequired: false, user: null })

    const route = await navigate('/settings/users')

    expect(route.name).toBe('login')
    expect(route.query.redirect).toBe('/settings/users')
  })

  it('keeps the setup page closed once an account exists', async () => {
    serveSession({ setupRequired: false, user: null })

    expect((await navigate('/setup')).name).toBe('login')
  })

  it('lets a signed-in user in, and away from the sign-in page', async () => {
    serveSession({ setupRequired: false, user })

    expect((await navigate('/instances')).name).toBe('instances')
    expect((await navigate('/login')).name).toBe('dashboard')
  })

  it('keeps the management of the accounts for administrators', async () => {
    serveSession({ setupRequired: false, user: { ...user, isAdministrator: false } })

    expect((await navigate('/settings/users')).name).toBe('dashboard')
    expect((await navigate('/settings/builds')).name).toBe('settings-builds')
  })

  it('lets an administrator manage the accounts', async () => {
    serveSession({ setupRequired: false, user })

    expect((await navigate('/settings/users')).name).toBe('settings-users')
  })

  it('shows the sign-in page when the daemon cannot be reached', async () => {
    server.use(http.get('/api/auth/session', () => HttpResponse.error()))

    expect((await navigate('/')).name).toBe('login')
  })
})

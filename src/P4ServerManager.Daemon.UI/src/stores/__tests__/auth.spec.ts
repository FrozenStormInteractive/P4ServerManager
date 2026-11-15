// Utilities
import { http, HttpResponse } from 'msw'
import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError, request, setUnauthorizedHandler } from '@/api/client'
import { useAuthStore } from '@/stores/auth'
import { server } from '@/testing/server'

const user = { id: 'u1', userName: 'admin', isAdministrator: true, createdAt: '2026-11-14T10:00:00.000Z', lastSignInAt: null }

describe('auth store', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
  })

  afterEach(() => {
    setUnauthorizedHandler(null)
  })

  it('reads the session from the daemon once', async () => {
    let calls = 0
    server.use(http.get('/api/auth/session', () => {
      calls++
      return HttpResponse.json({ setupRequired: false, user })
    }))
    const auth = useAuthStore()

    await Promise.all([auth.ensureSession(), auth.ensureSession()])
    await auth.ensureSession()

    expect(calls).toBe(1)
    expect(auth.user?.userName).toBe('admin')
  })

  it('forgets the user once signed out, even when the daemon cannot be reached', async () => {
    server.use(http.post('/api/auth/logout', () => HttpResponse.error()))
    const auth = useAuthStore()
    auth.session = { setupRequired: false, user }

    await expect(auth.logout()).rejects.toThrow()

    expect(auth.user).toBeNull()
  })

  it('calls the handler of ended sessions on a 401 outside of the authentication endpoints only', async () => {
    server.use(
      http.get('/api/instances', () => new HttpResponse(null, { status: 401 })),
      http.post('/api/auth/login', () => HttpResponse.json({ status: 401 }, { status: 401 })),
    )
    const handler = vi.fn()
    setUnauthorizedHandler(handler)

    await expect(request('POST', '/api/auth/login', {})).rejects.toBeInstanceOf(ApiError)
    expect(handler).not.toHaveBeenCalled()
    await expect(request('GET', '/api/instances')).rejects.toBeInstanceOf(ApiError)
    expect(handler).toHaveBeenCalledOnce()
  })
})

// Types
import type { AuthSession, ChangePasswordRequest, LoginRequest, SetupRequest, User } from '@/api/types'

// Utilities
import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import * as api from '@/api/auth'

const SIGNED_OUT: AuthSession = { setupRequired: false, user: null }

/**
 * The session of the user of the web interface. The router reads it before each navigation: the pages need a signed-in
 * user, except the sign-in page, and the setup page while the daemon has no account.
 */
export const useAuthStore = defineStore('auth', () => {
  const session = ref<AuthSession | null>(null)
  let pending: Promise<AuthSession> | null = null

  const user = computed(() => session.value?.user ?? null)
  const setupRequired = computed(() => session.value?.setupRequired ?? false)
  const isAdministrator = computed(() => user.value?.isAdministrator ?? false)

  /**
   * The session, read from the daemon the first time only.
   */
  async function ensureSession (): Promise<AuthSession> {
    if (session.value) {
      return session.value
    }
    pending ??= api.getSession()
      .then(value => {
        session.value = value
        return value
      })
      .finally(() => {
        pending = null
      })
    return pending
  }

  async function setup (body: SetupRequest) {
    session.value = await api.setup(body)
  }

  async function login (body: LoginRequest) {
    session.value = await api.login(body)
  }

  async function logout () {
    try {
      await api.logout()
    } finally {
      session.value = SIGNED_OUT
    }
  }

  async function changePassword (body: ChangePasswordRequest) {
    await api.changePassword(body)
  }

  /**
   * Takes in a change of the account of the signed-in user, such as its administrator right.
   */
  function userChanged (changed: User) {
    if (session.value?.user?.id === changed.id) {
      session.value = { ...session.value, user: changed }
    }
  }

  /**
   * Forgets the user once the daemon no longer accepts its session: it expired, its password changed or its account
   * was deleted.
   */
  function sessionEnded () {
    session.value = SIGNED_OUT
  }

  return {
    session,
    user,
    setupRequired,
    isAdministrator,
    ensureSession,
    setup,
    login,
    logout,
    changePassword,
    userChanged,
    sessionEnded,
  }
})

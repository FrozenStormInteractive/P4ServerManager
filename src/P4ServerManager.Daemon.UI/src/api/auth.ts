// Types
import type { AuthSession, ChangePasswordRequest, LoginRequest, SetupRequest } from './types'

// Utilities
import { request } from './client'

/**
 * Whether a user is signed in, and whether the daemon still waits for its first account. Needs no session.
 */
export function getSession (): Promise<AuthSession> {
  return request('GET', '/api/auth/session')
}

/**
 * Creates the first account and signs it in.
 */
export function setup (body: SetupRequest): Promise<AuthSession> {
  return request('POST', '/api/auth/setup', body)
}

export function login (body: LoginRequest): Promise<AuthSession> {
  return request('POST', '/api/auth/login', body)
}

export function logout (): Promise<void> {
  return request('POST', '/api/auth/logout')
}

/**
 * Changes the password of the signed-in user. Its other sessions end.
 */
export function changePassword (body: ChangePasswordRequest): Promise<void> {
  return request('PUT', '/api/auth/password', body)
}

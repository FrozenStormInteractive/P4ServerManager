// Types
import type { CreateUserRequest, SetUserPasswordRequest, User } from './types'

// Utilities
import { request } from './client'

export function listUsers (): Promise<User[]> {
  return request('GET', '/api/users')
}

export function createUser (body: CreateUserRequest): Promise<User> {
  return request('POST', '/api/users', body)
}

/**
 * Sets a new password on an account, without its current one. The sessions of the account end.
 */
export function setUserPassword (id: string, body: SetUserPasswordRequest): Promise<void> {
  return request('PUT', `/api/users/${encodeURIComponent(id)}/password`, body)
}

export function deleteUser (id: string): Promise<void> {
  return request('DELETE', `/api/users/${encodeURIComponent(id)}`)
}

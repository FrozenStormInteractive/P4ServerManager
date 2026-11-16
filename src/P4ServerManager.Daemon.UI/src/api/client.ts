// Types
import type { ProblemDetails } from './types'

/**
 * A request to the daemon failed. `problem` holds the problem details the daemon returned, if any.
 */
export class ApiError extends Error {
  readonly status: number
  readonly problem: ProblemDetails | null

  constructor (status: number, problem: ProblemDetails | null) {
    super(problem?.detail ?? problem?.title ?? `The request failed with status ${status}.`)
    this.name = 'ApiError'
    this.status = status
    this.problem = problem
  }

  /**
   * Validation errors by field, keyed by the JSON name of the field (`name`, `superUser.password`...).
   */
  get fieldErrors (): Record<string, string[]> {
    return this.problem?.errors ?? {}
  }
}

/**
 * Header the daemon asks of the requests that change something, so that pages of other origins cannot send them with
 * the session of the user.
 */
export const REQUEST_HEADER = 'X-P4SM-Request'

let unauthorizedHandler: (() => void) | null = null

/**
 * Sets what to do when the session ends: a request outside of `/api/auth` answered 401.
 */
export function setUnauthorizedHandler (handler: (() => void) | null) {
  unauthorizedHandler = handler
}

export async function request<T> (method: string, url: string, body?: unknown): Promise<T> {
  const response = await fetch(url, {
    method,
    headers: body === undefined
      ? { Accept: 'application/json', [REQUEST_HEADER]: '1' }
      : { 'Accept': 'application/json', 'Content-Type': 'application/json', [REQUEST_HEADER]: '1' },
    body: body === undefined ? undefined : JSON.stringify(body),
  })
  return readResponse<T>(url, response)
}

/**
 * Posts files as `multipart/form-data`. The browser sets the content type, with its boundary.
 */
export async function upload<T> (url: string, form: FormData): Promise<T> {
  const response = await fetch(url, {
    method: 'POST',
    headers: { Accept: 'application/json', [REQUEST_HEADER]: '1' },
    body: form,
  })
  return readResponse<T>(url, response)
}

async function readResponse<T> (url: string, response: Response): Promise<T> {
  const isJson = response.headers.get('Content-Type')?.includes('json') ?? false
  const content: unknown = isJson ? await response.json() : null
  if (!response.ok) {
    if (response.status === 401 && !url.startsWith('/api/auth/')) {
      unauthorizedHandler?.()
    }
    throw new ApiError(response.status, content as ProblemDetails | null)
  }
  return content as T
}

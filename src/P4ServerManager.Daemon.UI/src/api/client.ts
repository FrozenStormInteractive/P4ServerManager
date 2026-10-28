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

export async function request<T> (method: string, url: string, body?: unknown): Promise<T> {
  const response = await fetch(url, {
    method,
    headers: body === undefined
      ? { Accept: 'application/json' }
      : { 'Accept': 'application/json', 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  })
  return readResponse<T>(response)
}

/**
 * Posts files as `multipart/form-data`. The browser sets the content type, with its boundary.
 */
export async function upload<T> (url: string, form: FormData): Promise<T> {
  const response = await fetch(url, {
    method: 'POST',
    headers: { Accept: 'application/json' },
    body: form,
  })
  return readResponse<T>(response)
}

async function readResponse<T> (response: Response): Promise<T> {
  const isJson = response.headers.get('Content-Type')?.includes('json') ?? false
  const content: unknown = isJson ? await response.json() : null
  if (!response.ok) {
    throw new ApiError(response.status, content as ProblemDetails | null)
  }
  return content as T
}

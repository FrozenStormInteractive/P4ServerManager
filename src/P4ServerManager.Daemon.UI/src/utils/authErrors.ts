// Utilities
import { ApiError } from '@/api/client'

type Translate = (key: string) => string

/**
 * The message shown for a failed sign-in, setup or change of password: what the daemon said, a hint when it refused
 * too many attempts, or that it cannot be reached.
 */
export function submitErrorMessage (error: unknown, t: Translate): string {
  if (error instanceof ApiError) {
    return error.status === 429 ? t('auth.errors.tooManyAttempts') : error.message
  }
  return t('auth.errors.unreachable')
}

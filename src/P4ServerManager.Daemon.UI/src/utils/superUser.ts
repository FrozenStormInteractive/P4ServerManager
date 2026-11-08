// Types
import type { Instance, SuperUserStatus } from '@/api/types'

/**
 * Why the manager cannot administer the server of the instance: no super user is stored, or the last check of the
 * stored one failed. Null when the account is valid, or not checked yet.
 */
export type SuperUserProblem = 'missing' | Exclude<SuperUserStatus, 'Unchecked' | 'Valid'>

export function getSuperUserProblem (instance: Instance): SuperUserProblem | null {
  const status = instance.superUser?.status
  if (!status) {
    return 'missing'
  }
  return status === 'Valid' || status === 'Unchecked' ? null : status
}

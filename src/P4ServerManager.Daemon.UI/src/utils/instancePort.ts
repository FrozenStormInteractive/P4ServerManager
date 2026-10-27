// Types
import type { Instance } from '@/api/types'

/**
 * The port as clients give it in P4PORT: prefixed with `ssl:` when the instance uses SSL.
 */
export function formatPort (instance: Pick<Instance, 'port' | 'ssl'>): string {
  return instance.ssl ? `ssl:${instance.port}` : String(instance.port)
}

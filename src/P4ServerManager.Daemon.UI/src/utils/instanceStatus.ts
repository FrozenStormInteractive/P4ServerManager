// Types
import type { Instance } from '@/api/types'

/**
 * What the UI shows for an instance: its lifecycle state while it is not ready, then whether p4d runs.
 */
export type InstanceStatus = 'provisioning' | 'failed' | 'running' | 'stopped' | 'unknown'

export function getInstanceStatus (instance: Instance): InstanceStatus {
  if (instance.state === 'Provisioning') {
    return 'provisioning'
  }
  if (instance.state === 'Failed') {
    return 'failed'
  }
  switch (instance.runtimeStatus) {
    case 'Running': {
      return 'running'
    }
    case 'Stopped': {
      return 'stopped'
    }
    default: {
      return 'unknown'
    }
  }
}

const colors: Record<InstanceStatus, string> = {
  provisioning: 'info',
  failed: 'error',
  running: 'success',
  stopped: 'grey',
  unknown: 'grey',
}

export function getInstanceStatusColor (status: InstanceStatus): string {
  return colors[status]
}

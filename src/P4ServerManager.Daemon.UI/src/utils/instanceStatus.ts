// Types
import type { Instance } from '@/api/types'

/**
 * What the UI shows for an instance: its lifecycle state while it is not ready, then whether p4d runs, or is being
 * started, stopped or restarted by the daemon.
 */
export type InstanceStatus
  = | 'provisioning'
    | 'failed'
    | 'running'
    | 'stopped'
    | 'starting'
    | 'stopping'
    | 'restarting'
    | 'unknown'

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
    case 'Starting': {
      return 'starting'
    }
    case 'Stopping': {
      return 'stopping'
    }
    case 'Restarting': {
      return 'restarting'
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
  starting: 'info',
  stopping: 'info',
  restarting: 'info',
  unknown: 'grey',
}

export function getInstanceStatusColor (status: InstanceStatus): string {
  return colors[status]
}

const inProgressStatuses = new Set<InstanceStatus>(['provisioning', 'starting', 'stopping', 'restarting'])

/**
 * Whether the daemon is working on the instance: no other action can be asked for meanwhile.
 */
export function isInstanceStatusInProgress (status: InstanceStatus): boolean {
  return inProgressStatuses.has(status)
}

// Types
import type { Instance } from '@/api/types'

/**
 * What the UI shows for an instance: its lifecycle state while it is not ready, then whether p4d runs, or is being
 * started, stopped, restarted, upgraded, converted to Unicode mode, checked, given another server ID or port, backed
 * up, or deleted by the daemon.
 */
export type InstanceStatus
  = | 'provisioning'
    | 'failed'
    | 'running'
    | 'stopped'
    | 'starting'
    | 'stopping'
    | 'restarting'
    | 'upgrading'
    | 'convertingToUnicode'
    | 'checkingHealth'
    | 'changingServerId'
    | 'changingPort'
    | 'backingUp'
    | 'verifyingBackup'
    | 'deleting'
    | 'unknown'

export function getInstanceStatus (instance: Instance): InstanceStatus {
  // A failed instance can be deleted too.
  if (instance.runtimeStatus === 'Deleting') {
    return 'deleting'
  }
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
    case 'Upgrading': {
      return 'upgrading'
    }
    case 'ConvertingToUnicode': {
      return 'convertingToUnicode'
    }
    case 'CheckingHealth': {
      return 'checkingHealth'
    }
    case 'ChangingServerId': {
      return 'changingServerId'
    }
    case 'ChangingPort': {
      return 'changingPort'
    }
    case 'BackingUp': {
      return 'backingUp'
    }
    case 'VerifyingBackup': {
      return 'verifyingBackup'
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
  upgrading: 'info',
  convertingToUnicode: 'info',
  checkingHealth: 'info',
  changingServerId: 'info',
  changingPort: 'info',
  backingUp: 'info',
  verifyingBackup: 'info',
  deleting: 'error',
  unknown: 'grey',
}

/**
 * Color of the status of an instance. A server that runs in maintenance mode stands out from one that serves its users
 * normally. While it is stopped or busy, it keeps the color of its status.
 */
export function getInstanceColor (instance: Instance): string {
  const status = getInstanceStatus(instance)
  return instance.maintenanceMode && status === 'running' ? 'warning' : colors[status]
}

const inProgressStatuses = new Set<InstanceStatus>([
  'provisioning',
  'starting',
  'stopping',
  'restarting',
  'upgrading',
  'convertingToUnicode',
  'checkingHealth',
  'changingServerId',
  'changingPort',
  'backingUp',
  'verifyingBackup',
  'deleting',
])

/**
 * Whether the daemon is working on the instance: no other action can be asked for meanwhile.
 */
export function isInstanceStatusInProgress (status: InstanceStatus): boolean {
  return inProgressStatuses.has(status)
}

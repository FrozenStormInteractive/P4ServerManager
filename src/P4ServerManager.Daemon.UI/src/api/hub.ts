// Types
import type { BackupRun, Build, BuildArchiveStatus, HealthCheckRun, Instance, OperationSnapshot, RestoreRun } from './types'
import type { HubConnection } from '@microsoft/signalr'

// Utilities
import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr'

export interface InstancesHubHandlers {
  onInstanceUpdated: (instance: Instance) => void
  onInstanceDeleted: (instanceId: string) => void
  onOperationProgress: (snapshot: OperationSnapshot) => void
  onHealthCheckUpdated: (run: HealthCheckRun) => void
  onHealthCheckDeleted: (instanceId: string, runId: string) => void
  onBackupUpdated: (run: BackupRun) => void
  onRestoreUpdated: (run: RestoreRun) => void
  /**
   * Called on the first connection and after each reconnection: events may have been missed until then.
   */
  onConnected: () => void
}

export interface BuildsHubHandlers {
  onBuildUpdated: (build: Build) => void
  onBuildDeleted: (buildId: string) => void
  onArchiveStatusChanged: (status: BuildArchiveStatus) => void
  /**
   * Called on the first connection and after each reconnection: events may have been missed until then.
   */
  onConnected: () => void
}

export interface InstancesHub {
  stop: () => Promise<void>
}

export type BuildsHub = InstancesHub

const RETRY_DELAY_MS = 5000

/**
 * Connects to the `/hubs/instances` hub of the daemon.
 */
export function connectInstancesHub (handlers: InstancesHubHandlers): InstancesHub {
  return connectHub('/hubs/instances', handlers.onConnected, connection => {
    connection.on('InstanceUpdated', handlers.onInstanceUpdated)
    connection.on('InstanceDeleted', handlers.onInstanceDeleted)
    connection.on('OperationProgress', handlers.onOperationProgress)
    connection.on('HealthCheckUpdated', handlers.onHealthCheckUpdated)
    connection.on('HealthCheckDeleted', handlers.onHealthCheckDeleted)
    connection.on('BackupUpdated', handlers.onBackupUpdated)
    connection.on('RestoreUpdated', handlers.onRestoreUpdated)
  })
}

/**
 * Connects to the `/hubs/builds` hub of the daemon.
 */
export function connectBuildsHub (handlers: BuildsHubHandlers): BuildsHub {
  return connectHub('/hubs/builds', handlers.onConnected, connection => {
    connection.on('BuildUpdated', handlers.onBuildUpdated)
    connection.on('BuildDeleted', handlers.onBuildDeleted)
    connection.on('ArchiveStatusChanged', handlers.onArchiveStatusChanged)
  })
}

/**
 * Connects to a hub of the daemon. Connecting is retried until it succeeds, and again whenever SignalR gives up
 * reconnecting, for example while the daemon restarts.
 */
function connectHub (
  url: string,
  onConnected: () => void,
  subscribe: (connection: HubConnection) => void,
): InstancesHub {
  const connection = new HubConnectionBuilder()
    .withUrl(url)
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build()

  let stopped = false
  let retry: ReturnType<typeof setTimeout> | undefined

  async function start () {
    try {
      await connection.start()
      onConnected()
    } catch {
      scheduleStart()
    }
  }

  function scheduleStart () {
    if (!stopped) {
      retry = setTimeout(start, RETRY_DELAY_MS)
    }
  }

  subscribe(connection)
  connection.onreconnected(() => onConnected())
  connection.onclose(() => scheduleStart())

  void start()

  return {
    async stop () {
      stopped = true
      clearTimeout(retry)
      if (connection.state !== HubConnectionState.Disconnected) {
        await connection.stop()
      }
    },
  }
}

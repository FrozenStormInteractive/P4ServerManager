// Types
import type { Instance, OperationSnapshot } from './types'

// Utilities
import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr'

export interface InstancesHubHandlers {
  onInstanceUpdated: (instance: Instance) => void
  onOperationProgress: (snapshot: OperationSnapshot) => void
  /**
   * Called on the first connection and after each reconnection: events may have been missed until then.
   */
  onConnected: () => void
}

export interface InstancesHub {
  stop: () => Promise<void>
}

const RETRY_DELAY_MS = 5000

/**
 * Connects to the `/hubs/instances` hub of the daemon. Connecting is retried until it succeeds, and again whenever
 * SignalR gives up reconnecting, for example while the daemon restarts.
 */
export function connectInstancesHub (handlers: InstancesHubHandlers): InstancesHub {
  const connection = new HubConnectionBuilder()
    .withUrl('/hubs/instances')
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build()

  let stopped = false
  let retry: ReturnType<typeof setTimeout> | undefined

  async function start () {
    try {
      await connection.start()
      handlers.onConnected()
    } catch {
      scheduleStart()
    }
  }

  function scheduleStart () {
    if (!stopped) {
      retry = setTimeout(start, RETRY_DELAY_MS)
    }
  }

  connection.on('InstanceUpdated', handlers.onInstanceUpdated)
  connection.on('OperationProgress', handlers.onOperationProgress)
  connection.onreconnected(() => handlers.onConnected())
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

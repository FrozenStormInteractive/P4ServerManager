/**
 * Types of the daemon REST API and SignalR hub. The JSON names are camelCase and the enums are strings.
 */

export type InstanceState = 'Provisioning' | 'Ready' | 'Failed'

export type RuntimeStatus = 'Unknown' | 'Running' | 'Stopped' | 'Starting' | 'Stopping' | 'Restarting' | 'Deleting'

export interface Instance {
  id: string
  name: string
  description: string | null
  port: number
  rootPath: string
  release: string
  version: string | null
  build: string | null
  caseSensitive: boolean
  unicode: boolean
  autoStart: boolean
  state: InstanceState
  runtimeStatus: RuntimeStatus
  lastError: string | null
  createdAt: string
}

export interface SuperUser {
  name: string
  password: string
}

export interface CreateInstanceRequest {
  name: string
  description?: string | null
  release: string
  port: number
  caseSensitive: boolean
  unicode: boolean
  autoStart: boolean
  rootPath?: string | null
  superUser: SuperUser
}

/**
 * Settings that can change after the creation. A missing setting is left as it is.
 */
export interface UpdateInstanceRequest {
  autoStart?: boolean
}

export interface CachedBuild {
  build: string
  downloadedAt: string
}

export interface P4dVersion {
  release: string
  version: string
  cachedBuilds: CachedBuild[]
}

export type OperationStatus = 'Running' | 'Succeeded' | 'Failed'

export type OperationStepStatus = 'Pending' | 'Running' | 'Succeeded' | 'Failed' | 'Skipped'

export interface OperationStepState {
  /**
   * Stable identifier, such as `resolve-build`, used to translate the step.
   */
  key: string
  /**
   * Title in English, shown when the step has no translation.
   */
  title: string
  status: OperationStepStatus
  message: string | null
}

/**
 * Progress of a long operation on an instance, such as its provisioning.
 */
export interface OperationSnapshot {
  instanceId: string
  /**
   * What the operation does, such as `provision`.
   */
  kind: string
  status: OperationStatus
  steps: OperationStepState[]
  currentStep: string | null
  percent: number | null
  error: string | null
}

/**
 * RFC 9457 problem details. `errors` holds the validation errors by field.
 */
export interface ProblemDetails {
  type?: string
  title?: string
  status?: number
  detail?: string
  errors?: Record<string, string[]>
}

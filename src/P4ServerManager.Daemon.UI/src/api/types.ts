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
  /**
   * True when p4d listens with SSL, on `ssl:` followed by the port.
   */
  ssl: boolean
  /**
   * Fingerprint of the certificate of the server, that clients check before they trust it. Null without SSL.
   */
  sslFingerprint: string | null
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
  ssl: boolean
  /**
   * Folder of the server: the root is its `root` subfolder, next to `journals`, `logs` and `archives`.
   */
  serverDirectory?: string | null
  superUser: SuperUser
}

/**
 * Settings that can change after the creation. A missing setting is left as it is.
 */
export interface UpdateInstanceRequest {
  autoStart?: boolean
}

/**
 * A server profile of the HelixCoreServer Synology package (p4dctl-ng), waiting to be imported.
 */
export interface LegacyProfile {
  /**
   * File name of the profile.
   */
  id: string
  name: string | null
  rootPath: string | null
  port: number | null
  /**
   * False when the profile ran p4d with `-C1`. Only a suggestion: the database does not record it.
   */
  caseSensitive: boolean
  autoStart: boolean
  /**
   * Settings of the profile an imported instance does not keep, such as P4JOURNAL.
   */
  ignoredSettings: string[]
  /**
   * Why the profile cannot be imported. Empty when it can.
   */
  problems: string[]
  /**
   * The instance that uses the root of the profile, once imported.
   */
  instanceId: string | null
}

/**
 * Settings of the instance that takes over the server of a legacy profile. The root comes from the profile, and the
 * Unicode mode from its database.
 */
export interface ImportLegacyProfileRequest {
  name: string
  description?: string | null
  release: string
  port: number
  caseSensitive: boolean
  autoStart: boolean
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

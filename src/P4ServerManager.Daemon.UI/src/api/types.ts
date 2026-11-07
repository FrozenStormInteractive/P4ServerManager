/**
 * Types of the daemon REST API and SignalR hub. The JSON names are camelCase and the enums are strings.
 */

export type InstanceState = 'Provisioning' | 'Ready' | 'Failed'

export type RuntimeStatus
  = | 'Unknown'
    | 'Running'
    | 'Stopped'
    | 'Starting'
    | 'Stopping'
    | 'Restarting'
    | 'Deleting'
    | 'Upgrading'

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

/**
 * The instances a start, a stop or a restart applies to: the ones listed, or every ready instance.
 */
export type InstanceSelection = { instanceIds: string[] } | { all: true }

export type InstanceAction = 'start' | 'stop' | 'restart'

/**
 * `Skipped` when there was nothing to do: the instance already ran for a start, was already stopped for a stop, or was
 * stopped for a restart of all the instances.
 */
export type InstanceActionOutcome = 'Succeeded' | 'Skipped' | 'Failed'

/**
 * Result of a start, a stop or a restart on one instance of a selection.
 */
export interface InstanceActionResult {
  /**
   * The instance once the action ended.
   */
  instance: Instance
  outcome: InstanceActionOutcome
  /**
   * Why the action failed. Null otherwise.
   */
  error: string | null
}

export interface SuperUser {
  name: string
  password: string
}

export interface CreateInstanceRequest {
  name: string
  description?: string | null
  release: string
  /**
   * Exact build of the release from the archive. The latest build when missing.
   */
  build?: string
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
  name?: string
  /** An empty text removes the description. */
  description?: string
  autoStart?: boolean
}

/**
 * The p4d release an instance moves to: its own release, for a newer patch, or a newer one.
 */
export interface UpgradeInstanceRequest {
  release: string
  /**
   * Exact build of the release from the archive, no older than the build of the instance. The latest build when
   * missing.
   */
  build?: string
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
   * True when the server listens with SSL. An imported instance keeps it, with the key and the certificate of its root.
   */
  ssl: boolean
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
  /**
   * True when the build is extracted, ready to run, rather than only archived.
   */
  extracted: boolean
}

export interface P4dVersion {
  release: string
  version: string
  cachedBuilds: CachedBuild[]
}

export type BuildSource = 'Ftp' | 'Imported'

/**
 * A p4d build of the archive, kept compressed, and extracted while an instance uses it.
 */
export interface Build {
  id: string
  release: string
  version: string
  build: string
  source: BuildSource
  sha256: string
  addedAt: string
  /**
   * Size of the compressed archive in bytes. Null while the build is not archived yet.
   */
  archiveSize: number | null
  extracted: boolean
  /**
   * True when the retention policy never removes the build.
   */
  protected: boolean
  instances: { id: string, name: string }[]
}

export type BuildArchiveScope = 'AllReleases' | 'SelectedReleases'

/**
 * How the daemon fills the archive: which new builds of the Perforce FTP it downloads, how often it checks, and how
 * many builds of each release it keeps.
 */
export interface BuildArchivePolicy {
  autoDownload: boolean
  checkIntervalHours: number
  scope: BuildArchiveScope
  releases: string[]
  /**
   * Null to keep every build.
   */
  keepLatestPerRelease: number | null
}

export type BuildFetchState = 'Queued' | 'Checking' | 'Downloading' | 'Compressing'

/**
 * A build being fetched from Perforce into the archive.
 */
export interface BuildFetch {
  release: string
  state: BuildFetchState
  /**
   * `p4d` or `p4`, while downloading.
   */
  fileName: string | null
  /**
   * Version and build, such as `2025.1/2850123`, once known.
   */
  build: string | null
  percent: number | null
}

export interface BuildArchiveStatus {
  running: boolean
  /**
   * What the archive worker does, such as `Checking r25.1`. Null when idle.
   */
  activity: string | null
  percent: number | null
  lastCheckAt: string | null
  lastCheckError: string | null
  /**
   * Null when the automatic download is off.
   */
  nextCheckAt: string | null
  lastDownloadError: string | null
  /**
   * True while the worker checks Perforce for new builds, or is about to.
   */
  checking: boolean
  /**
   * The builds being fetched from Perforce: the current one first, then those waiting their turn.
   */
  fetches: BuildFetch[]
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
  /**
   * How the operation ended, once it succeeded, for an operation that can succeed in more than one way: for an upgrade,
   * `upgraded`, or `up-to-date` when the instance already ran the latest build and nothing was changed. Null otherwise.
   */
  outcome: string | null
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

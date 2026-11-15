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
    | 'ConvertingToUnicode'
    | 'CheckingHealth'
    | 'ChangingServerId'
    | 'ChangingPort'
    | 'BackingUp'
    | 'VerifyingBackup'
    | 'Restoring'

export interface Instance {
  id: string
  name: string
  /**
   * Server ID of p4d. A new server takes the name of the instance; it changes with `PUT /api/instances/{id}/server-id`
   * only. Null until it is known.
   */
  serverId: string | null
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
  /**
   * True when p4d runs, or starts, in maintenance mode (`p4d -n`): it only allows the commands that do not need a
   * client workspace. Every start of p4d follows it until its maintenance mode is disabled.
   */
  maintenanceMode: boolean
  state: InstanceState
  runtimeStatus: RuntimeStatus
  lastError: string | null
  /**
   * Super user account the daemon stores to administer the server. Null when none is stored, as for an imported
   * instance.
   */
  superUser: StoredSuperUser | null
  createdAt: string
}

/**
 * Result of the last check of the stored super user: `Unchecked` until the server ran since it was stored.
 */
export type SuperUserStatus = 'Unchecked' | 'Valid' | 'InvalidCredentials' | 'PasswordExpired' | 'InsufficientAccess'

/**
 * Super user account stored for an instance. Its password is never returned.
 */
export interface StoredSuperUser {
  userName: string
  status: SuperUserStatus
  /**
   * Date of the last check that reached the server. Null until checked.
   */
  checkedAt: string | null
  /**
   * Message of p4 when the last check failed.
   */
  error: string | null
}

/**
 * Another account, or the new password of the same one after it was changed in P4. Nothing changes in P4.
 */
export interface SetSuperUserRequest {
  userName: string
  password: string
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
 * New server ID of the p4d of an instance: a letter, then up to 31 letters, digits, '-' or '_'.
 */
export interface ChangeServerIdRequest {
  serverId: string
}

/**
 * New TCP port of the p4d of an instance, between 1024 and 65535, that no other instance uses and that is free on the
 * machine.
 */
export interface ChangePortRequest {
  port: number
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
  /**
   * Every step, in order, including the ones that will be skipped. The same step, with the same key, can be there more
   * than once.
   */
  steps: OperationStepState[]
  /**
   * Position in `steps` of the step running, or null between steps and once finished.
   */
  currentStepIndex: number | null
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

/**
 * `Validate` runs `p4d -xv`, `CrossCheck` runs `p4d -xx`.
 */
export type HealthCheckKind = 'Validate' | 'CrossCheck'

/**
 * `Healthy` and `ProblemsFound` tell the health of the instance. The others say nothing of its database: a check could
 * not run (`Failed`), or was cut short (`Canceled`, `Interrupted`).
 */
export type HealthCheckStatus = 'Running' | 'Healthy' | 'ProblemsFound' | 'Failed' | 'Canceled' | 'Interrupted'

export interface HealthCheckFinding {
  check: HealthCheckKind
  /**
   * The table or the pair of tables, when p4d names it.
   */
  table: string | null
  message: string
}

export interface HealthCheckProgress {
  check: HealthCheckKind
  /**
   * The table or the pair of tables the check is on.
   */
  item: string | null
  itemsChecked: number
  /**
   * Progress of the whole run. It counts tables, not time, from an estimate of their number.
   */
  percent: number
}

/**
 * A health check of an instance, running or kept as a report.
 */
export interface HealthCheckRun {
  id: string
  instanceId: string
  status: HealthCheckStatus
  checks: HealthCheckKind[]
  p4dVersion: string
  startedAt: string
  finishedAt: string | null
  error: string | null
  hasFixJournal: boolean
  progress: HealthCheckProgress | null
  findings: HealthCheckFinding[]
  /**
   * The report was deleted while it still tells the health of the instance: it is left out of the list of reports. The
   * daemon deletes it once a later check replaces it.
   */
  reportHidden: boolean
}

export type BackupDestinationKind = 'Local' | 'S3' | 'S3Compatible' | 'GoogleCloudStorage' | 'AzureBlob' | 'Ftp'

export type FtpEncryption = 'Explicit' | 'Implicit' | 'None'

export interface LocalDestinationSettings {
  /**
   * Absolute path of the folder, on the machine of the daemon.
   */
  path: string
}

export interface S3DestinationSettings {
  bucket: string
  prefix: string | null
  region: string
  /**
   * URL of the S3-compatible API. Null for Amazon S3.
   */
  endpoint: string | null
  forcePathStyle: boolean
  accessKeyId: string
  storageClass: string | null
}

export interface GcsDestinationSettings {
  bucket: string
  prefix: string | null
}

export interface AzureBlobDestinationSettings {
  accountUrl: string
  container: string
  prefix: string | null
}

export interface FtpDestinationSettings {
  host: string
  port: number | null
  encryption: FtpEncryption
  path: string
  userName: string
  /**
   * SHA-256 fingerprint of a self-signed certificate to trust. Null to validate the certificate as usual.
   */
  certificateFingerprint: string | null
}

/**
 * Secrets of a destination: only ever sent, never returned.
 */
export interface BackupDestinationCredentials {
  secretAccessKey?: string | null
  serviceAccountJson?: string | null
  accountKey?: string | null
  sasToken?: string | null
  password?: string | null
}

/**
 * A place where the backups of the instances go: an encrypted backup repository on a storage. Exactly the settings of
 * its kind are set.
 */
export interface BackupDestination {
  id: string
  name: string
  kind: BackupDestinationKind
  /**
   * Where the repository is, such as `s3://bucket/prefix`. Never a credential.
   */
  location: string
  local: LocalDestinationSettings | null
  s3: S3DestinationSettings | null
  gcs: GcsDestinationSettings | null
  azure: AzureBlobDestinationSettings | null
  ftp: FtpDestinationSettings | null
  hasCredentials: boolean
  repositoryId: string
  instanceCount: number
  createdAt: string
  updatedAt: string
}

export interface BackupDestinationSettings {
  local?: LocalDestinationSettings | null
  s3?: S3DestinationSettings | null
  gcs?: GcsDestinationSettings | null
  azure?: AzureBlobDestinationSettings | null
  ftp?: FtpDestinationSettings | null
}

export interface CreateBackupDestinationRequest extends BackupDestinationSettings {
  name: string
  kind: BackupDestinationKind
  credentials: BackupDestinationCredentials | null
  passphrase: string
}

export interface UpdateBackupDestinationRequest extends BackupDestinationSettings {
  name: string
  /**
   * Null keeps the stored secrets.
   */
  credentials: BackupDestinationCredentials | null
}

export interface BackupDestinationTestResult {
  succeeded: boolean
  error: string | null
  capabilities: string[]
}

export type BackupKind = 'Full' | 'Incremental'

export type BackupTrigger = 'Manual' | 'Scheduled'

export type BackupRunStatus = 'Running' | 'Succeeded' | 'PartiallySucceeded' | 'Failed' | 'Canceled' | 'Interrupted'

export type BackupDestinationStatus = 'Pending' | 'Uploading' | 'Succeeded' | 'Failed'

export interface BackupVerification {
  verifiedAt: string
  deep: boolean
  succeeded: boolean
  checkedObjects: number
  problemCount: number
  problems: string[]
  error: string | null
}

export interface BackupRunDestination {
  destinationId: string
  destinationName: string
  status: BackupDestinationStatus
  uploadedObjects: number
  uploadedBytes: number
  snapshotKey: string | null
  error: string | null
  lastVerification: BackupVerification | null
}

/**
 * A backup of an instance, running or ended. It is kept after the instance is deleted.
 */
export interface BackupRun {
  id: string
  instanceId: string
  instanceName: string
  kind: BackupKind
  trigger: BackupTrigger
  status: BackupRunStatus
  startedAt: string
  finishedAt: string | null
  checkpointNumber: number | null
  journalNumber: number | null
  fileCount: number
  totalBytes: number
  error: string | null
  /**
   * What the backup could not take, though it succeeded, such as the archives of a depot in an object store.
   */
  warnings: string[]
  destinations: BackupRunDestination[]
}

/**
 * A backup point stored at a destination, read from the destination itself: it lists the points of instances this
 * daemon never knew, such as after a reinstallation.
 */
export interface BackupPoint {
  /**
   * Key of the manifest of the point: what a restore asks for.
   */
  key: string
  id: string
  sourceId: string
  sourceName: string
  kind: BackupKind
  createdAt: string
  checkpointNumber: number | null
  journalNumber: number | null
  p4dVersion: string | null
  unicode: boolean
  caseSensitive: boolean
  serverId: string | null
}

export type RestoreTarget = 'ExistingInstance' | 'NewInstance'

export type RestoreRunStatus = 'Running' | 'Succeeded' | 'SucceededWithProblems' | 'Failed' | 'Canceled' | 'Interrupted'

/**
 * A restore of a backup point, running or ended.
 */
export interface RestoreRun {
  id: string
  instanceId: string
  instanceName: string
  target: RestoreTarget
  destinationId: string
  destinationName: string
  snapshotKey: string
  pointId: string
  pointCreatedAt: string | null
  sourceName: string | null
  status: RestoreRunStatus
  startedAt: string
  finishedAt: string | null
  fileCount: number
  totalBytes: number
  downloadedBytes: number
  /**
   * Files the instance already had, unchanged, put back rather than downloaded.
   */
  reusedFiles: number
  verifyProblemCount: number
  verifyProblems: string[]
  warnings: string[]
  error: string | null
}

export interface RestoreInstanceRequest {
  name: string
  description: string | null
  port: number
  release: string | null
  build: string | null
  serverDirectory: string | null
  ssl: boolean
  autoStart: boolean
  includeLicense: boolean
  superUser: { name: string, password: string }
  source: { destinationId: string, snapshotKey: string }
}

/**
 * A folder of the machine of the daemon.
 */
export interface DirectoryEntry {
  /**
   * Name of the folder, or its path for a root.
   */
  name: string
  path: string
  /**
   * Whether the daemon may create files and folders in it. Other folders are only shown, to reach their subfolders.
   */
  writable: boolean
}

/**
 * The subfolders of a folder of the machine of the daemon, to pick a path.
 */
export interface DirectoryListing {
  /**
   * Absolute path of the folder, normalized.
   */
  path: string
  /**
   * Folder that holds it, or null for a root.
   */
  parent: string | null
  /**
   * Whether the daemon may create files and folders in the folder.
   */
  writable: boolean
  /**
   * The folders from its root down to the folder itself.
   */
  ancestors: DirectoryEntry[]
  /**
   * Its subfolders, by name, without the hidden ones.
   */
  directories: DirectoryEntry[]
  /**
   * The roots of the file system: `/`, or the drives on Windows.
   */
  roots: DirectoryEntry[]
}

/**
 * An account of the web interface. The password is never returned.
 */
export interface User {
  id: string
  userName: string
  /**
   * Whether the user manages the accounts.
   */
  isAdministrator: boolean
  createdAt: string
  /**
   * When the user last signed in, or null if never.
   */
  lastSignInAt: string | null
}

/**
 * Whether a user is signed in, and whether the daemon still waits for its first account.
 */
export interface AuthSession {
  /**
   * No account exists yet: the first one is created from the setup page.
   */
  setupRequired: boolean
  /**
   * The signed-in user, or null.
   */
  user: User | null
}

export interface SetupRequest {
  userName: string
  password: string
}

export interface LoginRequest {
  userName: string
  password: string
  /**
   * Keeps the session after the browser closes.
   */
  rememberMe: boolean
}

export interface ChangePasswordRequest {
  currentPassword: string
  newPassword: string
}

export interface CreateUserRequest {
  userName: string
  password: string
  isAdministrator?: boolean
}

export interface SetUserAdministratorRequest {
  isAdministrator: boolean
}

export interface SetUserPasswordRequest {
  password: string
}

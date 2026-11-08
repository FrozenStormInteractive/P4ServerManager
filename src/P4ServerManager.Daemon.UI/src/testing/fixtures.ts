// Types
import type { Build, BuildArchiveStatus, HealthCheckRun, Instance, OperationSnapshot } from '@/api/types'

export function createInstance (overrides: Partial<Instance> = {}): Instance {
  return {
    id: '5f0079c4-12bf-4f3e-b7f5-3d8677fb10bc',
    name: 'main',
    description: null,
    port: 1666,
    ssl: false,
    sslFingerprint: null,
    rootPath: '/volume1/p4/main/root',
    release: 'r25.2',
    version: '2025.2',
    build: '3086021',
    caseSensitive: true,
    unicode: false,
    autoStart: true,
    maintenanceMode: false,
    state: 'Ready',
    runtimeStatus: 'Running',
    lastError: null,
    createdAt: '2026-10-21T23:06:43.318Z',
    ...overrides,
  }
}

export function createSnapshot (instanceId: string, overrides: Partial<OperationSnapshot> = {}): OperationSnapshot {
  return {
    instanceId,
    kind: 'provision',
    status: 'Running',
    steps: [
      { key: 'resolve-build', title: 'Get the p4d build', status: 'Running', message: 'Downloading p4d (42%)' },
      { key: 'prepare-directories', title: 'Prepare the directories', status: 'Pending', message: null },
      { key: 'initialize-database', title: 'Initialize the database', status: 'Pending', message: null },
      { key: 'start-server', title: 'Start the server', status: 'Pending', message: null },
      { key: 'create-super-user', title: 'Create the super user', status: 'Skipped', message: null },
    ],
    currentStep: 'resolve-build',
    percent: 42,
    error: null,
    outcome: null,
    ...overrides,
  }
}

export function createBuild (overrides: Partial<Build> = {}): Build {
  return {
    id: '0c6f4a3e-2a51-4b8e-9d55-0c1f5b1e9a01',
    release: 'r25.2',
    version: '2025.2',
    build: '3086021',
    source: 'Ftp',
    sha256: 'a'.repeat(64),
    addedAt: '2026-10-21T23:06:43.318Z',
    archiveSize: 13_000_000,
    extracted: true,
    protected: false,
    instances: [],
    ...overrides,
  }
}

export function createArchiveStatus (overrides: Partial<BuildArchiveStatus> = {}): BuildArchiveStatus {
  return {
    running: false,
    activity: null,
    percent: null,
    lastCheckAt: null,
    lastCheckError: null,
    nextCheckAt: null,
    lastDownloadError: null,
    checking: false,
    fetches: [],
    ...overrides,
  }
}

export function createHealthCheckRun (instanceId: string, overrides: Partial<HealthCheckRun> = {}): HealthCheckRun {
  return {
    id: '8b1f5c2e-6d0a-4e7b-9c3d-2a4f1e0b7c11',
    instanceId,
    status: 'Healthy',
    checks: ['Validate', 'CrossCheck'],
    p4dVersion: '2026.1/3089746',
    startedAt: '2026-11-01T00:00:00.000Z',
    finishedAt: '2026-11-01T00:02:05.000Z',
    error: null,
    hasFixJournal: false,
    progress: null,
    findings: [],
    reportHidden: false,
    ...overrides,
  }
}

// Types
import type { Instance, OperationSnapshot } from '@/api/types'

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
    ...overrides,
  }
}

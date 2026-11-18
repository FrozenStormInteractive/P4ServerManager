// Types
import type { Instance } from '@/api/types'
import type { RestoreSource } from '@/utils/restore'

// Utilities
import { flushPromises } from '@vue/test-utils'
import { http, HttpResponse } from 'msw'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h } from 'vue'
import { VApp } from 'vuetify/components'
import RestoreDialog from '@/components/backups/RestoreDialog.vue'
import { createInstance } from '@/testing/fixtures'
import { mountWithPlugins } from '@/testing/mount'
import { server } from '@/testing/server'

const source: RestoreSource = {
  destinationId: 'destination-1',
  destinationName: 'nas',
  snapshotKey: 'snapshots/aaaa/20261113T080000000Z-bbbb',
  sourceName: 'main',
  createdAt: '2026-11-13T08:00:00Z',
  kind: 'Incremental',
}

function field (name: string): HTMLInputElement {
  return document.body.querySelector<HTMLInputElement>(`.v-dialog input[name="${CSS.escape(name)}"]`)!
}

async function type (name: string, value: string) {
  const input = field(name)
  input.value = value
  input.dispatchEvent(new Event('input'))
  await flushPromises()
}

function button (text: string): HTMLButtonElement {
  return Array.from(document.body.querySelectorAll<HTMLButtonElement>('.v-dialog button'))
    .find(element => element.textContent?.trim() === text)!
}

async function mountDialog (instance: Instance | null, onStarted = vi.fn()) {
  const InApp = defineComponent(() => () => h(VApp, () => h(RestoreDialog, { modelValue: true, source, instance, onStarted })))
  const result = await mountWithPlugins(InApp, { attachTo: document.body })
  await flushPromises()
  return result
}

describe('RestoreDialog', () => {
  beforeEach(() => {
    server.use(http.get('/api/instances/server-directory', () => HttpResponse.json({
      path: '/volume1/P4ServerManager/restored',
      isDefault: true,
      caseVariant: null,
    })))
  })

  afterEach(() => {
    document.body.innerHTML = ''
  })

  it('replaces the data of the instance only once its name is typed again', async () => {
    const instance = createInstance({ id: 'instance-1', name: 'main' })
    let body: unknown = null
    server.use(http.post('/api/instances/instance-1/restores', async ({ request }) => {
      body = await request.json()
      return HttpResponse.json({
        id: 'restore-1',
        instanceId: 'instance-1',
        instanceName: 'main',
        target: 'ExistingInstance',
        destinationId: source.destinationId,
        destinationName: 'nas',
        snapshotKey: source.snapshotKey,
        pointId: 'bbbb',
        pointCreatedAt: source.createdAt,
        sourceName: 'main',
        status: 'Running',
        startedAt: '2026-11-13T09:00:00Z',
        finishedAt: null,
        fileCount: 0,
        totalBytes: 0,
        downloadedBytes: 0,
        reusedFiles: 0,
        verifyProblemCount: 0,
        verifyProblems: [],
        warnings: [],
        error: null,
      }, { status: 202 })
    }))
    const onStarted = vi.fn()
    await mountDialog(instance, onStarted)

    expect(document.body.textContent).toContain('Incremental of main')
    await type('confirmation', 'mian')
    button('Restore').click()
    await flushPromises()
    expect(body).toBeNull()
    expect(onStarted).not.toHaveBeenCalled()

    await type('confirmation', 'main')
    button('Restore').click()
    await flushPromises()

    expect(body).toEqual({ destinationId: source.destinationId, snapshotKey: source.snapshotKey })
    // The dialog that opened this one closes too.
    expect(onStarted).toHaveBeenCalledOnce()
  })

  it('restores into a new instance with the super user of the backed-up server, and opens it', async () => {
    let body: unknown = null
    server.use(http.post('/api/instances/restore', async ({ request }) => {
      body = await request.json()
      return HttpResponse.json(createInstance({ id: 'copy-1', name: 'main-restored', state: 'Provisioning' }), { status: 202 })
    }))
    const onStarted = vi.fn()
    const { router } = await mountDialog(null, onStarted)

    expect(field('name').value).toBe('main-restored')
    await type('port', '1777')
    await type('password', 'weak')
    button('Restore').click()
    await flushPromises()

    expect(body).toEqual({
      name: 'main-restored',
      description: null,
      port: 1777,
      release: null,
      build: null,
      serverDirectory: null,
      ssl: false,
      autoStart: true,
      includeLicense: true,
      superUser: { name: 'admin', password: 'weak' },
      source: { destinationId: source.destinationId, snapshotKey: source.snapshotKey },
    })
    await vi.waitFor(() => expect(router.currentRoute.value.name).toBe('instance'))
    expect(router.currentRoute.value.params.id).toBe('copy-1')
    expect(onStarted).toHaveBeenCalledOnce()
  })

  it('shows why the daemon refused the restore', async () => {
    server.use(http.post('/api/instances/restore', () => HttpResponse.json(
      { status: 422, detail: 'The point was made with p4d 2026.1/3089746: r25.1 cannot read it.' },
      { status: 422, headers: { 'Content-Type': 'application/problem+json' } },
    )))
    const onStarted = vi.fn()
    await mountDialog(null, onStarted)

    await type('password', 'weak')
    button('Restore').click()
    await flushPromises()

    expect(document.body.querySelector('[data-testid="restore-error"]')?.textContent).toContain('cannot read it')
    expect(onStarted).not.toHaveBeenCalled()
  })
})

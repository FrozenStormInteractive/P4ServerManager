// Types
import type { BackupRun } from '@/api/types'

// Utilities
import { flushPromises } from '@vue/test-utils'
import { http, HttpResponse } from 'msw'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h } from 'vue'
import { RouterView } from 'vue-router'
import { VApp } from 'vuetify/components'
import { useBackupsStore } from '@/stores/backups'
import { useInstancesStore } from '@/stores/instances'
import { createBackupDestination, createBackupRun, createInstance } from '@/testing/fixtures'
import { mountWithPlugins } from '@/testing/mount'
import { server } from '@/testing/server'

vi.mock('@/api/hub', () => ({
  connectInstancesHub: vi.fn(() => ({ stop: vi.fn(async () => {}) })),
}))

const instance = createInstance({ runtimeStatus: 'Running' })
const nas = createBackupDestination()
const cloud = createBackupDestination({ id: '3c2b1a00-0000-4000-8000-000000000002', name: 'cloud', location: 's3://p4/' })
const instanceUrl = `/api/instances/${instance.id}`

async function mountWith (runs: BackupRun[], selected: string[]) {
  server.use(
    http.get(`${instanceUrl}/health-checks`, () => HttpResponse.json([])),
    http.get(`${instanceUrl}/backups`, () => HttpResponse.json(runs)),
    http.get(`${instanceUrl}/backup-destinations`, () => HttpResponse.json({ destinationIds: selected })),
    http.get('/api/backup-destinations', () => HttpResponse.json([nas, cloud])),
  )
  const InApp = defineComponent(() => () => h(VApp, () => h(RouterView)))
  const { wrapper, router, pinia } = await mountWithPlugins(InApp, { attachTo: document.body })
  useInstancesStore(pinia).upsertInstance(instance)
  await router.push(`/instances/${instance.id}/backups`)
  await flushPromises()
  return { wrapper, router, pinia }
}

describe('InstanceBackupsPage', () => {
  afterEach(() => {
    document.body.innerHTML = ''
  })

  it('lists the backups with what they did at each destination', async () => {
    const { wrapper } = await mountWith(
      [
        createBackupRun(instance.id, {
          status: 'PartiallySucceeded',
          warnings: ['Depot cold keeps its archives in an object store: they are not in this backup.'],
          destinations: [
            { ...createBackupRun(instance.id).destinations[0]! },
            {
              destinationId: cloud.id,
              destinationName: 'cloud',
              status: 'Failed',
              uploadedObjects: 0,
              uploadedBytes: 0,
              snapshotKey: null,
              error: 'Access denied.',
              lastVerification: null,
            },
          ],
        }),
      ],
      [nas.id, cloud.id],
    )

    expect(wrapper.find('h1').text()).toBe('Backups')
    const text = wrapper.text()
    expect(text).toContain('Partly succeeded')
    expect(text).toContain('nas')
    expect(text).toContain('cloud')
    expect(text).toContain('4 (4.0 KB)')
    expect(wrapper.find('[data-testid="run-warnings"]').attributes('aria-label')).toBe('1 warning')
  })

  it('warns that an instance without destination is not backed up, and cannot start a backup', async () => {
    const { wrapper } = await mountWith([], [])

    expect(wrapper.find('[data-testid="backup-destinations"]').text())
      .toContain('No destination is chosen: the instance is not backed up.')
    const start = wrapper.findAll('button').find(button => button.text().includes('Back up now'))!
    expect(start.attributes('disabled')).toBeDefined()
  })

  it('begins a full backup from the menu', async () => {
    let body: unknown = null
    server.use(http.post(`${instanceUrl}/backups`, async ({ request }) => {
      body = await request.json()
      return HttpResponse.json(createBackupRun(instance.id, { status: 'Running', finishedAt: null }), { status: 202 })
    }))
    const { wrapper, pinia } = await mountWith([], [nas.id])

    await wrapper.findAll('button').find(button => button.text().includes('Back up now'))!.trigger('click')
    await flushPromises()
    const item = Array.from(document.body.querySelectorAll<HTMLElement>('.v-list-item'))
      .find(element => element.textContent?.includes('Full backup'))!
    item.click()
    await flushPromises()

    expect(body).toEqual({ kind: 'Full', destinationIds: null })
    expect(useBackupsStore(pinia).getRunningRun(instance.id)).not.toBeNull()
    expect(wrapper.text()).toContain('Stop the backup')
  })

  it('saves the destinations chosen for the instance', async () => {
    let body: unknown = null
    server.use(http.put(`${instanceUrl}/backup-destinations`, async ({ request }) => {
      body = await request.json()
      return HttpResponse.json({ destinationIds: [nas.id, cloud.id] })
    }))
    const { wrapper, pinia } = await mountWith([], [nas.id])

    const select = wrapper.find('[data-testid="backup-destinations"]').findComponent({ name: 'VSelect' })
    select.vm.$emit('update:modelValue', [nas.id, cloud.id])
    await flushPromises()
    const save = wrapper.find('[data-testid="backup-destinations"]').findAll('button').find(button => button.text() === 'Save')!
    await save.trigger('click')
    await flushPromises()

    expect(body).toEqual({ destinationIds: [nas.id, cloud.id] })
    expect(useBackupsStore(pinia).getInstanceDestinations(instance.id)).toEqual([nas.id, cloud.id])
  })
})

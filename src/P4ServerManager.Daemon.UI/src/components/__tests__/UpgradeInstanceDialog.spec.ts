// Types
import type { P4dVersion } from '@/api/types'

// Utilities
import { flushPromises } from '@vue/test-utils'
import { http, HttpResponse } from 'msw'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import UpgradeInstanceDialog from '@/components/instances/UpgradeInstanceDialog.vue'
import { useInstancesStore } from '@/stores/instances'
import { createInstance } from '@/testing/fixtures'
import { mountWithPlugins } from '@/testing/mount'
import { server } from '@/testing/server'
import { compareReleases } from '@/utils/release'

// Runs r25.1: r24.2 is older, and cannot be offered.
const instance = createInstance({ release: 'r25.1', version: '2025.1', build: '2850123' })

const versions: P4dVersion[] = [
  { release: 'r25.2', version: '2025.2', cachedBuilds: [] },
  { release: 'r25.1', version: '2025.1', cachedBuilds: [] },
  { release: 'r24.2', version: '2024.2', cachedBuilds: [] },
]

function upgradeButton () {
  const button = [...document.querySelectorAll('button')].find(b => b.textContent?.trim() === 'Upgrade')
  if (!button) {
    throw new Error('No upgrade button.')
  }
  return button
}

async function mountDialog () {
  const result = await mountWithPlugins(UpgradeInstanceDialog, {
    props: { modelValue: true, instance },
    attachTo: document.body,
  })
  useInstancesStore(result.pinia).upsertInstance(instance)
  await flushPromises()
  return result
}

describe('UpgradeInstanceDialog', () => {
  let bodies: unknown[]

  beforeEach(() => {
    bodies = []
    server.use(
      http.get('/api/p4d/versions', () => HttpResponse.json(versions)),
      http.post(`/api/instances/${instance.id}/upgrade`, async ({ request }) => {
        bodies.push(await request.json())
        return HttpResponse.json({ ...instance, runtimeStatus: 'Upgrading' }, { status: 202 })
      }),
    )
  })

  afterEach(() => {
    document.body.innerHTML = ''
  })

  it('upgrades to the newest release by default', async () => {
    const { wrapper, pinia } = await mountDialog()

    upgradeButton().click()
    await flushPromises()

    expect(bodies).toEqual([{ release: 'r25.2' }])
    expect(useInstancesStore(pinia).getInstance(instance.id)?.runtimeStatus).toBe('Upgrading')
    expect(wrapper.emitted('upgraded')).toHaveLength(1)
  })

  it('offers the release of the instance and the newer ones only', async () => {
    const { wrapper } = await mountDialog()

    const items = wrapper.findComponent({ name: 'VSelect' }).props('items') as { release: string }[]
    expect(items.map(item => item.release)).toEqual(['r25.2', 'r25.1'])
  })

  it('warns that a running server is stopped during the upgrade', async () => {
    await mountDialog()

    expect(document.body.textContent).toContain('its users are disconnected until it starts again')
  })

  it('shows why the daemon refused the upgrade', async () => {
    server.use(http.post(`/api/instances/${instance.id}/upgrade`, () =>
      HttpResponse.json({ detail: 'Another operation is in progress on this instance.' }, { status: 409 })))
    const { wrapper } = await mountDialog()

    upgradeButton().click()
    await flushPromises()

    expect(document.body.textContent)
      .toContain('The upgrade could not begin: Another operation is in progress on this instance.')
    expect(wrapper.emitted('upgraded')).toBeUndefined()
  })
})

describe('compareReleases', () => {
  it('orders the releases by year, then by number', () => {
    expect(['r25.1', 'r24.2', 'r25.2', 'r23.1'].toSorted(compareReleases)).toEqual(['r23.1', 'r24.2', 'r25.1', 'r25.2'])
  })
})

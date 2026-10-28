// Types
import type { BuildArchivePolicy, P4dVersion } from '@/api/types'

// Utilities
import { flushPromises } from '@vue/test-utils'
import { http, HttpResponse } from 'msw'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import BuildArchivePolicyCard from '@/components/builds/BuildArchivePolicyCard.vue'
import { useBuildsStore } from '@/stores/builds'
import { useNotificationsStore } from '@/stores/notifications'
import { createArchiveStatus } from '@/testing/fixtures'
import { mountWithPlugins } from '@/testing/mount'
import { server } from '@/testing/server'

const policy: BuildArchivePolicy = {
  autoDownload: true,
  checkIntervalHours: 6,
  scope: 'SelectedReleases',
  releases: ['r25.2'],
  keepLatestPerRelease: null,
}

const versions: P4dVersion[] = [
  { release: 'r26.1', version: '2026.1', cachedBuilds: [] },
  { release: 'r25.2', version: '2025.2', cachedBuilds: [] },
]

function findButton (text: string): HTMLButtonElement {
  const button = [...document.querySelectorAll('button')].find(b => b.textContent?.trim() === text)
  if (!button) {
    throw new Error(`No button ${text}.`)
  }
  return button
}

describe('BuildArchivePolicyCard', () => {
  let saved: unknown[]
  let checks: number

  beforeEach(() => {
    saved = []
    checks = 0
    server.use(
      http.get('/api/p4d/archive/policy', () => HttpResponse.json(policy)),
      http.get('/api/p4d/versions', () => HttpResponse.json(versions)),
      http.put('/api/p4d/archive/policy', async ({ request }) => {
        const body = await request.json()
        saved.push(body)
        return HttpResponse.json(body)
      }),
      http.get('/api/p4d/archive/status', () => HttpResponse.json(createArchiveStatus({
        lastCheckAt: '2026-10-27T06:00:00Z',
        nextCheckAt: '2026-10-27T12:00:00Z',
      }))),
      http.post('/api/p4d/archive/check', () => {
        checks++
        return new HttpResponse(null, { status: 202 })
      }),
    )
  })

  afterEach(() => {
    document.body.innerHTML = ''
  })

  it('saves the policy with the retention asked for', async () => {
    const { wrapper, pinia } = await mountWithPlugins(BuildArchivePolicyCard, { attachTo: document.body })
    await flushPromises()

    await wrapper.find('input[name="keepAll"]').setValue(false)
    await wrapper.find('input[name="keepLatestPerRelease"]').setValue('3')
    findButton('Save').click()
    await flushPromises()

    expect(saved).toEqual([{ ...policy, keepLatestPerRelease: 3 }])
    expect(useNotificationsStore(pinia).queue.map(n => n.text)).toEqual(['The archive policy is saved.'])
  })

  it('shows the field errors of the daemon', async () => {
    server.use(http.put('/api/p4d/archive/policy', () => HttpResponse.json(
      { detail: 'One or more validation errors occurred.', errors: { releases: ['Select at least one release.'] } },
      { status: 400 },
    )))
    const { wrapper } = await mountWithPlugins(BuildArchivePolicyCard, { attachTo: document.body })
    await flushPromises()

    findButton('Save').click()
    await flushPromises()

    expect(wrapper.text()).toContain('Select at least one release.')
  })

  it('asks the daemon to check Perforce now', async () => {
    const { pinia } = await mountWithPlugins(BuildArchivePolicyCard, { attachTo: document.body })
    useBuildsStore(pinia).status = createArchiveStatus()
    await flushPromises()

    findButton('Check now').click()
    await flushPromises()

    expect(checks).toBe(1)
  })

  it('keeps Check now disabled from the click until the check is done', async () => {
    const { pinia } = await mountWithPlugins(BuildArchivePolicyCard, { attachTo: document.body })
    const builds = useBuildsStore(pinia)
    builds.status = createArchiveStatus()
    await flushPromises()

    findButton('Check now').click()
    await flushPromises()
    expect(findButton('Check now').disabled).toBe(true)

    builds.status = createArchiveStatus({ running: true, checking: true })
    await flushPromises()
    expect(findButton('Check now').disabled).toBe(true)

    builds.status = createArchiveStatus()
    await flushPromises()
    expect(findButton('Check now').disabled).toBe(false)
  })

  it('shows the check under way, and how the last one went', async () => {
    const { wrapper, pinia } = await mountWithPlugins(BuildArchivePolicyCard, { attachTo: document.body })
    useBuildsStore(pinia).status = createArchiveStatus({
      running: true,
      checking: true,
      lastCheckError: 'r24.2: unreachable',
    })
    await flushPromises()

    expect(wrapper.find('[data-status]').text()).toBe('Checking Perforce for new builds…')
    expect(wrapper.find('[data-archive-progress]').exists()).toBe(true)
    expect(wrapper.text()).toContain('The last check failed: r24.2: unreachable')
  })

  it('leaves the fetches to the list of builds', async () => {
    const { wrapper, pinia } = await mountWithPlugins(BuildArchivePolicyCard, { attachTo: document.body })
    useBuildsStore(pinia).status = createArchiveStatus({
      running: true,
      fetches: [{ release: 'r25.2', state: 'Downloading', fileName: 'p4d', build: null, percent: 42 }],
    })
    await flushPromises()

    expect(wrapper.find('[data-status]').text()).toBe('Idle')
    expect(wrapper.find('[data-archive-progress]').exists()).toBe(false)
    expect(findButton('Check now').disabled).toBe(false)
  })
})

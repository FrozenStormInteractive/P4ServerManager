// Types
import type { ImportLegacyProfileRequest, LegacyProfile, P4dVersion } from '@/api/types'

// Utilities
import { flushPromises } from '@vue/test-utils'
import { http, HttpResponse } from 'msw'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import LegacyProfilesCard from '@/components/instances/LegacyProfilesCard.vue'
import { useInstancesStore } from '@/stores/instances'
import { createInstance } from '@/testing/fixtures'
import { mountWithPlugins } from '@/testing/mount'
import { server } from '@/testing/server'

const versions: P4dVersion[] = [
  { release: 'r26.1', version: '2026.1', cachedBuilds: [] },
  { release: 'r25.2', version: '2025.2', cachedBuilds: [] },
]

function createProfile (overrides: Partial<LegacyProfile> = {}): LegacyProfile {
  return {
    id: 'main.json',
    name: 'main',
    rootPath: '/volume1/Perforce/main/root',
    port: 1666,
    caseSensitive: false,
    autoStart: true,
    ignoredSettings: [],
    problems: [],
    instanceId: null,
    ...overrides,
  }
}

function button (text: string, scope: ParentNode = document) {
  const element = [...scope.querySelectorAll<HTMLButtonElement>('button')]
    .find(candidate => candidate.textContent?.trim() === text)
  if (!element) {
    throw new Error(`No button ${text}.`)
  }
  return element
}

function row (id: string) {
  return document.querySelector<HTMLElement>(`[data-profile="${CSS.escape(id)}"]`)!
}

describe('LegacyProfilesCard', () => {
  let profiles: LegacyProfile[]

  beforeEach(() => {
    profiles = []
    server.use(
      http.get('/api/legacy-profiles', () => HttpResponse.json(profiles)),
      http.get('/api/p4d/versions', () => HttpResponse.json(versions)),
    )
  })

  afterEach(() => {
    document.body.innerHTML = ''
  })

  it('shows nothing without profiles to import', async () => {
    profiles = [createProfile({ instanceId: 'imported-id' })]

    await mountWithPlugins(LegacyProfilesCard, { attachTo: document.body })
    await flushPromises()

    expect(document.body.textContent).not.toContain('Servers of the HelixCoreServer package')
  })

  it('lists the profiles with their problems and the settings they lose', async () => {
    profiles = [
      createProfile({ ignoredSettings: ['P4JOURNAL'] }),
      createProfile({ id: 'ssl.json', name: 'ssl', problems: ['SSL ports are not supported yet.'] }),
    ]

    await mountWithPlugins(LegacyProfilesCard, { attachTo: document.body })
    await vi.waitFor(() => expect(document.body.textContent).toContain('Servers of the HelixCoreServer package'))

    expect(row('main.json').textContent).toContain('/volume1/Perforce/main/root · 1666')
    expect(row('main.json').textContent).toContain('Not kept: P4JOURNAL')
    expect(button('Import', row('main.json')).disabled).toBe(false)
    expect(row('ssl.json').textContent).toContain('SSL ports are not supported yet.')
    expect(button('Import', row('ssl.json')).disabled).toBe(true)
  })

  it('dismisses a profile', async () => {
    profiles = [createProfile()]
    const dismissed: string[] = []
    server.use(http.delete('/api/legacy-profiles/:id', ({ params }) => {
      dismissed.push(String(params.id))
      return new HttpResponse(null, { status: 204 })
    }))

    await mountWithPlugins(LegacyProfilesCard, { attachTo: document.body })
    await vi.waitFor(() => expect(row('main.json')).not.toBeNull())
    button('Dismiss', row('main.json')).click()

    await vi.waitFor(() => expect(document.body.textContent).not.toContain('Servers of the HelixCoreServer package'))
    expect(dismissed).toEqual(['main.json'])
  })

  it('imports a profile with the settings it suggests, then opens the instance', async () => {
    profiles = [createProfile({ port: 1777, autoStart: false })]
    const requests: ImportLegacyProfileRequest[] = []
    server.use(http.post('/api/legacy-profiles/:id/import', async ({ request, params }) => {
      expect(params.id).toBe('main.json')
      const body = await request.json() as ImportLegacyProfileRequest
      requests.push(body)
      return HttpResponse.json(createInstance({ id: 'new-id', name: body.name, state: 'Provisioning' }), { status: 202 })
    }))

    const { router } = await mountWithPlugins(LegacyProfilesCard, { attachTo: document.body })
    await vi.waitFor(() => expect(row('main.json')).not.toBeNull())
    button('Import', row('main.json')).click()
    await vi.waitFor(() => expect(document.body.textContent).toContain('2026.1 (r26.1)'))

    document.querySelector('#import-instance-form')!.dispatchEvent(new Event('submit', { cancelable: true }))

    await vi.waitFor(() => expect(router.currentRoute.value.name).toBe('instance'))
    expect(router.currentRoute.value.params.id).toBe('new-id')
    expect(requests).toEqual([{
      name: 'main',
      description: null,
      release: 'r26.1',
      port: 1777,
      caseSensitive: false,
      autoStart: false,
    }])
    expect(useInstancesStore().getInstance('new-id')?.state).toBe('Provisioning')
    expect(document.body.textContent).not.toContain('Servers of the HelixCoreServer package')
  })

  it('shows the errors of the daemon by field', async () => {
    profiles = [createProfile()]
    server.use(http.post('/api/legacy-profiles/:id/import', () => HttpResponse.json(
      { status: 400, title: 'Invalid', errors: { port: ['Port 1666 is already in use on this machine.'] } },
      { status: 400, headers: { 'Content-Type': 'application/problem+json' } },
    )))

    await mountWithPlugins(LegacyProfilesCard, { attachTo: document.body })
    await vi.waitFor(() => expect(row('main.json')).not.toBeNull())
    button('Import', row('main.json')).click()
    await vi.waitFor(() => expect(document.body.textContent).toContain('2026.1 (r26.1)'))

    document.querySelector('#import-instance-form')!.dispatchEvent(new Event('submit', { cancelable: true }))

    await vi.waitFor(() => expect(document.body.textContent).toContain('Port 1666 is already in use on this machine.'))
  })
})

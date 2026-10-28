// Types
import type { BuildArchivePolicy, P4dVersion } from '@/api/types'

// Utilities
import { flushPromises } from '@vue/test-utils'
import { http, HttpResponse } from 'msw'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h } from 'vue'
import { VApp } from 'vuetify/components'
import SettingsPage from '@/pages/SettingsPage.vue'
import { createArchiveStatus, createBuild } from '@/testing/fixtures'
import { mountWithPlugins } from '@/testing/mount'
import { server } from '@/testing/server'

vi.mock('@/api/hub', () => ({
  connectBuildsHub: vi.fn(() => ({ stop: vi.fn(async () => {}) })),
}))

const policy: BuildArchivePolicy = {
  autoDownload: false,
  checkIntervalHours: 6,
  scope: 'SelectedReleases',
  releases: [],
  keepLatestPerRelease: null,
}

const versions: P4dVersion[] = [{ release: 'r25.2', version: '2025.2', cachedBuilds: [] }]

// The drawer of the page takes its place in the layout of the app.
const InApp = defineComponent(() => () => h(VApp, () => h(SettingsPage)))

describe('SettingsPage', () => {
  beforeEach(() => {
    server.use(
      http.get('/api/p4d/builds', () => HttpResponse.json([createBuild()])),
      http.get('/api/p4d/archive/status', () => HttpResponse.json(createArchiveStatus())),
      http.get('/api/p4d/archive/policy', () => HttpResponse.json(policy)),
      http.get('/api/p4d/versions', () => HttpResponse.json(versions)),
    )
  })

  afterEach(() => {
    document.body.innerHTML = ''
  })

  it('opens the p4d builds section by default', async () => {
    const { wrapper, router } = await mountWithPlugins(InApp, { attachTo: document.body })

    await router.push('/settings')
    await flushPromises()

    expect(router.currentRoute.value.name).toBe('settings-builds')
    expect(wrapper.find('h1').text()).toBe('p4d builds')
    expect(wrapper.text()).toContain('Archive policy')
    expect(wrapper.text()).toContain('2025.2/3086021')
  })

  it('lists the sections in a drawer of their own', async () => {
    const { wrapper, router } = await mountWithPlugins(InApp, { attachTo: document.body })

    await router.push('/settings')
    await flushPromises()

    const drawer = wrapper.findComponent({ name: 'VNavigationDrawer' })
    expect(drawer.find('[aria-label="settings"]').text()).toContain('p4d builds')
    expect(drawer.find('a[href="/settings/builds"]').exists()).toBe(true)
  })
})

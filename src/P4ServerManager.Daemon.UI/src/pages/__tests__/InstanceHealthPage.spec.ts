// Types
import type { HealthCheckRun } from '@/api/types'

// Utilities
import { flushPromises } from '@vue/test-utils'
import { http, HttpResponse } from 'msw'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h } from 'vue'
import { RouterView } from 'vue-router'
import { VApp } from 'vuetify/components'
import { useHealthChecksStore } from '@/stores/healthChecks'
import { useInstancesStore } from '@/stores/instances'
import { useNotificationsStore } from '@/stores/notifications'
import { createHealthCheckRun, createInstance } from '@/testing/fixtures'
import { mountWithPlugins } from '@/testing/mount'
import { server } from '@/testing/server'

vi.mock('@/api/hub', () => ({
  connectInstancesHub: vi.fn(() => ({ stop: vi.fn(async () => {}) })),
}))

const instance = createInstance()
const checksUrl = `/api/instances/${instance.id}/health-checks`

const problem = { check: 'CrossCheck', table: 'db.change/db.desc', message: 'db.change/db.desc inconsistencies found.' } as const

// The page and its drawer take their place in the layout of the app.
async function mountWith (runs: HealthCheckRun[], overrides = {}) {
  server.use(http.get(checksUrl, () => HttpResponse.json(runs)))
  const InApp = defineComponent(() => () => h(VApp, () => h(RouterView)))
  const { wrapper, router, pinia } = await mountWithPlugins(InApp, { attachTo: document.body })
  useInstancesStore(pinia).upsertInstance({ ...instance, ...overrides })
  await router.push(`/instances/${instance.id}/health`)
  await flushPromises()
  return { wrapper, router, pinia }
}

function findButton (text: string): HTMLButtonElement {
  const buttons = document.body.querySelectorAll<HTMLButtonElement>('.v-dialog button')
  return Array.from(buttons).find(button => button.textContent?.trim() === text)!
}

describe('InstanceHealthPage', () => {
  afterEach(() => {
    document.body.innerHTML = ''
  })

  it('tells that the instance was not checked yet', async () => {
    const { wrapper } = await mountWith([])

    expect(wrapper.find('h1').text()).toBe('Health')
    expect(wrapper.find('[data-testid="health-state"]').text()).toContain('Not checked yet')
    expect(wrapper.text()).toContain('No reports yet.')
  })

  it('tells that the instance is healthy from its last complete check', async () => {
    const { wrapper } = await mountWith([createHealthCheckRun(instance.id)])

    const state = wrapper.find('[data-testid="health-state"]').text()
    expect(state).toContain('Healthy')
    expect(state).toContain('no problems are known')
  })

  it('tells the problems of the last complete check, with its fix journal', async () => {
    const { wrapper } = await mountWith([
      createHealthCheckRun(instance.id, { status: 'ProblemsFound', findings: [problem], hasFixJournal: true }),
    ])

    const state = wrapper.find('[data-testid="health-state"]')
    expect(state.text()).toContain('Problems found')
    expect(state.text()).toContain('1 problem is known.')
    expect(state.text()).toContain('db.change/db.desc inconsistencies found.')
    expect(state.find('a[href$="/fix-journal"]').exists()).toBe(true)
    expect(wrapper.find('[data-health="problems"]').exists()).toBe(true)
  })

  it('does not take a check cut short for the health of the instance', async () => {
    const { wrapper } = await mountWith([
      createHealthCheckRun(instance.id, { id: 'canceled', status: 'Canceled', startedAt: '2026-11-02T00:00:00Z' }),
      createHealthCheckRun(instance.id, { id: 'healthy', startedAt: '2026-11-01T00:00:00Z' }),
    ])

    const state = wrapper.find('[data-testid="health-state"]').text()
    expect(state).toContain('Healthy')
    expect(state).toContain('A later check did not run to its end')
  })

  it('keeps the problems of a check that no later run has checked again', async () => {
    const { wrapper } = await mountWith([
      createHealthCheckRun(instance.id, { id: 'validate', checks: ['Validate'], startedAt: '2026-11-02T00:00:00Z' }),
      createHealthCheckRun(instance.id, {
        id: 'both',
        status: 'ProblemsFound',
        findings: [problem],
        hasFixJournal: true,
        startedAt: '2026-11-01T00:00:00Z',
      }),
    ])

    const state = wrapper.find('[data-testid="health-state"]')
    expect(state.text()).toContain('Problems found')
    expect(state.text()).toContain('db.change/db.desc inconsistencies found.')
    expect(state.find('a[href$="/fix-journal"]').exists()).toBe(true)
  })

  it('clears the problems of a check once a later run finds nothing', async () => {
    const { wrapper } = await mountWith([
      createHealthCheckRun(instance.id, { id: 'cross', checks: ['CrossCheck'], startedAt: '2026-11-02T00:00:00Z' }),
      createHealthCheckRun(instance.id, {
        id: 'both',
        status: 'ProblemsFound',
        findings: [problem],
        startedAt: '2026-11-01T00:00:00Z',
      }),
    ])

    const state = wrapper.find('[data-testid="health-state"]').text()
    expect(state).toContain('Healthy')
    expect(state).not.toContain('inconsistencies found')
  })

  it('tells which check never ran', async () => {
    const { wrapper } = await mountWith([createHealthCheckRun(instance.id, { checks: ['Validate'] })])

    expect(wrapper.find('[data-testid="health-state"]').text()).toContain('Never checked: Consistency between tables.')
  })

  it('shows the progress and the problems of the check in progress, then its result', async () => {
    const running = createHealthCheckRun(instance.id, {
      status: 'Running',
      finishedAt: null,
      progress: { check: 'Validate', item: 'db.rev', itemsChecked: 12, percent: 34 },
      findings: [{ check: 'Validate', table: 'db.rev', message: '**** Checksum mismatch ****' }],
    })
    const { wrapper, pinia } = await mountWith([running])

    const state = wrapper.find('[data-testid="health-state"]')
    expect(state.text()).toContain('Check in progress')
    expect(state.text()).toContain('34%')
    expect(wrapper.find('[data-testid="health-item"]').text()).toContain('db.rev')
    expect(state.text()).toContain('Problems found so far')
    expect(state.text()).toContain('**** Checksum mismatch ****')
    expect(wrapper.find('[data-health="running"]').exists()).toBe(true)

    // It is not a report yet.
    expect(wrapper.text()).toContain('No reports yet.')
    expect(wrapper.findAll('button').map(button => button.text())).toContain('Cancel the check')

    useInstancesStore(pinia).setHealthCheck({
      ...running,
      status: 'ProblemsFound',
      finishedAt: '2026-11-01T01:00:00Z',
      progress: null,
    })
    await flushPromises()

    expect(wrapper.find('[data-testid="health-state"]').text()).toContain('Problems found')
    expect(wrapper.text()).not.toContain('No reports yet.')
    expect(useNotificationsStore(pinia).queue.at(-1)?.text).toBe('The check of main found 1 problem.')
  })

  it('cancels the check in progress', async () => {
    const running = createHealthCheckRun(instance.id, { status: 'Running', finishedAt: null })
    const cancel = vi.fn()
    server.use(http.post(`${checksUrl}/${running.id}/cancel`, () => {
      cancel()
      return new HttpResponse(null, { status: 202 })
    }))
    const { wrapper } = await mountWith([running])

    await wrapper.findAll('button').find(button => button.text() === 'Cancel the check')!.trigger('click')
    await flushPromises()

    expect(cancel).toHaveBeenCalledOnce()
  })

  it('runs the checks chosen in the dialog', async () => {
    let body: unknown = null
    server.use(http.post(checksUrl, async ({ request }) => {
      body = await request.json()
      return HttpResponse.json(createHealthCheckRun(instance.id, { status: 'Running', finishedAt: null }), { status: 202 })
    }))
    const { wrapper, pinia } = await mountWith([])

    await wrapper.findAll('button').find(button => button.text() === 'Run a check')!.trigger('click')
    await flushPromises()
    const dialog = document.body.querySelector('.v-dialog')!
    expect(dialog.textContent).toContain('disconnects its users')

    dialog.querySelector<HTMLInputElement>('input[name="CrossCheck"]')!.click()
    await flushPromises()
    findButton('Run').click()
    await flushPromises()

    expect(body).toEqual({ checks: ['Validate'] })
    expect(useHealthChecksStore(pinia).getRunningRun(instance.id)).not.toBeNull()
  })

  it('does not offer a check on an instance that is not ready', async () => {
    const { wrapper } = await mountWith([], { state: 'Failed' })

    const button = wrapper.findAll('button').find(b => b.text() === 'Run a check')!
    expect(button.attributes('disabled')).toBeDefined()
  })

  it('keeps the state of the instance once its report is deleted', async () => {
    const run = createHealthCheckRun(instance.id)
    const { wrapper } = await mountWith([run])
    server.use(
      http.delete(`${checksUrl}/${run.id}`, () => new HttpResponse(null, { status: 204 })),
      http.get(checksUrl, () => HttpResponse.json([{ ...run, reportHidden: true }])),
    )

    await wrapper.find('button[aria-label="Delete the report"]').trigger('click')
    await flushPromises()
    findButton('Delete').click()
    await flushPromises()

    expect(wrapper.text()).toContain('No reports yet.')
    const state = wrapper.find('[data-testid="health-state"]').text()
    expect(state).toContain('Healthy')
    expect(state).not.toContain('A later check did not run to its end')
  })

  it('removes a report the daemon deletes at once', async () => {
    const canceled = createHealthCheckRun(instance.id, { id: 'canceled', status: 'Canceled', startedAt: '2026-11-02T00:00:00Z' })
    const healthy = createHealthCheckRun(instance.id, { id: 'healthy', startedAt: '2026-11-01T00:00:00Z' })
    const { wrapper } = await mountWith([canceled, healthy])
    server.use(
      http.delete(`${checksUrl}/${canceled.id}`, () => new HttpResponse(null, { status: 204 })),
      http.get(checksUrl, () => HttpResponse.json([healthy])),
    )

    await wrapper.find('button[aria-label="Delete the report"]').trigger('click')
    await flushPromises()
    findButton('Delete').click()
    await flushPromises()

    expect(wrapper.findAll('button[aria-label="Delete the report"]')).toHaveLength(1)
  })

  it('opens a report', async () => {
    const { wrapper } = await mountWith([
      createHealthCheckRun(instance.id, { status: 'Failed', error: 'Root directory invalid.' }),
    ])

    await wrapper.find('button[aria-label="View the report"]').trigger('click')
    await flushPromises()

    const dialog = document.body.querySelector('.v-dialog')!
    expect(dialog.textContent).toContain('Root directory invalid.')
    expect(dialog.textContent).toContain('2026.1/3089746')
  })

  it('lists the sections of the instance in a drawer', async () => {
    const { wrapper } = await mountWith([])

    const drawer = wrapper.findComponent({ name: 'VNavigationDrawer' })
    expect(drawer.find(`a[href="/instances/${instance.id}"]`).text()).toBe('Overview')
    expect(drawer.find(`a[href="/instances/${instance.id}/health"]`).text()).toBe('Health')
  })
})

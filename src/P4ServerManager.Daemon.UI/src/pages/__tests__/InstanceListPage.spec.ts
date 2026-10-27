// Types
import type { InstanceActionResult } from '@/api/types'

// Utilities
import { flushPromises } from '@vue/test-utils'
import { http, HttpResponse } from 'msw'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import InstanceListPage from '@/pages/InstanceListPage.vue'
import { useInstancesStore } from '@/stores/instances'
import { createInstance } from '@/testing/fixtures'
import { mountWithPlugins } from '@/testing/mount'
import { server } from '@/testing/server'

const alpha = createInstance({ id: 'a', name: 'alpha', runtimeStatus: 'Running' })
const beta = createInstance({ id: 'b', name: 'beta', runtimeStatus: 'Stopped' })
const gamma = createInstance({ id: 'c', name: 'gamma', state: 'Failed', runtimeStatus: 'Stopped' })

async function mountPage () {
  const { wrapper, pinia } = await mountWithPlugins(InstanceListPage, { attachTo: document.body })
  const store = useInstancesStore(pinia)
  for (const instance of [alpha, beta, gamma]) {
    store.upsertInstance(instance)
  }
  await flushPromises()
  return { wrapper, store }
}

// The last match: the buttons of a dialog come after the page.
function findButton (text: string): HTMLButtonElement {
  const button = [...document.querySelectorAll('button')].findLast(b => b.textContent?.trim() === text)
  if (!button) {
    throw new Error(`No button ${text}.`)
  }
  return button
}

describe('InstanceListPage', () => {
  beforeEach(() => {
    server.use(http.get('/api/legacy-profiles', () => HttpResponse.json([])))
  })

  afterEach(() => {
    document.body.innerHTML = ''
  })

  it('stops the selected servers that run, and only them', async () => {
    let body: unknown
    server.use(http.post('/api/instances/stop', async ({ request }) => {
      body = await request.json()
      return HttpResponse.json<InstanceActionResult[]>([
        { instance: { ...alpha, runtimeStatus: 'Stopped' }, outcome: 'Succeeded', error: null },
      ])
    }))
    const { wrapper, store } = await mountPage()

    // The first checkbox selects every row.
    await wrapper.find('input[type="checkbox"]').setValue(true)
    expect(wrapper.text()).toContain('3 selected')
    findButton('Stop').click()
    await flushPromises()

    expect(document.body.textContent).toContain('Stop alpha?')
    expect(body).toBeUndefined()

    findButton('Stop').click()
    await flushPromises()

    expect(body).toEqual({ instanceIds: ['a'] })
    expect(store.getInstance('a')?.runtimeStatus).toBe('Stopped')
  })

  it('starts the selected servers without a confirmation', async () => {
    let body: unknown
    server.use(http.post('/api/instances/start', async ({ request }) => {
      body = await request.json()
      return HttpResponse.json<InstanceActionResult[]>([{ instance: beta, outcome: 'Succeeded', error: null }])
    }))
    const { wrapper } = await mountPage()

    await wrapper.find('input[type="checkbox"]').setValue(true)
    findButton('Start').click()
    await flushPromises()

    expect(body).toEqual({ instanceIds: ['b'] })
  })

  it('restarts only the selected servers that run', async () => {
    let body: unknown
    server.use(http.post('/api/instances/restart', async ({ request }) => {
      body = await request.json()
      return HttpResponse.json<InstanceActionResult[]>([{ instance: alpha, outcome: 'Succeeded', error: null }])
    }))
    const delta = createInstance({ id: 'd', name: 'delta', runtimeStatus: 'Running' })
    const { wrapper, store } = await mountPage()
    store.upsertInstance(delta)
    await flushPromises()

    await wrapper.find('input[type="checkbox"]').setValue(true)
    findButton('Restart').click()
    await flushPromises()

    const dialog = document.querySelector('.v-dialog')!
    expect(dialog.textContent).toContain('Restart 2 servers?')
    expect([...dialog.querySelectorAll('li')].map(li => li.textContent)).toEqual(['alpha', 'delta'])

    findButton('Restart').click()
    await flushPromises()

    expect(body).toEqual({ instanceIds: ['a', 'd'] })
  })

  it('does not offer a restart when no selected server runs', async () => {
    const { wrapper } = await mountPage()

    // beta is stopped.
    await wrapper.findAll('input[type="checkbox"]')[2]!.trigger('click')

    expect(wrapper.text()).toContain('1 selected')
    expect(findButton('Restart').disabled).toBe(true)
    expect(findButton('Start').disabled).toBe(false)
  })

  it('does not offer a start when no selected server is stopped', async () => {
    const { wrapper } = await mountPage()

    // The row checkboxes toggle on a click, not on a change.
    await wrapper.findAll('input[type="checkbox"]')[1]!.trigger('click')

    expect(wrapper.text()).toContain('1 selected')
    expect(findButton('Start').disabled).toBe(true)
    expect(findButton('Stop').disabled).toBe(false)
  })

  it('starts all the servers and lists the ones that failed', async () => {
    let body: unknown
    server.use(http.post('/api/instances/start', async ({ request }) => {
      body = await request.json()
      return HttpResponse.json<InstanceActionResult[]>([
        { instance: alpha, outcome: 'Skipped', error: null },
        { instance: beta, outcome: 'Failed', error: 'p4d did not start.' },
      ])
    }))
    const { wrapper } = await mountPage()

    findButton('All servers').click()
    await flushPromises()
    const startAll = [...document.querySelectorAll('.v-list-item')].find(i => i.textContent?.includes('Start all'))
    ;(startAll as HTMLElement).click()
    await flushPromises()

    expect(body).toEqual({ all: true })
    expect(wrapper.text()).toContain('Some servers did not start')
    expect(wrapper.text()).toContain('beta: p4d did not start.')
  })

  it('asks before it stops all the servers', async () => {
    let requests = 0
    server.use(http.post('/api/instances/stop', () => {
      requests++
      return HttpResponse.json<InstanceActionResult[]>([])
    }))
    await mountPage()

    findButton('All servers').click()
    await flushPromises()
    const stopAll = [...document.querySelectorAll('.v-list-item')].find(i => i.textContent?.includes('Stop all'))
    ;(stopAll as HTMLElement).click()
    await flushPromises()

    expect(document.body.textContent).toContain('Stop all the servers?')
    expect(requests).toBe(0)

    findButton('Stop').click()
    await flushPromises()

    expect(requests).toBe(1)
  })
})

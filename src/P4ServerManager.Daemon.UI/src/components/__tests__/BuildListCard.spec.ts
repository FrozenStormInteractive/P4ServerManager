// Utilities
import { flushPromises } from '@vue/test-utils'
import { http, HttpResponse } from 'msw'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import BuildListCard from '@/components/builds/BuildListCard.vue'
import { useBuildsStore } from '@/stores/builds'
import { useInstancesStore } from '@/stores/instances'
import { createArchiveStatus, createBuild, createInstance } from '@/testing/fixtures'
import { mountWithPlugins } from '@/testing/mount'
import { server } from '@/testing/server'

const used = createBuild({ id: 'used', build: '3086021' })
const archived = createBuild({ id: 'archived', build: '3000000', extracted: false, source: 'Imported' })

async function mountCard () {
  const result = await mountWithPlugins(BuildListCard, { attachTo: document.body })
  const builds = useBuildsStore(result.pinia)
  builds.upsertBuild(used)
  builds.upsertBuild(archived)
  builds.loaded = true
  builds.status = createArchiveStatus()
  useInstancesStore(result.pinia).upsertInstance(createInstance({ version: '2025.2', build: '3086021' }))
  await flushPromises()
  return result
}

function rows () {
  return [...document.querySelectorAll('tbody tr')]
}

describe('BuildListCard', () => {
  beforeEach(() => {
    server.use(http.get('/api/p4d/versions', () => HttpResponse.json([])))
  })

  afterEach(() => {
    document.body.innerHTML = ''
  })

  it('lists the builds newest first, with the servers that use them', async () => {
    await mountCard()

    expect(rows().map(row => row.textContent)).toEqual([
      expect.stringContaining('2025.2/3086021'),
      expect.stringContaining('2025.2/3000000'),
    ])
    expect(rows()[0]!.textContent).toContain('main')
    expect(rows()[0]!.textContent).toContain('12.4 MB')
    expect(rows()[1]!.textContent).toContain('Unused')
    expect(rows()[1]!.textContent).toContain('Imported')
  })

  it('lists the fetches from Perforce, the one under way first', async () => {
    const { pinia } = await mountCard()
    useBuildsStore(pinia).status = createArchiveStatus({
      running: true,
      fetches: [
        { release: 'r26.1', state: 'Compressing', fileName: null, build: '2026.1/3089746', percent: 67 },
        { release: 'r25.2', state: 'Queued', fileName: null, build: null, percent: null },
      ],
    })
    await flushPromises()

    const fetches = [...document.querySelectorAll('[data-fetch]')].map(item => item.textContent)
    expect(fetches).toEqual([
      expect.stringMatching(/r26\.1.*2026\.1\/3089746.*Compressing into the archive.*67%/),
      expect.stringMatching(/r25\.2.*Waiting for its turn/),
    ])
  })

  it('shows no queue when nothing is fetched', async () => {
    await mountCard()

    expect(document.querySelector('[data-fetches]')).toBeNull()
  })

  it('cannot delete a build that a server uses', async () => {
    await mountCard()

    const deleteButtons = rows().map(row => row.querySelector<HTMLButtonElement>('button[aria-label="Delete"]')!)
    expect(deleteButtons[0]!.disabled).toBe(true)
    expect(deleteButtons[1]!.disabled).toBe(false)
  })

  it('tells why a build in use cannot be deleted', async () => {
    const { wrapper } = await mountCard()

    const tooltips = wrapper.findAllComponents({ name: 'VTooltip' })
    expect(tooltips.map(tooltip => tooltip.props('disabled'))).toEqual([false, true])
    expect(tooltips[0]!.props('text')).toBe('This build cannot be deleted while a server uses it.')
  })

  it('deletes an unused build once confirmed', async () => {
    let deleted = false
    server.use(http.delete('/api/p4d/builds/archived', () => {
      deleted = true
      return new HttpResponse(null, { status: 204 })
    }))
    const { pinia } = await mountCard()

    rows()[1]!.querySelector<HTMLButtonElement>('button[aria-label="Delete"]')!.click()
    await flushPromises()
    const confirm = [...document.querySelectorAll('button')].findLast(b => b.textContent?.trim() === 'Delete')!
    confirm.click()
    await flushPromises()

    expect(deleted).toBe(true)
    expect(useBuildsStore(pinia).sortedBuilds.map(b => b.id)).toEqual(['used'])
  })

  it('protects a build', async () => {
    let body: unknown
    server.use(http.patch('/api/p4d/builds/archived', async ({ request }) => {
      body = await request.json()
      return HttpResponse.json({ ...archived, protected: true })
    }))
    const { pinia } = await mountCard()

    rows()[1]!.querySelector<HTMLInputElement>('input[type="checkbox"]')!.click()
    await flushPromises()

    expect(body).toEqual({ protected: true })
    expect(useBuildsStore(pinia).builds.archived!.protected).toBe(true)
  })
})

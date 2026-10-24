// Types
import type { CreateInstanceRequest, P4dVersion } from '@/api/types'

// Utilities
import { flushPromises } from '@vue/test-utils'
import { http, HttpResponse } from 'msw'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import CreateInstanceDialog from '@/components/instances/CreateInstanceDialog.vue'
import { createInstance } from '@/testing/fixtures'
import { mountWithPlugins } from '@/testing/mount'
import { server } from '@/testing/server'

const versions: P4dVersion[] = [
  { release: 'r26.1', version: '2026.1', cachedBuilds: [] },
  { release: 'r25.2', version: '2025.2', cachedBuilds: [{ build: '3086021', downloadedAt: '2026-10-21T23:06:43Z' }] },
]

function input (name: string) {
  const element = document.querySelector<HTMLInputElement | HTMLTextAreaElement>(`[name="${CSS.escape(name)}"]`)
  if (!element) {
    throw new Error(`No field named ${name}.`)
  }
  return element
}

async function type (name: string, value: string) {
  const element = input(name)
  element.value = value
  element.dispatchEvent(new Event('input'))
  await flushPromises()
}

async function submit () {
  document.querySelector('#create-instance-form')!.dispatchEvent(new Event('submit', { cancelable: true }))
  await flushPromises()
}

async function mountDialog () {
  const result = await mountWithPlugins(CreateInstanceDialog, {
    props: { modelValue: true },
    attachTo: document.body,
  })
  await vi.waitFor(() => expect(document.body.textContent).toContain('2026.1 (r26.1)'))
  return result
}

describe('CreateInstanceDialog', () => {
  let requests: CreateInstanceRequest[]

  beforeEach(() => {
    requests = []
    server.use(
      http.get('/api/p4d/versions', () => HttpResponse.json(versions)),
      http.post('/api/instances', async ({ request }) => {
        const body = await request.json() as CreateInstanceRequest
        requests.push(body)
        return HttpResponse.json(createInstance({ id: 'new-id', name: body.name, state: 'Provisioning' }), { status: 202 })
      }),
    )
  })

  afterEach(() => {
    document.body.innerHTML = ''
  })

  it('selects the newest p4d version by default', async () => {
    await mountDialog()

    expect(input('release').value).toBe('r26.1')
  })

  it('does not send an invalid form', async () => {
    await mountDialog()
    await type('name', '1-bad-name')
    await type('password', 'short')

    await submit()

    await vi.waitFor(() => {
      expect(document.body.textContent).toContain('Start with a letter')
      expect(document.body.textContent).toContain('Use at least 8 characters.')
    })
    expect(requests).toEqual([])
  })

  it('requires the super user', async () => {
    await mountDialog()
    await type('name', 'main')
    await type('superUserName', '')

    await submit()

    await vi.waitFor(() => {
      expect(document.body.textContent).toContain('Enter a user name.')
      expect(document.body.textContent).toContain('Use at least 8 characters.')
    })
    expect(requests).toEqual([])
  })

  it('sends the settings, then opens the new instance', async () => {
    const { wrapper, router } = await mountDialog()
    await type('name', 'main')
    await type('description', '  Main depot  ')
    await type('port', '1777')
    await type('password', 'Sup3r-Secret!')
    await type('confirmPassword', 'Sup3r-Secret!')

    await submit()

    await vi.waitFor(() => expect(router.currentRoute.value.name).toBe('instance'))
    expect(router.currentRoute.value.params.id).toBe('new-id')
    expect(requests).toEqual([{
      name: 'main',
      description: 'Main depot',
      release: 'r26.1',
      port: 1777,
      caseSensitive: true,
      unicode: false,
      autoStart: true,
      rootPath: null,
      superUser: { name: 'admin', password: 'Sup3r-Secret!' },
    }])
    expect(wrapper.emitted('update:modelValue')).toEqual([[false]])
  })

  it('folds the advanced settings and sends the auto-start turned off there', async () => {
    await mountDialog()
    expect(document.querySelector('[name="autoStart"]')).toBeNull()
    await type('name', 'main')
    await type('password', 'Sup3r-Secret!')
    await type('confirmPassword', 'Sup3r-Secret!')

    document.querySelector<HTMLButtonElement>('button[name="advanced"]')!.click()
    await vi.waitFor(() => expect(document.querySelector('[name="autoStart"]')).not.toBeNull())
    const autoStart = input('autoStart') as HTMLInputElement
    expect(autoStart.checked).toBe(true)
    autoStart.click()
    await flushPromises()
    await submit()

    await vi.waitFor(() => expect(requests).toHaveLength(1))
    expect(requests[0]!.autoStart).toBe(false)
  })

  it('shows the errors returned by the daemon next to their field', async () => {
    server.use(http.post('/api/instances', () => HttpResponse.json(
      { title: 'One or more validation errors occurred.', status: 400, errors: { port: ['Port 1777 is already in use on this machine.'] } },
      { status: 400, headers: { 'Content-Type': 'application/problem+json' } },
    )))
    const { router } = await mountDialog()
    await type('name', 'main')
    await type('port', '1777')
    await type('password', 'Sup3r-Secret!')
    await type('confirmPassword', 'Sup3r-Secret!')

    await submit()

    await vi.waitFor(() => expect(document.body.textContent).toContain('Port 1777 is already in use on this machine.'))
    expect(router.currentRoute.value.name).toBe('dashboard')

    await type('port', '1778')
    expect(document.body.textContent).not.toContain('Port 1777 is already in use on this machine.')
  })
})

// Utilities
import { flushPromises } from '@vue/test-utils'
import { http, HttpResponse } from 'msw'
import { afterEach, describe, expect, it } from 'vitest'
import { defineComponent, h } from 'vue'
import { VApp } from 'vuetify/components'
import BackupDestinationDialog from '@/components/backups/BackupDestinationDialog.vue'
import { createBackupDestination } from '@/testing/fixtures'
import { mountWithPlugins } from '@/testing/mount'
import { server } from '@/testing/server'

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

async function mountDialog () {
  const InApp = defineComponent(() => () => h(VApp, () => h(BackupDestinationDialog, { modelValue: true })))
  const result = await mountWithPlugins(InApp, { attachTo: document.body })
  await flushPromises()
  return result
}

describe('BackupDestinationDialog', () => {
  afterEach(() => {
    document.body.innerHTML = ''
  })

  it('adds a local destination with its passphrase', async () => {
    let body: unknown = null
    server.use(
      http.post('/api/backup-destinations', async ({ request }) => {
        body = await request.json()
        return HttpResponse.json(createBackupDestination(), { status: 201 })
      }),
      http.get('/api/backup-destinations', () => HttpResponse.json([createBackupDestination()])),
    )
    await mountDialog()

    await type('name', 'nas')
    await type('local.path', '/volume1/backups')
    await type('passphrase', 'correct horse battery staple')
    await type('passphraseConfirm', 'correct horse battery staple')
    button('Create').click()
    await flushPromises()

    expect(body).toEqual({
      name: 'nas',
      kind: 'Local',
      local: { path: '/volume1/backups' },
      credentials: null,
      passphrase: 'correct horse battery staple',
    })
  })

  it('refuses passphrases that do not match, without asking the daemon', async () => {
    let posted = false
    server.use(http.post('/api/backup-destinations', () => {
      posted = true
      return HttpResponse.json(createBackupDestination(), { status: 201 })
    }))
    await mountDialog()

    await type('name', 'nas')
    await type('local.path', '/volume1/backups')
    await type('passphrase', 'correct horse battery staple')
    await type('passphraseConfirm', 'another passphrase here')
    button('Create').click()
    await flushPromises()

    expect(posted).toBe(false)
    expect(document.body.textContent).toContain('The passphrases do not match.')
  })

  it('shows why the daemon refused the destination', async () => {
    server.use(http.post('/api/backup-destinations', () => HttpResponse.json(
      { status: 422, detail: '/volume1/backups already holds a backup repository, and the passphrase does not open it.' },
      { status: 422, headers: { 'Content-Type': 'application/problem+json' } },
    )))
    await mountDialog()

    await type('name', 'nas')
    await type('local.path', '/volume1/backups')
    await type('passphrase', 'correct horse battery staple')
    await type('passphraseConfirm', 'correct horse battery staple')
    button('Create').click()
    await flushPromises()

    expect(document.body.querySelector('[data-testid="destination-error"]')?.textContent)
      .toContain('the passphrase does not open it')
  })
})

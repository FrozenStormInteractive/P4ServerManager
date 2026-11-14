// Types
import type { DirectoryEntry, DirectoryListing } from '@/api/types'

// Utilities
import { flushPromises } from '@vue/test-utils'
import { http, HttpResponse } from 'msw'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h, ref } from 'vue'
import ServerDirectoryDialog from '@/components/filesystem/ServerDirectoryDialog.vue'
import ServerDirectoryField from '@/components/filesystem/ServerDirectoryField.vue'
import { mountWithPlugins } from '@/testing/mount'
import { server } from '@/testing/server'

// A small Linux file system: the folders and their subfolders.
const tree: Record<string, string[]> = {
  '/': ['/srv', '/volume1'],
  '/srv': [],
  '/volume1': ['/volume1/backups', '/volume1/perforce'],
  '/volume1/backups': [],
  '/volume1/perforce': [],
}

// The folders the daemon may not write to.
const readOnly = new Set(['/', '/srv'])

function entry (path: string): DirectoryEntry {
  return {
    name: path === '/' ? '/' : path.slice(path.lastIndexOf('/') + 1),
    path,
    writable: !readOnly.has(path),
  }
}

function listingOf (path: string): DirectoryListing {
  const ancestors = ['/']
  for (const segment of path.split('/').filter(Boolean)) {
    ancestors.push(`${ancestors.length === 1 ? '' : ancestors.at(-1)}/${segment}`)
  }
  return {
    path,
    parent: ancestors.length > 1 ? ancestors.at(-2)! : null,
    writable: !readOnly.has(path),
    ancestors: ancestors.map(a => entry(a)),
    directories: tree[path]!.map(d => entry(d)),
    roots: [entry('/')],
  }
}

function dialogText () {
  return document.querySelector('.server-directory-dialog')?.textContent ?? ''
}

function selectedPath () {
  return document.querySelector('.selected-path')?.textContent?.trim()
}

function button (name: string) {
  const element = document.querySelector<HTMLButtonElement>(`.server-directory-dialog button[name="${CSS.escape(name)}"]`)
  if (!element) {
    throw new Error(`No button named ${name}.`)
  }
  return element
}

function folderItem (name: string) {
  const item = [...document.querySelectorAll<HTMLElement>('.server-directory-dialog .v-list-item')]
    .find(element => element.querySelector('.v-list-item-title')?.textContent?.trim() === name)
  if (!item) {
    throw new Error(`No folder named ${name}.`)
  }
  return item
}

async function addFolder (name: string) {
  button('newFolder').click()
  await vi.waitFor(() => expect(document.querySelector('input[name="newFolderName"]')).not.toBeNull())
  const input = document.querySelector<HTMLInputElement>('input[name="newFolderName"]')!
  input.value = name
  input.dispatchEvent(new Event('input'))
  await flushPromises()
  document.querySelector('#new-folder-form')!.dispatchEvent(new Event('submit', { cancelable: true }))
  await flushPromises()
}

describe('ServerDirectoryDialog', () => {
  let listed: (string | null)[]
  let writes: string[]

  beforeEach(() => {
    listed = []
    writes = []
    server.use(
      http.get('/api/filesystem/directories', ({ request }) => {
        const path = new URL(request.url).searchParams.get('path')
        listed.push(path)
        const folder = path ?? '/'
        if (!tree[folder]) {
          return HttpResponse.json({ status: 404, detail: `The folder ${folder} does not exist.` }, { status: 404 })
        }
        return HttpResponse.json(listingOf(folder))
      }),
      http.all('/api/filesystem/*', ({ request }) => {
        writes.push(`${request.method} ${request.url}`)
        return new HttpResponse(null, { status: 405 })
      }),
    )
  })

  afterEach(() => {
    document.body.innerHTML = ''
  })

  async function mountDialog (start: string | null) {
    const result = await mountWithPlugins(ServerDirectoryDialog, {
      props: { modelValue: true, start },
      attachTo: document.body,
    })
    await vi.waitFor(() => expect(selectedPath()).toBeTruthy())
    return result
  }

  it('opens at the start folder and selects the folder it navigates to', async () => {
    const { wrapper } = await mountDialog('/volume1')

    expect(dialogText()).toContain('backups')
    expect(dialogText()).toContain('perforce')

    folderItem('perforce').click()
    await vi.waitFor(() => expect(selectedPath()).toBe('/volume1/perforce'))
    expect(dialogText()).toContain('No subfolders')

    button('directoryUp').click()
    await vi.waitFor(() => expect(selectedPath()).toBe('/volume1'))
    folderItem('backups').click()
    await vi.waitFor(() => expect(selectedPath()).toBe('/volume1/backups'))

    button('selectDirectory').click()
    await flushPromises()

    expect(wrapper.emitted('select')).toEqual([['/volume1/backups']])
    expect(wrapper.emitted('update:modelValue')).toEqual([[false]])
  })

  it('shows the folders the daemon may not write to as read-only, and does not select them', async () => {
    const { wrapper } = await mountDialog(null)

    expect(folderItem('srv').textContent).toContain('read-only')
    expect(folderItem('volume1').textContent).not.toContain('read-only')
    expect(button('selectDirectory').disabled).toBe(true)
    expect(button('newFolder').classList).toContain('v-icon-btn--disabled')
    expect(dialogText()).toContain('cannot create files or folders here')

    folderItem('volume1').click()
    await vi.waitFor(() => expect(selectedPath()).toBe('/volume1'))
    expect(button('selectDirectory').disabled).toBe(false)
    expect(button('newFolder').classList).not.toContain('v-icon-btn--disabled')

    button('directoryUp').click()
    await vi.waitFor(() => expect(selectedPath()).toBe('/'))
    folderItem('srv').click()
    await vi.waitFor(() => expect(selectedPath()).toBe('/srv'))
    button('selectDirectory').click()
    await flushPromises()

    expect(wrapper.emitted('select')).toBeUndefined()
  })

  it('shows the part of the start folder that does not exist yet as new folders', async () => {
    const { wrapper } = await mountDialog('/volume1/perforce/main/root')

    expect(selectedPath()).toBe('/volume1/perforce/main/root')
    expect(listed).toEqual(['/volume1/perforce/main/root', '/volume1/perforce/main', '/volume1/perforce'])
    expect(button('selectDirectory').disabled).toBe(false)

    button('directoryUp').click()
    await flushPromises()
    expect(selectedPath()).toBe('/volume1/perforce/main')
    expect(folderItem('root').textContent).toContain('new')
    expect(listed).toHaveLength(3)

    button('selectDirectory').click()
    await flushPromises()
    expect(wrapper.emitted('select')).toEqual([['/volume1/perforce/main']])
  })

  it('adds a folder to the path without creating it on the machine', async () => {
    const { wrapper } = await mountDialog('/volume1/perforce')

    await addFolder('main')

    expect(selectedPath()).toBe('/volume1/perforce/main')
    await addFolder('root')
    expect(selectedPath()).toBe('/volume1/perforce/main/root')

    // The new folders stay while the user moves around.
    button('directoryUp').click()
    await flushPromises()
    button('directoryUp').click()
    await flushPromises()
    expect(selectedPath()).toBe('/volume1/perforce')
    folderItem('main').click()
    await flushPromises()
    folderItem('root').click()
    await flushPromises()

    button('selectDirectory').click()
    await flushPromises()

    expect(wrapper.emitted('select')).toEqual([['/volume1/perforce/main/root']])
    expect(writes).toEqual([])
  })

  it('refuses a new folder name with a slash or already taken', async () => {
    await mountDialog('/volume1')

    await addFolder('a/b')
    expect(dialogText()).toContain('cannot contain slashes')

    const input = document.querySelector<HTMLInputElement>('input[name="newFolderName"]')!
    input.value = 'backups'
    input.dispatchEvent(new Event('input'))
    await flushPromises()
    document.querySelector('#new-folder-form')!.dispatchEvent(new Event('submit', { cancelable: true }))
    await flushPromises()

    expect(dialogText()).toContain('already has this name')
    expect(selectedPath()).toBe('/volume1')
  })
})

describe('ServerDirectoryField', () => {
  beforeEach(() => {
    server.use(http.get('/api/filesystem/directories', ({ request }) => {
      const path = new URL(request.url).searchParams.get('path') ?? '/'
      return HttpResponse.json(listingOf(path))
    }))
  })

  afterEach(() => {
    document.body.innerHTML = ''
  })

  it('keeps the path typed and fills it with the folder picked', async () => {
    const value = ref('/volume1')
    const Host = defineComponent({
      setup: () => () => h(ServerDirectoryField, {
        'modelValue': value.value,
        'onUpdate:modelValue': (path: string) => {
          value.value = path
        },
        'label': 'Server directory',
        'name': 'serverDirectory',
      }),
    })
    await mountWithPlugins(Host, { attachTo: document.body })

    const input = document.querySelector<HTMLInputElement>('input[name="serverDirectory"]')!
    expect(input.value).toBe('/volume1')

    document.querySelector<HTMLButtonElement>('button[aria-label="Browse the folders of the server"]')!.click()
    await vi.waitFor(() => expect(dialogText()).toContain('perforce'))
    expect(document.querySelector('.server-directory-dialog .v-card-title')?.textContent).toBe('Server directory')

    folderItem('perforce').click()
    await vi.waitFor(() => expect(selectedPath()).toBe('/volume1/perforce'))
    button('selectDirectory').click()
    await flushPromises()

    expect(value.value).toBe('/volume1/perforce')
  })
})

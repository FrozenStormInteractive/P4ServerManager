// Types
import type { ServerDirectoryPreview } from '@/api/types'

// Utilities
import { http, HttpResponse } from 'msw'
import { afterEach, describe, expect, it, vi } from 'vitest'
import ServerDirectoryCaseWarning from '@/components/filesystem/ServerDirectoryCaseWarning.vue'
import { mountWithPlugins } from '@/testing/mount'
import { server } from '@/testing/server'

function usePreview (answer: (url: URL) => ServerDirectoryPreview) {
  const queries: URLSearchParams[] = []
  server.use(http.get('/api/instances/server-directory', ({ request }) => {
    const url = new URL(request.url)
    queries.push(url.searchParams)
    return HttpResponse.json(answer(url))
  }))
  return queries
}

function warning () {
  return document.querySelector('.server-directory-case-warning')
}

describe('ServerDirectoryCaseWarning', () => {
  afterEach(() => {
    document.body.innerHTML = ''
  })

  it('warns of a folder whose name differs from the default server directory by its case only', async () => {
    const queries = usePreview(() => ({
      path: '/volume1/P4ServerManager/Main',
      isDefault: true,
      caseVariant: '/volume1/P4ServerManager/main',
    }))

    await mountWithPlugins(ServerDirectoryCaseWarning, {
      props: { name: 'Main', serverDirectory: '' },
      attachTo: document.body,
    })

    await vi.waitFor(() => expect(warning()).not.toBeNull())
    expect(warning()!.textContent).toContain('/volume1/P4ServerManager/main already exists')
    expect(warning()!.textContent).toContain('/volume1/P4ServerManager/Main')
    expect(queries).toHaveLength(1)
    expect(queries[0]!.get('name')).toBe('Main')
    expect(queries[0]!.has('serverDirectory')).toBe(false)
  })

  it('asks about the server directory typed in the form', async () => {
    const queries = usePreview(url => ({
      path: url.searchParams.get('serverDirectory')!,
      isDefault: false,
      caseVariant: null,
    }))

    await mountWithPlugins(ServerDirectoryCaseWarning, {
      props: { name: 'Main', serverDirectory: '/srv/Perforce' },
      attachTo: document.body,
    })

    await vi.waitFor(() => expect(queries).toHaveLength(1))
    expect(queries[0]!.get('serverDirectory')).toBe('/srv/Perforce')
    expect(warning()).toBeNull()
  })

  it('asks nothing while the name cannot name a folder', async () => {
    const queries = usePreview(() => ({ path: '', isDefault: true, caseVariant: null }))

    await mountWithPlugins(ServerDirectoryCaseWarning, {
      props: { name: '1-not-a-name', serverDirectory: '' },
      attachTo: document.body,
    })

    await new Promise(resolve => setTimeout(resolve, 400))
    expect(queries).toHaveLength(0)
    expect(warning()).toBeNull()
  })
})

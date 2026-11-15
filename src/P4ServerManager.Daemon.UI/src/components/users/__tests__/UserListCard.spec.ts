// Utilities
import { flushPromises } from '@vue/test-utils'
import { http, HttpResponse } from 'msw'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import UserListCard from '@/components/users/UserListCard.vue'
import { useAuthStore } from '@/stores/auth'
import { useUsersStore } from '@/stores/users'
import { mountWithPlugins } from '@/testing/mount'
import { server } from '@/testing/server'

const admin = { id: 'u1', userName: 'admin', createdAt: '2026-11-14T10:00:00.000Z', lastSignInAt: null }
const jane = { id: 'u2', userName: 'jane', createdAt: '2026-11-14T11:00:00.000Z', lastSignInAt: null }

async function fill (name: string, value: string) {
  const element = document.querySelector<HTMLInputElement>(`[name="${CSS.escape(name)}"]`)!
  element.value = value
  element.dispatchEvent(new Event('input'))
  await flushPromises()
}

function button (label: string) {
  const element = [...document.querySelectorAll<HTMLButtonElement>('button')]
    .find(b => b.getAttribute('aria-label') === label || b.textContent?.trim() === label)
  if (!element) {
    throw new Error(`No button ${label}.`)
  }
  return element
}

async function mountCard (users = [admin, jane]) {
  server.use(http.get('/api/users', () => HttpResponse.json(users)))
  const result = await mountWithPlugins(UserListCard, { attachTo: document.body })
  useAuthStore(result.pinia).session = { setupRequired: false, user: admin }
  await useUsersStore(result.pinia).fetchAll()
  await flushPromises()
  return result
}

describe('UserListCard', () => {
  beforeEach(() => {
    document.body.innerHTML = ''
  })

  afterEach(() => {
    document.body.innerHTML = ''
  })

  it('lists the accounts and marks the one of the user', async () => {
    const { wrapper } = await mountCard()

    expect(wrapper.text()).toContain('admin')
    expect(wrapper.text()).toContain('jane')
    expect(wrapper.text()).toContain('You')
    expect(wrapper.text()).toContain('Never')
  })

  it('adds an account', async () => {
    const requests: unknown[] = []
    server.use(http.post('/api/users', async ({ request }) => {
      requests.push(await request.json())
      return HttpResponse.json({ ...jane, id: 'u3', userName: 'bob' }, { status: 201 })
    }))
    const { pinia } = await mountCard()

    button('Add account').click()
    await flushPromises()
    await fill('userName', 'bob')
    await fill('password', 'correct horse battery')
    await fill('confirmation', 'correct horse battery')
    document.querySelector('#user-form')!.dispatchEvent(new Event('submit', { cancelable: true }))

    await vi.waitFor(() => expect(requests).toEqual([{ userName: 'bob', password: 'correct horse battery' }]))
    await vi.waitFor(() => expect(useUsersStore(pinia).users.map(u => u.userName)).toEqual(['admin', 'bob', 'jane']))
  })

  it('deletes another account after a confirmation', async () => {
    const deleted: string[] = []
    server.use(http.delete('/api/users/:id', ({ params }) => {
      deleted.push(String(params.id))
      return new HttpResponse(null, { status: 204 })
    }))
    const { pinia } = await mountCard()

    document.querySelectorAll<HTMLButtonElement>('button[aria-label="Delete account"]')[1]!.click()
    await flushPromises()
    expect(document.body.textContent).toContain('Delete the account jane?')
    const confirm = [...document.querySelectorAll<HTMLButtonElement>('.v-overlay button')]
      .find(b => b.textContent?.trim() === 'Delete account')!
    confirm.click()

    await vi.waitFor(() => expect(deleted).toEqual(['u2']))
    await vi.waitFor(() => expect(useUsersStore(pinia).users.map(u => u.userName)).toEqual(['admin']))
  })

  it('cannot delete the last account', async () => {
    await mountCard([admin])

    expect(button('Delete account').disabled).toBe(true)
  })
})

// Utilities
import { flushPromises } from '@vue/test-utils'
import { http, HttpResponse } from 'msw'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import EditInstanceDialog from '@/components/instances/EditInstanceDialog.vue'
import { useInstancesStore } from '@/stores/instances'
import { createInstance } from '@/testing/fixtures'
import { mountWithPlugins } from '@/testing/mount'
import { server } from '@/testing/server'

const instance = createInstance({ description: 'The current text' })

function field (name: string) {
  const element = document.querySelector<HTMLInputElement | HTMLTextAreaElement>(`[name="${CSS.escape(name)}"]`)
  if (!element) {
    throw new Error(`No field named ${name}.`)
  }
  return element
}

async function fill (name: string, value: string) {
  const element = field(name)
  element.value = value
  element.dispatchEvent(new Event('input'))
  await flushPromises()
}

async function submit () {
  document.querySelector('#edit-instance-form')!.dispatchEvent(new Event('submit', { cancelable: true }))
  await flushPromises()
}

async function mountDialog () {
  const result = await mountWithPlugins(EditInstanceDialog, {
    props: { modelValue: true, instance },
    attachTo: document.body,
  })
  useInstancesStore(result.pinia).upsertInstance(instance)
  await flushPromises()
  return result
}

describe('EditInstanceDialog', () => {
  let requests: unknown[]

  beforeEach(() => {
    requests = []
    server.use(http.patch(`/api/instances/${instance.id}`, async ({ request }) => {
      const body = await request.json() as Record<string, unknown>
      requests.push(body)
      return HttpResponse.json({ ...instance, ...body })
    }))
  })

  afterEach(() => {
    document.body.innerHTML = ''
  })

  it('starts from the current name and description', async () => {
    await mountDialog()

    expect(field('name').value).toBe(instance.name)
    expect(field('description').value).toBe('The current text')
  })

  it('sends what changed and shows the instance saved by the daemon', async () => {
    const { wrapper, pinia } = await mountDialog()

    await fill('name', 'renamed')
    await submit()

    await vi.waitFor(() => expect(requests).toEqual([{ name: 'renamed' }]))
    await flushPromises()
    expect(useInstancesStore(pinia).getInstance(instance.id)?.name).toBe('renamed')
    expect(wrapper.emitted('update:modelValue')).toEqual([[false]])
  })

  it('removes the description with an empty text', async () => {
    await mountDialog()

    await fill('description', ' '.repeat(3))
    await submit()

    await vi.waitFor(() => expect(requests).toEqual([{ description: '' }]))
  })

  it('sends nothing when nothing changed', async () => {
    const { wrapper } = await mountDialog()

    await submit()

    await vi.waitFor(() => expect(wrapper.emitted('update:modelValue')).toEqual([[false]]))
    expect(requests).toEqual([])
  })

  it('checks the name before it sends it', async () => {
    const { wrapper } = await mountDialog()

    await fill('name', '1abc')
    await submit()

    await vi.waitFor(() => expect(document.body.textContent).toContain('Start with a letter'))
    expect(requests).toEqual([])
    expect(wrapper.emitted('update:modelValue')).toBeUndefined()
  })

  it('shows the error of the daemon on its field and stays open', async () => {
    server.use(http.patch(`/api/instances/${instance.id}`, () => HttpResponse.json(
      { title: 'One or more validation errors occurred.', errors: { name: ['An instance named other already exists.'] } },
      { status: 400 },
    )))
    const { wrapper } = await mountDialog()

    await fill('name', 'other')
    await submit()

    await vi.waitFor(() => expect(document.body.textContent).toContain('An instance named other already exists.'))
    expect(wrapper.emitted('update:modelValue')).toBeUndefined()
  })

  it('shows why the daemon refused the change', async () => {
    server.use(http.patch(`/api/instances/${instance.id}`, () =>
      HttpResponse.json({ detail: 'Another operation is in progress on this instance.' }, { status: 409 })))
    await mountDialog()

    await fill('name', 'renamed')
    await submit()

    await vi.waitFor(() => expect(document.body.textContent)
      .toContain('The server could not be changed: Another operation is in progress on this instance.'))
  })
})

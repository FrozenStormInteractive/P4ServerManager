// Utilities
import { flushPromises } from '@vue/test-utils'
import { http, HttpResponse } from 'msw'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import DeleteInstanceDialog from '@/components/instances/DeleteInstanceDialog.vue'
import { useInstancesStore } from '@/stores/instances'
import { createInstance } from '@/testing/fixtures'
import { mountWithPlugins } from '@/testing/mount'
import { server } from '@/testing/server'

const instance = createInstance()

function input (name: string) {
  const element = document.querySelector<HTMLInputElement>(`[name="${CSS.escape(name)}"]`)
  if (!element) {
    throw new Error(`No field named ${name}.`)
  }
  return element
}

// The first radio keeps the files, the second deletes them.
async function chooseFiles (deleteFiles: boolean) {
  const radios = document.querySelectorAll<HTMLInputElement>('input[type="radio"][name="deleteFiles"]')
  radios[deleteFiles ? 1 : 0]!.click()
  await flushPromises()
}

async function typeName (name: string) {
  const confirmation = input('confirmation')
  confirmation.value = name
  confirmation.dispatchEvent(new Event('input'))
  await flushPromises()
}

function deleteButton () {
  const button = [...document.querySelectorAll('button')].find(b => b.textContent?.trim() === 'Delete')
  if (!button) {
    throw new Error('No delete button.')
  }
  return button
}

async function mountDialog () {
  const result = await mountWithPlugins(DeleteInstanceDialog, {
    props: { modelValue: true, instance },
    attachTo: document.body,
  })
  useInstancesStore(result.pinia).upsertInstance(instance)
  await flushPromises()
  return result
}

describe('DeleteInstanceDialog', () => {
  let requests: URL[]

  beforeEach(() => {
    requests = []
    server.use(
      http.delete(`/api/instances/${instance.id}`, ({ request }) => {
        requests.push(new URL(request.url))
        return HttpResponse.json({ ...instance, runtimeStatus: 'Deleting' }, { status: 202 })
      }),
    )
  })

  afterEach(() => {
    document.body.innerHTML = ''
  })

  it('waits for a choice about the files', async () => {
    await mountDialog()

    const radios = [...document.querySelectorAll<HTMLInputElement>('input[type="radio"][name="deleteFiles"]')]
    expect(radios.map(radio => radio.checked)).toEqual([false, false])
    expect(deleteButton().disabled).toBe(true)
  })

  it('keeps the files when asked, and shows the instance being deleted', async () => {
    const { wrapper, pinia } = await mountDialog()

    await chooseFiles(false)
    await typeName(instance.name)
    deleteButton().click()
    await flushPromises()

    expect(requests.map(url => url.searchParams.get('deleteFiles'))).toEqual(['false'])
    // The deletion goes on in the daemon: the hub tells once the instance is gone.
    expect(useInstancesStore(pinia).getInstance(instance.id)?.runtimeStatus).toBe('Deleting')
    expect(wrapper.emitted('deleted')).toHaveLength(1)
  })

  it.each([false, true])('asks for the name of the instance, whether it deletes the files or not (%s)', async deleteFiles => {
    const { wrapper } = await mountDialog()

    await chooseFiles(deleteFiles)
    expect(deleteButton().disabled).toBe(true)
    await typeName('wrong')
    expect(deleteButton().disabled).toBe(true)

    await typeName(instance.name)
    deleteButton().click()
    await flushPromises()

    expect(requests.map(url => url.searchParams.get('deleteFiles'))).toEqual([String(deleteFiles)])
    expect(wrapper.emitted('deleted')).toHaveLength(1)
  })

  it('shows no list of files, only what deleting them means', async () => {
    await mountDialog()

    await chooseFiles(true)

    expect(document.body.textContent).toContain('Deletes what the server uses, wherever its folder is')
    expect(document.querySelector('.deleted-files')).toBeNull()
  })

  it('shows why the daemon refuses to delete the files', async () => {
    server.use(http.delete(`/api/instances/${instance.id}`, () => HttpResponse.json(
      { detail: 'The files of instance main cannot be deleted. /volume1 is a root of the file system.' },
      { status: 409 })))
    const { wrapper } = await mountDialog()

    await chooseFiles(true)
    await typeName(instance.name)
    deleteButton().click()
    await flushPromises()

    expect(document.body.textContent).toContain('The server could not be deleted: The files of instance main cannot be deleted.')
    expect(wrapper.emitted('deleted')).toBeUndefined()
  })

  it('shows why the daemon could not delete the instance', async () => {
    server.use(http.delete(`/api/instances/${instance.id}`, () =>
      HttpResponse.json({ detail: 'Another operation is in progress on this instance.' }, { status: 409 })))
    const { wrapper, pinia } = await mountDialog()

    await chooseFiles(false)
    await typeName(instance.name)
    deleteButton().click()
    await flushPromises()

    expect(document.body.textContent).toContain('The server could not be deleted: Another operation is in progress')
    expect(useInstancesStore(pinia).getInstance(instance.id)).toBeDefined()
    expect(wrapper.emitted('deleted')).toBeUndefined()
  })
})

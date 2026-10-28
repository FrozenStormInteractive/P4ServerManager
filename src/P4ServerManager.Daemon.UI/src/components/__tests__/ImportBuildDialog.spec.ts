// Utilities
import { flushPromises } from '@vue/test-utils'
import { http, HttpResponse } from 'msw'
import { afterEach, describe, expect, it } from 'vitest'
import ImportBuildDialog from '@/components/builds/ImportBuildDialog.vue'
import { useBuildsStore } from '@/stores/builds'
import { createBuild } from '@/testing/fixtures'
import { mountWithPlugins } from '@/testing/mount'
import { server } from '@/testing/server'

function importButton () {
  const button = [...document.querySelectorAll('button')].findLast(b => b.textContent?.trim() === 'Import')
  if (!button) {
    throw new Error('No import button.')
  }
  return button
}

describe('ImportBuildDialog', () => {
  afterEach(() => {
    document.body.innerHTML = ''
  })

  it('sends p4d and p4, and adds the build', async () => {
    let fields: string[] = []
    server.use(http.post('/api/p4d/builds/import', async ({ request }) => {
      const form = await request.formData()
      fields = [...form.keys()]
      return HttpResponse.json(createBuild({ source: 'Imported', extracted: false }), { status: 201 })
    }))
    const { wrapper, pinia } = await mountWithPlugins(ImportBuildDialog, {
      props: { modelValue: true },
      attachTo: document.body,
    })
    await flushPromises()

    const inputs = wrapper.findAllComponents({ name: 'VFileInput' })
    inputs[0]!.vm.$emit('update:modelValue', new File(['p4d'], 'p4d'))
    inputs[1]!.vm.$emit('update:modelValue', new File(['p4'], 'p4'))
    await flushPromises()
    importButton().click()
    await flushPromises()

    expect(fields).toEqual(['p4d', 'p4'])
    expect(useBuildsStore(pinia).sortedBuilds).toHaveLength(1)
    expect(wrapper.emitted('update:modelValue')).toEqual([[false]])
  })

  it('needs both files', async () => {
    await mountWithPlugins(ImportBuildDialog, { props: { modelValue: true }, attachTo: document.body })
    await flushPromises()

    importButton().click()
    await flushPromises()

    expect(document.body.textContent).toContain('Choose a file.')
  })

  it('shows why the daemon refused the build', async () => {
    server.use(http.post('/api/p4d/builds/import', () => HttpResponse.json(
      { detail: 'p4d 2025.2/3086021 is already in the archive.' },
      { status: 409 },
    )))
    const { wrapper } = await mountWithPlugins(ImportBuildDialog, {
      props: { modelValue: true },
      attachTo: document.body,
    })
    await flushPromises()

    const inputs = wrapper.findAllComponents({ name: 'VFileInput' })
    inputs[0]!.vm.$emit('update:modelValue', new File(['p4d'], 'p4d'))
    inputs[1]!.vm.$emit('update:modelValue', new File(['p4'], 'p4'))
    await flushPromises()
    importButton().click()
    await flushPromises()

    expect(document.body.textContent).toContain(
      'The build could not be imported: p4d 2025.2/3086021 is already in the archive.')
  })
})

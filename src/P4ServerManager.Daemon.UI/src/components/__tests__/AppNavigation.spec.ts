// Utilities
import { flushPromises } from '@vue/test-utils'
import { afterEach, describe, expect, it } from 'vitest'
import { defineComponent, h } from 'vue'
import { VLayout } from 'vuetify/components'
import AppNavigation from '@/components/AppNavigation.vue'
import { useInstancesStore } from '@/stores/instances'
import { createInstance } from '@/testing/fixtures'
import { mountWithPlugins } from '@/testing/mount'

// The navigation drawer needs a layout around it.
const Layout = defineComponent({
  render: () => h(VLayout, () => h(AppNavigation)),
})

describe('AppNavigation', () => {
  afterEach(() => {
    document.body.innerHTML = ''
  })

  it('links to the pages, then lists the instances with their status', async () => {
    const { wrapper, pinia } = await mountWithPlugins(Layout)
    const store = useInstancesStore(pinia)
    store.upsertInstance(createInstance({ id: 'b', name: 'zeta', runtimeStatus: 'Stopped' }))
    store.upsertInstance(createInstance({ id: 'a', name: 'alpha', runtimeStatus: 'Running' }))
    store.upsertInstance(createInstance({ id: 'c', name: 'new', state: 'Provisioning', runtimeStatus: 'Unknown' }))
    await flushPromises()

    const links = wrapper.findAll('a').map(a => [a.text(), a.attributes('href')])
    expect(links).toEqual([
      ['Dashboard', '/'],
      ['Instances', '/instances'],
      ['Settings', '/settings'],
      ['alpha', '/instances/a'],
      ['new', '/instances/c'],
      ['zeta', '/instances/b'],
    ])

    expect(wrapper.find('a[href="/instances/a"] [aria-label="Running"]').exists()).toBe(true)
    expect(wrapper.find('a[href="/instances/b"] [aria-label="Stopped"]').exists()).toBe(true)
    expect(wrapper.find('a[href="/instances/c"] .v-progress-circular').exists()).toBe(true)
  })

  it('says when there is no instance yet', async () => {
    const { wrapper, pinia } = await mountWithPlugins(Layout)
    useInstancesStore(pinia).loaded = true
    await flushPromises()

    expect(wrapper.text()).toContain('No instances yet')
  })
})

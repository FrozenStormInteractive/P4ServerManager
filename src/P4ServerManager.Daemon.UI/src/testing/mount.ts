// Types
import type { MountingOptions } from '@vue/test-utils'
import type { Component } from 'vue'

// Utilities
import { mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import { createVuetify } from 'vuetify'
import i18n from '@/plugins/i18n'
import { routes } from '@/router'

/**
 * Mounts a component with the plugins of the app: Vuetify, a fresh Pinia, i18n and a router on memory history.
 */
export async function mountWithPlugins (component: Component, options: MountingOptions<unknown> = {}) {
  const pinia = createPinia()
  setActivePinia(pinia)
  const router = createRouter({ history: createMemoryHistory(), routes })
  await router.push('/')
  await router.isReady()

  const wrapper = mount(component, {
    ...options,
    global: {
      ...options.global,
      plugins: [createVuetify(), pinia, i18n, router],
    },
  })
  return { wrapper, router, pinia }
}

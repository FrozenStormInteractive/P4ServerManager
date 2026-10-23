// Utilities
import { afterEach, describe, expect, it } from 'vitest'
import OperationProgress from '@/components/instances/OperationProgress.vue'
import { createInstance, createSnapshot } from '@/testing/fixtures'
import { mountWithPlugins } from '@/testing/mount'

describe('OperationProgress', () => {
  afterEach(() => {
    document.body.innerHTML = ''
  })

  it('translates the known steps and shows the skipped ones', async () => {
    const instance = createInstance({ state: 'Provisioning' })
    const { wrapper } = await mountWithPlugins(OperationProgress, {
      props: { instance, snapshot: createSnapshot(instance.id) },
    })

    expect(wrapper.text()).toContain('Provisioning')
    expect(wrapper.get('[data-step="resolve-build"]').text()).toContain('Get the p4d build')
    expect(wrapper.get('[data-step="resolve-build"]').text()).toContain('Downloading p4d (42%)')
    expect(wrapper.get('[data-step="create-super-user"]').text()).toContain('Skipped')
  })

  it('falls back to the titles sent by the daemon for an unknown operation', async () => {
    const instance = createInstance()
    const snapshot = createSnapshot(instance.id, {
      kind: 'Upgrade',
      steps: [{ key: 'swap-binaries', title: 'Swap the binaries', status: 'Pending', message: null }],
      currentStep: null,
    })
    const { wrapper } = await mountWithPlugins(OperationProgress, { props: { instance, snapshot } })

    expect(wrapper.text()).toContain('Upgrade')
    expect(wrapper.get('[data-step="swap-binaries"]').text()).toContain('Swap the binaries')
  })
})

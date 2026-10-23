// Utilities
import { afterAll, afterEach, beforeAll } from 'vitest'
import { server } from './server'

// happy-dom has no visual viewport, which the Vuetify overlays (dialogs, menus) position themselves against.
if (!('visualViewport' in globalThis) || !globalThis.visualViewport) {
  Object.defineProperty(globalThis, 'visualViewport', {
    configurable: true,
    value: {
      width: window.innerWidth,
      height: window.innerHeight,
      offsetLeft: 0,
      offsetTop: 0,
      pageLeft: 0,
      pageTop: 0,
      scale: 1,
      addEventListener: () => {},
      removeEventListener: () => {},
    },
  })
}

beforeAll(() => server.listen({ onUnhandledFrame: 'error' }))
afterEach(() => server.resetHandlers())
afterAll(() => server.close())

// Utilities
import { setupServer } from 'msw/node'

/**
 * Mock of the daemon API. Each test declares the handlers it needs with `server.use`; an unexpected request fails.
 */
export const server = setupServer()

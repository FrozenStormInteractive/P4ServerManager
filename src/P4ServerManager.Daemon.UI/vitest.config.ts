import { fileURLToPath } from 'node:url'
import { defineConfig, mergeConfig } from 'vitest/config'
import viteConfig from './vite.config.ts'

// The tests reuse the Vite config (plugins, `@` alias). happy-dom gives them a window and a location, against which
// the relative URLs of the API resolve.
export default mergeConfig(
  viteConfig,
  defineConfig({
    test: {
      environment: 'happy-dom',
      root: fileURLToPath(new URL('./', import.meta.url)),
      setupFiles: ['src/testing/setup.ts'],
      restoreMocks: true,
      unstubGlobals: true,
      // Vuetify imports the styles of its components, which Node cannot load: Vite transforms it to leave them out
      // (`styles` in the Vite config).
      server: { deps: { inline: ['vuetify'] } },
    },
  }),
)

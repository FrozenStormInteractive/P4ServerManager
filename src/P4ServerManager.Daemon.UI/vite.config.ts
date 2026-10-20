import {fileURLToPath, URL} from 'node:url'
import Vue from '@vitejs/plugin-vue'
import Fonts from 'unplugin-fonts/vite'
import {defineConfig} from 'vite'
import Vuetify, {transformAssetUrls} from 'vite-plugin-vuetify'

const serverUrl = process.env.SERVER_HTTP || process.env.SERVER_HTTPS || 'https://localhost:7145'

// https://vitejs.dev/config/
export default defineConfig({
  plugins: [
    Vue({
      template: {transformAssetUrls},
    }),
    // https://github.com/vuetifyjs/vuetify-loader/tree/master/packages/vite-plugin#readme
    Vuetify({
      autoImport: true,
      // The unit tests do not look at the styles: Vitest cannot load the ones built from the settings.
      styles: process.env.VITEST
        ? 'none'
        : {
          configFile: 'src/styles/settings.scss',
        },
    }),
    Fonts({
      fontsource: {
        families: [
          {
            name: 'Roboto',
            weights: [100, 300, 400, 500, 700, 900],
            styles: ['normal', 'italic'],
          },
        ],
      },
    }),
  ],
  define: {'process.env': {}},
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('src', import.meta.url)),
    }
  },
  server: {
    proxy: {
      '^/weatherforecast': {
        target: serverUrl,
        secure: false,
        changeOrigin: false,
      }
    },
  }
})

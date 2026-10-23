import { fileURLToPath, URL } from 'node:url'
import Vue from '@vitejs/plugin-vue'
import Fonts from 'unplugin-fonts/vite'
import { defineConfig } from 'vite'
import Vuetify, { transformAssetUrls } from 'vite-plugin-vuetify'

const serverUrl = process.env.ASPNETCORE_HTTPS_PORT
  ? `https://localhost:${process.env.ASPNETCORE_HTTPS_PORT}`
  : (process.env.ASPNETCORE_URLS ? process.env.ASPNETCORE_URLS.split(';', 1)[0] : 'https://localhost:7278')

// https://vitejs.dev/config/
export default defineConfig({
  plugins: [
    Vue({
      template: { transformAssetUrls },
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
  define: { 'process.env': {} },
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('src', import.meta.url)),
    },
  },
  server: {
    proxy: {
      '^/api': {
        target: serverUrl,
        secure: false,
        changeOrigin: false,
      },
      '^/hubs': {
        target: serverUrl,
        secure: false,
        changeOrigin: false,
        ws: true,
      },
    },
    port: Number.parseInt(process.env.DEV_SERVER_PORT || '50084'),
  },
})

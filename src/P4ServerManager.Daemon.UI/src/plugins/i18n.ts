import { createI18n } from 'vue-i18n'
import en from '@/locales/en.json'

export type MessageSchema = typeof en

export default createI18n<[MessageSchema], 'en'>({
  legacy: false,
  locale: 'en',
  fallbackLocale: 'en',
  messages: {
    en,
  },
  datetimeFormats: {
    en: {
      long: {
        year: 'numeric',
        month: 'short',
        day: 'numeric',
        hour: 'numeric',
        minute: '2-digit',
      },
    },
  },
})

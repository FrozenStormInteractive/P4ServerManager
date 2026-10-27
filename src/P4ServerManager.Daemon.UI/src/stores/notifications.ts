// Utilities
import { defineStore } from 'pinia'
import { ref } from 'vue'

export type NotificationColor = 'success' | 'info' | 'warning' | 'error'

export interface Notification {
  text: string
  color: NotificationColor
}

/**
 * Short messages shown for a few seconds, one after the other, then forgotten. What must stay on screen, such as the
 * last error of an instance, belongs to the page instead.
 */
export const useNotificationsStore = defineStore('notifications', () => {
  const queue = ref<Notification[]>([])

  function notify (text: string, color: NotificationColor = 'success') {
    queue.value.push({ text, color })
  }

  return { queue, notify }
})

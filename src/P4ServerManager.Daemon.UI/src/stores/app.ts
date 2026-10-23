// Utilities
import { defineStore } from 'pinia'
import { ref } from 'vue'

export const useAppStore = defineStore('app', () => {
  /**
   * Whether the navigation drawer is open. Null lets Vuetify decide from the screen size.
   */
  const drawer = ref<boolean | null>(null)
  const createDialogOpen = ref(false)

  function openCreateDialog () {
    createDialogOpen.value = true
  }

  return { drawer, createDialogOpen, openCreateDialog }
})

// Types
import type { CreateUserRequest, User } from '@/api/types'

// Utilities
import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import * as api from '@/api/users'

/**
 * The accounts of the web interface, managed from the settings.
 */
export const useUsersStore = defineStore('users', () => {
  const users = ref<User[]>([])
  const loaded = ref(false)
  const error = ref<string | null>(null)

  async function fetchAll () {
    try {
      users.value = await api.listUsers()
      error.value = null
      loaded.value = true
    } catch (error_) {
      error.value = error_ instanceof Error ? error_.message : String(error_)
    }
  }

  async function create (body: CreateUserRequest): Promise<User> {
    const user = await api.createUser(body)
    users.value = [...users.value, user].toSorted((a, b) => a.userName.localeCompare(b.userName))
    return user
  }

  async function setPassword (id: string, password: string) {
    await api.setUserPassword(id, { password })
  }

  async function setAdministrator (id: string, isAdministrator: boolean): Promise<User> {
    const user = await api.setUserAdministrator(id, { isAdministrator })
    users.value = users.value.map(existing => existing.id === id ? user : existing)
    return user
  }

  async function remove (id: string) {
    await api.deleteUser(id)
    users.value = users.value.filter(user => user.id !== id)
  }

  const administratorCount = computed(() => users.value.filter(user => user.isAdministrator).length)

  return { users, loaded, error, administratorCount, fetchAll, create, setPassword, setAdministrator, remove }
})

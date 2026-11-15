<template>
  <v-card prepend-icon="mdi-account-multiple-outline" :title="t('settings.sections.users')">
    <template #append>
      <v-btn
        color="primary"
        prepend-icon="mdi-plus"
        :text="t('users.add')"
        variant="tonal"
        @click="openDialog(null)"
      />
    </template>

    <v-card-text class="pb-0">{{ t('users.text') }}</v-card-text>

    <v-alert
      v-if="users.error"
      class="ma-4"
      density="compact"
      type="error"
      variant="tonal"
    >
      {{ t('users.loadError', { message: users.error }) }}
    </v-alert>

    <v-alert
      v-if="actionError"
      class="ma-4"
      closable
      density="compact"
      type="error"
      variant="tonal"
      @click:close="actionError = null"
    >
      {{ actionError }}
    </v-alert>

    <v-data-table
      :headers="headers"
      hide-default-footer
      hover
      item-value="id"
      :items="users.users"
      :items-per-page="-1"
      :loading="!users.loaded && !users.error"
    >
      <template #item.userName="{ item }">
        {{ item.userName }}
        <v-chip
          v-if="item.id === auth.user?.id"
          class="ml-2"
          size="x-small"
          :text="t('users.you')"
        />
      </template>

      <template #item.isAdministrator="{ item }">
        <v-switch
          :aria-label="t('users.administratorOf', { name: item.userName })"
          color="primary"
          density="compact"
          :disabled="isLastAdministrator(item) || changing === item.id"
          hide-details
          inset
          :model-value="item.isAdministrator"
          @update:model-value="value => askSetAdministrator(item, !!value)"
        />
      </template>

      <template #item.createdAt="{ item }">
        {{ d(new Date(item.createdAt), 'long') }}
      </template>

      <template #item.lastSignInAt="{ item }">
        {{ item.lastSignInAt ? d(new Date(item.lastSignInAt), 'long') : t('users.never') }}
      </template>

      <template #item.actions="{ item }">
        <div class="d-flex justify-end">
          <v-btn
            :aria-label="t('users.setPassword')"
            icon="mdi-form-textbox-password"
            size="small"
            variant="text"
            @click="openDialog(item)"
          />

          <v-btn
            :aria-label="t('users.remove')"
            color="error"
            :disabled="isLastAdministrator(item)"
            icon="mdi-delete-outline"
            size="small"
            variant="text"
            @click="askRemove(item)"
          />
        </div>
      </template>
    </v-data-table>

    <UserDialog v-model="dialogOpen" :user="selected" />

    <v-dialog v-model="demoteOpen" max-width="480">
      <v-card :title="t('users.demoteSelfTitle')">
        <v-card-text>{{ t('users.demoteSelfText') }}</v-card-text>

        <v-card-actions>
          <v-spacer />
          <v-btn :text="t('actions.cancel')" @click="demoteOpen = false" />

          <v-btn
            color="error"
            :text="t('users.demoteSelf')"
            variant="flat"
            @click="demoteSelf"
          />
        </v-card-actions>
      </v-card>
    </v-dialog>

    <v-dialog v-model="removeOpen" max-width="480">
      <v-card :title="t('users.removeTitle', { name: selected?.userName ?? '' })">
        <v-card-text>
          {{ selected?.id === auth.user?.id ? t('users.removeSelfText') : t('users.removeText') }}
        </v-card-text>

        <v-card-actions>
          <v-spacer />
          <v-btn :disabled="removing" :text="t('actions.cancel')" @click="removeOpen = false" />

          <v-btn
            color="error"
            :loading="removing"
            :text="t('users.remove')"
            variant="flat"
            @click="remove"
          />
        </v-card-actions>
      </v-card>
    </v-dialog>
  </v-card>
</template>

<script lang="ts" setup>
  // Types
  import type { User } from '@/api/types'

  // Composables
  import { computed, ref } from 'vue'
  import { useI18n } from 'vue-i18n'
  import { useRouter } from 'vue-router'

  // Components and stores
  import UserDialog from '@/components/users/UserDialog.vue'
  import { useAuthStore } from '@/stores/auth'
  import { useNotificationsStore } from '@/stores/notifications'
  import { useUsersStore } from '@/stores/users'

  /**
   * The accounts of the web interface: added, given a new password, named administrators and deleted here. The page
   * loads them. The last administrator can neither lose its right nor be deleted.
   */
  const { t, d } = useI18n()
  const auth = useAuthStore()
  const users = useUsersStore()
  const notifications = useNotificationsStore()
  const router = useRouter()

  const dialogOpen = ref(false)
  const removeOpen = ref(false)
  const selected = ref<User | null>(null)
  const removing = ref(false)
  const demoteOpen = ref(false)
  const changing = ref<string | null>(null)
  const actionError = ref<string | null>(null)

  const headers = computed(() => [
    { title: t('users.headers.userName'), key: 'userName', sortable: false },
    { title: t('users.headers.isAdministrator'), key: 'isAdministrator', sortable: false },
    { title: t('users.headers.createdAt'), key: 'createdAt', sortable: false },
    { title: t('users.headers.lastSignInAt'), key: 'lastSignInAt', sortable: false },
    { title: '', key: 'actions', sortable: false, align: 'end' as const },
  ])

  function openDialog (user: User | null) {
    selected.value = user
    dialogOpen.value = true
  }

  function isLastAdministrator (user: User) {
    return user.isAdministrator && users.administratorCount <= 1
  }

  function askSetAdministrator (user: User, isAdministrator: boolean) {
    if (!isAdministrator && user.id === auth.user?.id) {
      // The user would lose the access to this page: it confirms first.
      selected.value = user
      demoteOpen.value = true
      return
    }
    void setAdministrator(user, isAdministrator)
  }

  async function demoteSelf () {
    demoteOpen.value = false
    if (selected.value) {
      await setAdministrator(selected.value, false)
    }
  }

  async function setAdministrator (user: User, isAdministrator: boolean) {
    changing.value = user.id
    actionError.value = null
    try {
      const changed = await users.setAdministrator(user.id, isAdministrator)
      notifications.notify(t(
        isAdministrator ? 'users.administratorGranted' : 'users.administratorRevoked',
        { name: changed.userName },
      ))
      auth.userChanged(changed)
      if (!changed.isAdministrator && changed.id === auth.user?.id) {
        await router.replace({ name: 'dashboard' })
      }
    } catch (error) {
      actionError.value = error instanceof Error ? error.message : String(error)
    } finally {
      changing.value = null
    }
  }

  function askRemove (user: User) {
    selected.value = user
    removeOpen.value = true
  }

  async function remove () {
    const user = selected.value
    if (!user) {
      return
    }

    removing.value = true
    actionError.value = null
    try {
      await users.remove(user.id)
      removeOpen.value = false
      if (user.id === auth.user?.id) {
        // The sessions of a deleted account end, this one included.
        auth.sessionEnded()
        await router.replace({ name: 'login' })
        return
      }
      notifications.notify(t('users.removed', { name: user.userName }))
    } catch (error) {
      actionError.value = error instanceof Error ? error.message : String(error)
      removeOpen.value = false
    } finally {
      removing.value = false
    }
  }
</script>

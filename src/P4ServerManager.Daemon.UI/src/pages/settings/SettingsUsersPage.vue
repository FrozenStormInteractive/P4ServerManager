<template>
  <div>
    <UserListCard />
  </div>
</template>

<script lang="ts" setup>
  // Composables
  import { onMounted } from 'vue'

  // Components and stores
  import UserListCard from '@/components/users/UserListCard.vue'
  import { useUsersStore } from '@/stores/users'

  /**
   * The accounts that sign in to the web interface.
   */
  const users = useUsersStore()

  onMounted(() => void users.fetchAll())
</script>

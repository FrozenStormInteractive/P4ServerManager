<template>
  <v-app>
    <template v-if="shell">
      <AppBar />

      <AppNavigation />
    </template>

    <v-main>
      <router-view />
    </v-main>

    <CreateInstanceDialog v-if="shell" v-model="app.createDialogOpen" />

    <AppNotifications />
  </v-app>
</template>

<script lang="ts" setup>
  // Composables
  import { computed, onUnmounted, watch } from 'vue'
  import { useRoute } from 'vue-router'

  // Components and stores
  import AppBar from '@/components/AppBar.vue'
  import AppNavigation from '@/components/AppNavigation.vue'
  import AppNotifications from '@/components/AppNotifications.vue'
  import CreateInstanceDialog from '@/components/instances/CreateInstanceDialog.vue'
  import { useAppStore } from '@/stores/app'
  import { useAuthStore } from '@/stores/auth'
  import { useInstancesStore } from '@/stores/instances'

  const app = useAppStore()
  const auth = useAuthStore()
  const instances = useInstancesStore()
  const route = useRoute()

  // The sign-in and setup pages stand alone, without the bar and the navigation of the app.
  const shell = computed(() => auth.user !== null && !route.meta.public)

  // The instances are followed while a user is signed in: the daemon refuses the hub without a session.
  watch(() => auth.user?.id, userId => {
    if (userId) {
      instances.connect()
      void instances.fetchAll()
    } else {
      void instances.disconnect()
    }
  }, { immediate: true })

  onUnmounted(() => {
    void instances.disconnect()
  })
</script>

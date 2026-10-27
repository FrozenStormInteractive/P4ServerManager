<template>
  <v-app>
    <AppBar />

    <AppNavigation />

    <v-main>
      <router-view />
    </v-main>

    <CreateInstanceDialog v-model="app.createDialogOpen" />

    <AppNotifications />
  </v-app>
</template>

<script lang="ts" setup>
  // Composables
  import { onMounted, onUnmounted } from 'vue'

  // Components and stores
  import AppBar from '@/components/AppBar.vue'
  import AppNavigation from '@/components/AppNavigation.vue'
  import AppNotifications from '@/components/AppNotifications.vue'
  import CreateInstanceDialog from '@/components/instances/CreateInstanceDialog.vue'
  import { useAppStore } from '@/stores/app'
  import { useInstancesStore } from '@/stores/instances'

  const app = useAppStore()
  const instances = useInstancesStore()

  onMounted(() => {
    instances.connect()
    void instances.fetchAll()
  })

  onUnmounted(() => {
    void instances.disconnect()
  })
</script>

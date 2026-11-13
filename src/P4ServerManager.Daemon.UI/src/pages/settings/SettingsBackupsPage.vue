<template>
  <div>
    <BackupDestinationListCard />
  </div>
</template>

<script lang="ts" setup>
  // Composables
  import { onMounted } from 'vue'

  // Components and stores
  import BackupDestinationListCard from '@/components/backups/BackupDestinationListCard.vue'
  import { useBackupsStore } from '@/stores/backups'

  /**
   * The backup destinations of the daemon, which the instances choose from.
   */
  const backups = useBackupsStore()

  onMounted(() => void backups.fetchDestinations())
</script>

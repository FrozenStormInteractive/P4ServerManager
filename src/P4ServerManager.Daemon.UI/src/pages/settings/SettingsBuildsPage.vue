<template>
  <div>
    <BuildArchivePolicyCard class="mb-6" />

    <BuildListCard />
  </div>
</template>

<script lang="ts" setup>
  // Composables
  import { onMounted, onUnmounted } from 'vue'

  // Components and stores
  import BuildArchivePolicyCard from '@/components/builds/BuildArchivePolicyCard.vue'
  import BuildListCard from '@/components/builds/BuildListCard.vue'
  import { useBuildsStore } from '@/stores/builds'

  /**
   * The archive of p4d builds: how it fills, and the builds it holds. The builds are followed live while the page is
   * open.
   */
  const builds = useBuildsStore()

  onMounted(() => {
    void builds.fetchAll()
    builds.connect()
  })
  onUnmounted(() => void builds.disconnect())
</script>

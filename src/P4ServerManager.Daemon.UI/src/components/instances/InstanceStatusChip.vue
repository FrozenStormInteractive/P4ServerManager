<template>
  <v-chip
    :color="getInstanceStatusColor(status)"
    label
    size="small"
    :text="t(`status.${status}`)"
    variant="tonal"
  >
    <template #prepend>
      <InstanceStatusIndicator class="mr-2" :instance="instance" />
    </template>
  </v-chip>
</template>

<script lang="ts" setup>
  // Types
  import type { Instance } from '@/api/types'

  // Composables
  import { computed } from 'vue'
  import { useI18n } from 'vue-i18n'

  // Components and utilities
  import InstanceStatusIndicator from '@/components/instances/InstanceStatusIndicator.vue'
  import { getInstanceStatus, getInstanceStatusColor } from '@/utils/instanceStatus'

  const props = defineProps<{
    instance: Instance
  }>()

  const { t } = useI18n()
  const status = computed(() => getInstanceStatus(props.instance))
</script>

<template>
  <v-chip
    :color="getInstanceColor(instance)"
    label
    size="small"
    :text="text"
    variant="tonal"
  >
    <template #prepend>
      <InstanceStatusIndicator class="mr-2" :instance="instance" />
    </template>

    <template v-if="instance.maintenanceMode" #append>
      <v-icon class="ml-1" icon="mdi-wrench" size="small" />
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
  import { getInstanceColor, getInstanceStatus } from '@/utils/instanceStatus'

  const props = defineProps<{
    instance: Instance
  }>()

  const { t } = useI18n()
  const status = computed(() => getInstanceStatus(props.instance))
  const text = computed(() => {
    const label = t(`status.${status.value}`)
    return props.instance.maintenanceMode ? t('status.inMaintenance', { status: label }) : label
  })
</script>

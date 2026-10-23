<template>
  <v-progress-circular
    v-if="status === 'provisioning'"
    :aria-label="t('status.provisioning')"
    color="info"
    indeterminate
    size="14"
    width="2"
  />

  <v-icon
    v-else
    :aria-label="t(`status.${status}`)"
    :color="getInstanceStatusColor(status)"
    icon="mdi-circle"
    role="img"
    size="10"
  />
</template>

<script lang="ts" setup>
  // Types
  import type { Instance } from '@/api/types'

  // Composables
  import { computed } from 'vue'
  import { useI18n } from 'vue-i18n'

  // Utilities
  import { getInstanceStatus, getInstanceStatusColor } from '@/utils/instanceStatus'

  const props = defineProps<{
    instance: Instance
  }>()

  const { t } = useI18n()
  const status = computed(() => getInstanceStatus(props.instance))
</script>

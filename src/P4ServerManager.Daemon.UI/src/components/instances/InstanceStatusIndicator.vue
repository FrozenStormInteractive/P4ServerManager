<template>
  <v-progress-circular
    v-if="isInstanceStatusInProgress(status)"
    :aria-label="t(`status.${status}`)"
    :color="getInstanceColor(instance)"
    indeterminate
    size="10"
    width="2"
  />

  <v-icon
    v-else
    :aria-label="t(`status.${status}`)"
    :color="getInstanceColor(instance)"
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
  import { getInstanceColor, getInstanceStatus, isInstanceStatusInProgress } from '@/utils/instanceStatus'

  const props = defineProps<{
    instance: Instance
  }>()

  const { t } = useI18n()
  const status = computed(() => getInstanceStatus(props.instance))
</script>

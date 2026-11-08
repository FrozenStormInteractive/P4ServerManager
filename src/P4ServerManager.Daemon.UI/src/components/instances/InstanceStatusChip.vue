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

    <template v-if="instance.maintenanceMode || superUserProblem" #append>
      <v-icon v-if="instance.maintenanceMode" class="ml-1" icon="mdi-wrench" size="small" />

      <v-icon
        v-if="superUserProblem"
        :aria-label="t('superUser.warning.title')"
        class="ml-1"
        color="warning"
        data-super-user-warning
        icon="mdi-account-alert"
        role="img"
        size="small"
        :title="t('superUser.warning.title')"
      />
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
  import { getSuperUserProblem } from '@/utils/superUser'

  const props = defineProps<{
    instance: Instance
  }>()

  const { t } = useI18n()
  const status = computed(() => getInstanceStatus(props.instance))
  // Only a ready instance has a super user to warn about: one being provisioned or failed has its own status.
  const superUserProblem = computed(() =>
    props.instance.state === 'Ready' ? getSuperUserProblem(props.instance) : null,
  )
  const text = computed(() => {
    const label = t(`status.${status.value}`)
    return props.instance.maintenanceMode ? t('status.inMaintenance', { status: label }) : label
  })
</script>

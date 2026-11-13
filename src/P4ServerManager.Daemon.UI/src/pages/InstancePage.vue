<template>
  <v-navigation-drawer v-model="drawer" width="232">
    <v-list :aria-label="t('instance.openSections')" density="compact" nav>
      <v-list-subheader>{{ instance?.name }}</v-list-subheader>

      <v-list-item
        exact
        prepend-icon="mdi-information-outline"
        :title="t('instance.sections.overview')"
        :to="{ name: 'instance', params: { id } }"
      />

      <v-list-item
        exact
        prepend-icon="mdi-heart-pulse"
        :title="t('instance.sections.health')"
        :to="{ name: 'instance-health', params: { id } }"
      >
        <template v-if="healthIndicator" #append>
          <v-progress-circular
            v-if="healthIndicator === 'running'"
            color="info"
            :data-health="healthIndicator"
            indeterminate
            size="14"
            width="2"
          />

          <v-icon
            v-else
            color="error"
            :data-health="healthIndicator"
            icon="mdi-alert-circle"
            size="small"
          />
        </template>
      </v-list-item>

      <v-list-item
        exact
        prepend-icon="mdi-cloud-upload-outline"
        :title="t('instance.sections.backups')"
        :to="{ name: 'instance-backups', params: { id } }"
      >
        <template v-if="backups.getRunningRun(id)" #append>
          <v-progress-circular
            color="info"
            data-backup="running"
            indeterminate
            size="14"
            width="2"
          />
        </template>
      </v-list-item>
    </v-list>
  </v-navigation-drawer>

  <div v-if="mobile" class="d-flex align-center ga-2 px-4 pt-4">
    <v-btn
      :aria-label="t('instance.openSections')"
      icon="mdi-menu-open"
      variant="text"
      @click="drawer = !drawer"
    />

    <span class="text-title-medium">{{ instance?.name }}</span>
  </div>

  <router-view />
</template>

<script lang="ts" setup>
  // Composables
  import { computed, ref, watch } from 'vue'
  import { useI18n } from 'vue-i18n'
  import { useDisplay } from 'vuetify'

  // Stores
  import { useBackupsStore } from '@/stores/backups'
  import { useHealthChecksStore } from '@/stores/healthChecks'
  import { useInstancesStore } from '@/stores/instances'

  /**
   * Lists the sections of an instance, each a child route, in a drawer of its own beside the navigation of the app. On
   * small screens the drawer is temporary and opens from the button above the section.
   */
  const props = defineProps<{
    id: string
  }>()

  const { t } = useI18n()
  const { mobile } = useDisplay()
  const instances = useInstancesStore()
  const healthChecks = useHealthChecksStore()
  const backups = useBackupsStore()

  // Left null, the drawer opens on wide screens and stays closed on small ones.
  const drawer = ref<boolean | null>(null)

  const instance = computed(() => instances.getInstance(props.id))

  const healthIndicator = computed(() => {
    if (healthChecks.getRunningRun(props.id)) {
      return 'running'
    }
    return healthChecks.getHealth(props.id).status === 'problems' ? 'problems' : null
  })

  // The section of the health tells the state of the instance from the other sections too.
  watch(() => props.id, id => void healthChecks.fetchRuns(id), { immediate: true })
</script>

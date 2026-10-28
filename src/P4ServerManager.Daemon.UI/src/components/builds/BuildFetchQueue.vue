<template>
  <div v-if="fetches.length > 0" class="mx-4 mt-4" data-fetches>
    <div class="text-title-small mb-2">{{ t('fetches.title') }}</div>

    <v-list border class="py-0" density="compact" rounded>
      <template v-for="(fetch, index) in fetches" :key="fetch.release">
        <v-divider v-if="index > 0" />

        <v-list-item :data-fetch="fetch.release">
          <template #prepend>
            <!-- The same room for the clock and the progress, so that the releases line up. -->
            <div class="d-flex me-4">
              <v-icon
                v-if="fetch.state === 'Queued'"
                class="text-medium-emphasis"
                icon="mdi-clock-outline"
              />

              <v-progress-circular
                v-else
                color="primary"
                :indeterminate="fetch.percent === null"
                :model-value="fetch.percent ?? 0"
                size="24"
                width="3"
              />
            </div>
          </template>

          <v-list-item-title>
            {{ fetch.release }}
            <span v-if="fetch.build" class="text-medium-emphasis">({{ fetch.build }})</span>
          </v-list-item-title>

          <v-list-item-subtitle>{{ describe(fetch) }}</v-list-item-subtitle>

          <template v-if="fetch.percent !== null" #append>
            <span class="text-body-small text-medium-emphasis">{{ fetch.percent }}%</span>
          </template>
        </v-list-item>
      </template>
    </v-list>
  </div>
</template>

<script lang="ts" setup>
  // Types
  import type { BuildFetch } from '@/api/types'

  // Composables
  import { computed } from 'vue'
  import { useI18n } from 'vue-i18n'

  // Stores
  import { useBuildsStore } from '@/stores/builds'

  /**
   * The builds the daemon fetches from Perforce: the one under way, with its progress, then those waiting their turn.
   * Hidden while there is none.
   */
  const { t } = useI18n()
  const builds = useBuildsStore()

  const fetches = computed(() => builds.status?.fetches ?? [])

  function describe (fetch: BuildFetch) {
    return t(`fetches.states.${fetch.state}`, { file: fetch.fileName ?? 'p4d', build: fetch.build ?? '' })
  }
</script>

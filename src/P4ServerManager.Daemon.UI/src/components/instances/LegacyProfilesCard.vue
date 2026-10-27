<template>
  <v-alert
    v-if="loadError"
    class="mb-4"
    density="compact"
    type="error"
    variant="tonal"
  >
    {{ t('legacyProfiles.loadError', { message: loadError }) }}
  </v-alert>

  <v-card v-if="pending.length > 0" class="mb-6" prepend-icon="mdi-import" :title="t('legacyProfiles.title')">
    <v-card-text class="pb-0">{{ t('legacyProfiles.text') }}</v-card-text>

    <v-list lines="three">
      <v-list-item v-for="profile in pending" :key="profile.id" :data-profile="profile.id">
        <v-list-item-title>{{ profile.name ?? t('legacyProfiles.unnamed') }}</v-list-item-title>

        <v-list-item-subtitle>
          {{ profile.rootPath ?? profile.id }} ·
          {{ profile.port === null ? t('legacyProfiles.noPort') : formatPort({ port: profile.port, ssl: profile.ssl }) }}
        </v-list-item-subtitle>

        <div v-for="problem in profile.problems" :key="problem" class="text-body-small text-error mt-1">
          {{ problem }}
        </div>

        <div v-if="profile.ignoredSettings.length > 0" class="text-body-small text-medium-emphasis mt-1">
          {{ t('legacyProfiles.ignored', { settings: profile.ignoredSettings.join(', ') }) }}
        </div>

        <template #append>
          <div class="d-flex ga-2">
            <v-btn
              :disabled="busy !== null"
              :loading="busy === profile.id"
              :text="t('actions.dismiss')"
              :title="t('legacyProfiles.dismissHint')"
              variant="text"
              @click="dismiss(profile)"
            />

            <v-btn
              color="primary"
              :disabled="profile.problems.length > 0 || busy !== null"
              :text="t('actions.import')"
              variant="tonal"
              @click="openImport(profile)"
            />
          </div>
        </template>
      </v-list-item>
    </v-list>

    <v-alert
      v-if="dismissError"
      class="ma-4"
      density="compact"
      type="error"
      variant="tonal"
    >
      {{ t('legacyProfiles.dismissError', { message: dismissError }) }}
    </v-alert>
  </v-card>

  <ImportLegacyProfileDialog v-model="importOpen" :profile="selected" @imported="onImported" />
</template>

<script lang="ts" setup>
  // Types
  import type { Instance, LegacyProfile } from '@/api/types'

  // Composables
  import { computed, onMounted, ref } from 'vue'
  import { useI18n } from 'vue-i18n'

  // Components and utilities
  import { dismissLegacyProfile, listLegacyProfiles } from '@/api/legacyProfiles'
  import ImportLegacyProfileDialog from '@/components/instances/ImportLegacyProfileDialog.vue'
  import { formatPort } from '@/utils/instancePort'

  const { t } = useI18n()

  const profiles = ref<LegacyProfile[]>([])
  const loadError = ref<string | null>(null)
  const dismissError = ref<string | null>(null)
  const busy = ref<string | null>(null)
  const selected = ref<LegacyProfile | null>(null)
  const importOpen = ref(false)

  // An imported profile stays in the folder of the daemon: it is shown no more.
  const pending = computed(() => profiles.value.filter(profile => profile.instanceId === null))

  onMounted(load)

  async function load () {
    try {
      profiles.value = await listLegacyProfiles()
      loadError.value = null
    } catch (error) {
      loadError.value = error instanceof Error ? error.message : String(error)
    }
  }

  function openImport (profile: LegacyProfile) {
    selected.value = profile
    importOpen.value = true
  }

  function onImported (instance: Instance) {
    profiles.value = profiles.value.map(profile =>
      profile.id === selected.value?.id ? { ...profile, instanceId: instance.id } : profile)
  }

  async function dismiss (profile: LegacyProfile) {
    busy.value = profile.id
    dismissError.value = null
    try {
      await dismissLegacyProfile(profile.id)
      profiles.value = profiles.value.filter(p => p.id !== profile.id)
    } catch (error) {
      dismissError.value = error instanceof Error ? error.message : String(error)
    } finally {
      busy.value = null
    }
  }
</script>

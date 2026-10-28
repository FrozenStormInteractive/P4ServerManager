<template>
  <v-card prepend-icon="mdi-archive-sync-outline" :title="t('buildPolicy.title')">
    <v-card-text>
      <v-alert
        v-if="loadError"
        class="mb-4"
        density="compact"
        type="error"
        variant="tonal"
      >
        {{ t('buildPolicy.loadError', { message: loadError }) }}
        <template #append>
          <v-btn :text="t('actions.retry')" variant="text" @click="load" />
        </template>
      </v-alert>

      <v-form v-if="policy" ref="form" :disabled="saving" @submit.prevent="save">
        <v-switch
          v-model="policy.autoDownload"
          color="primary"
          :hint="t('buildPolicy.autoDownloadHint')"
          inset
          :label="t('buildPolicy.autoDownload')"
          name="autoDownload"
          persistent-hint
        />

        <v-row class="mt-2" density="compact">
          <v-col cols="12" sm="4">
            <v-text-field
              v-model.number="policy.checkIntervalHours"
              :disabled="!policy.autoDownload"
              :error-messages="serverErrors.checkIntervalHours"
              :label="t('buildPolicy.interval')"
              max="720"
              min="1"
              name="checkIntervalHours"
              :rules="rules.interval"
              :suffix="t('buildPolicy.intervalSuffix')"
              type="number"
            />
          </v-col>

          <v-col cols="12" sm="8">
            <v-radio-group
              v-model="policy.scope"
              hide-details
              inline
              :label="t('buildPolicy.scope')"
              name="scope"
            >
              <v-radio :label="t('buildPolicy.scopes.SelectedReleases')" value="SelectedReleases" />
              <v-radio :label="t('buildPolicy.scopes.AllReleases')" value="AllReleases" />
            </v-radio-group>
          </v-col>
        </v-row>

        <v-select
          v-if="policy.scope === 'SelectedReleases'"
          v-model="policy.releases"
          chips
          closable-chips
          :error-messages="serverErrors.releases"
          :hint="t('buildPolicy.releasesHint')"
          :items="releaseItems"
          :label="t('buildPolicy.releases')"
          :loading="versionsLoading"
          multiple
          name="releases"
          persistent-hint
          :rules="rules.releases"
        />

        <v-row class="mt-2" density="compact">
          <v-col cols="12" sm="4">
            <v-switch
              v-model="keepAll"
              color="primary"
              hide-details
              inset
              :label="t('buildPolicy.keepAll')"
              name="keepAll"
            />
          </v-col>

          <v-col cols="12" sm="8">
            <v-text-field
              v-if="!keepAll"
              v-model.number="keepLatest"
              :error-messages="serverErrors.keepLatestPerRelease"
              :hint="t('buildPolicy.keepLatestHint')"
              :label="t('buildPolicy.keepLatest')"
              min="1"
              name="keepLatestPerRelease"
              persistent-hint
              :rules="rules.keep"
              type="number"
            />
          </v-col>
        </v-row>

        <v-alert
          v-if="saveError"
          class="mt-4"
          density="compact"
          type="error"
          variant="tonal"
        >
          {{ t('buildPolicy.error', { message: saveError }) }}
        </v-alert>
      </v-form>
    </v-card-text>

    <v-divider />

    <v-card-text class="d-flex flex-wrap align-center ga-4">
      <div class="flex-grow-1">
        <div class="text-body-medium" data-status>
          {{ status?.checking ? t('buildPolicy.checking') : t('buildPolicy.idle') }}
        </div>

        <div class="text-body-small text-medium-emphasis">
          {{ status?.lastCheckAt ? t('buildPolicy.lastCheck', { date: d(new Date(status.lastCheckAt), 'long') }) : t('buildPolicy.neverChecked') }}
          <template v-if="status?.nextCheckAt">
            · {{ t('buildPolicy.nextCheck', { date: d(new Date(status.nextCheckAt), 'long') }) }}
          </template>
        </div>
      </div>

      <v-btn
        :disabled="checkRequested || status?.checking"
        :loading="checkRequested && !status?.checking"
        prepend-icon="mdi-refresh"
        :text="t('actions.checkNow')"
        variant="text"
        @click="checkNow"
      />

      <v-btn
        color="primary"
        :disabled="!policy"
        :loading="saving"
        :text="t('actions.save')"
        variant="flat"
        @click="save"
      />
    </v-card-text>

    <v-progress-linear
      v-if="status?.checking"
      color="primary"
      data-archive-progress
      indeterminate
    />

    <v-alert
      v-if="status?.lastCheckError"
      class="ma-4 mt-0"
      density="compact"
      type="warning"
      variant="tonal"
    >
      <div class="text-pre-wrap">{{ t('buildPolicy.checkError', { message: status.lastCheckError }) }}</div>
    </v-alert>
  </v-card>
</template>

<script lang="ts" setup>
  // Types
  import type { BuildArchivePolicy, P4dVersion } from '@/api/types'
  import type { VForm } from 'vuetify/components'

  // Composables
  import { computed, onMounted, ref, useTemplateRef, watch } from 'vue'
  import { useI18n } from 'vue-i18n'

  // API, stores and utilities
  import { checkArchive, getArchivePolicy } from '@/api/builds'
  import { ApiError } from '@/api/client'
  import { listVersions } from '@/api/p4d'
  import { useBuildsStore } from '@/stores/builds'
  import { useNotificationsStore } from '@/stores/notifications'

  type Rule = (value: unknown) => true | string

  /**
   * Edits the policy of the archive: whether and how often the daemon checks the Perforce FTP, which releases it
   * downloads, and how many builds it keeps. Also shows what the archive worker does.
   */
  const { t, d } = useI18n()
  const builds = useBuildsStore()
  const notifications = useNotificationsStore()
  const form = useTemplateRef<VForm>('form')

  const DEFAULT_KEEP = 5

  const policy = ref<BuildArchivePolicy | null>(null)
  const keepAll = ref(true)
  const keepLatest = ref<number | string>(DEFAULT_KEEP)
  const loadError = ref<string | null>(null)
  const saveError = ref<string | null>(null)
  const serverErrors = ref<Record<string, string[]>>({})
  const saving = ref(false)
  const versions = ref<P4dVersion[]>([])
  const versionsLoading = ref(false)
  // From the click until the worker reports the check: the status only follows once the daemon has it.
  const checkRequested = ref(false)

  const status = computed(() => builds.status)

  watch(() => status.value?.checking, checking => {
    if (checking) {
      checkRequested.value = false
    }
  })

  // The releases of the Perforce FTP and of the archive, plus those of the policy the list may lack.
  const releaseItems = computed(() => {
    const known = versions.value.map(version => version.release)
    const extra = (policy.value?.releases ?? []).filter(release => !known.includes(release))
    return [...known, ...extra]
  })

  const rules: Record<string, Rule[]> = {
    interval: [value => (Number.isInteger(Number(value)) && Number(value) >= 1 && Number(value) <= 720)
      || t('buildPolicy.rules.interval')],
    // Without the automatic download, the releases only matter to a check asked for by hand.
    releases: [value => !policy.value?.autoDownload || (Array.isArray(value) && value.length > 0)
      || t('buildPolicy.rules.releases')],
    keep: [value => (Number.isInteger(Number(value)) && Number(value) >= 1) || t('buildPolicy.rules.keep')],
  }

  onMounted(() => {
    void load()
    void loadVersions()
  })

  async function load () {
    loadError.value = null
    try {
      const loaded = await getArchivePolicy()
      policy.value = { ...loaded, releases: [...loaded.releases] }
      keepAll.value = loaded.keepLatestPerRelease === null
      keepLatest.value = loaded.keepLatestPerRelease ?? DEFAULT_KEEP
    } catch (error) {
      loadError.value = error instanceof Error ? error.message : String(error)
    }
  }

  async function loadVersions () {
    versionsLoading.value = true
    try {
      versions.value = await listVersions()
    } catch {
      // The releases of the policy are still listed: the field stays usable.
    } finally {
      versionsLoading.value = false
    }
  }

  async function save () {
    if (!policy.value) {
      return
    }
    const result = await form.value?.validate()
    if (result && !result.valid) {
      return
    }

    saving.value = true
    saveError.value = null
    serverErrors.value = {}
    try {
      const saved = await builds.savePolicy({
        ...policy.value,
        checkIntervalHours: Number(policy.value.checkIntervalHours),
        keepLatestPerRelease: keepAll.value ? null : Number(keepLatest.value),
      })
      policy.value = { ...saved, releases: [...saved.releases] }
      notifications.notify(t('buildPolicy.saved'))
    } catch (error) {
      if (error instanceof ApiError) {
        serverErrors.value = error.fieldErrors
      }
      saveError.value = error instanceof Error ? error.message : String(error)
    } finally {
      saving.value = false
    }
  }

  async function checkNow () {
    checkRequested.value = true
    try {
      await checkArchive()
    } catch (error) {
      checkRequested.value = false
      saveError.value = error instanceof Error ? error.message : String(error)
    }
  }
</script>

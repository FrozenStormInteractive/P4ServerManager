<template>
  <v-dialog v-model="open" max-width="480">
    <v-card :title="t('downloadBuild.title')">
      <v-card-text>
        <p class="text-body-medium mb-4">{{ t('downloadBuild.text') }}</p>

        <v-alert
          v-if="versionsError"
          class="mb-4"
          density="compact"
          type="error"
          variant="tonal"
        >
          {{ t('createInstance.versionsError', { message: versionsError }) }}
          <template #append>
            <v-btn :text="t('actions.retry')" variant="text" @click="loadVersions" />
          </template>
        </v-alert>

        <v-select
          v-model="release"
          :disabled="submitting"
          :item-props="itemProps"
          item-title="title"
          item-value="release"
          :items="versionItems"
          :label="t('downloadBuild.release')"
          :loading="versionsLoading"
          name="release"
          :no-data-text="t('createInstance.noVersions')"
        />

        <v-alert
          v-if="submitError"
          class="mt-4"
          density="compact"
          type="error"
          variant="tonal"
        >
          {{ t('downloadBuild.error', { message: submitError }) }}
        </v-alert>
      </v-card-text>

      <v-card-actions>
        <v-spacer />
        <v-btn :disabled="submitting" :text="t('actions.cancel')" @click="open = false" />

        <v-btn
          color="primary"
          :disabled="!release"
          :loading="submitting"
          :text="t('downloadBuild.submit')"
          variant="flat"
          @click="submit"
        />
      </v-card-actions>
    </v-card>
  </v-dialog>
</template>

<script lang="ts" setup>
  // Types
  import type { P4dVersion } from '@/api/types'

  // Composables
  import { computed, ref, watch } from 'vue'
  import { useI18n } from 'vue-i18n'

  // API and stores
  import { downloadBuild } from '@/api/builds'
  import { listVersions } from '@/api/p4d'
  import { useBuildsStore } from '@/stores/builds'
  import { useNotificationsStore } from '@/stores/notifications'

  /**
   * Has the daemon fetch the current build of a release from Perforce into the archive. The download goes on in the
   * daemon: the build appears in the list once it is archived.
   */
  const open = defineModel<boolean>({ default: false })

  const { t } = useI18n()
  const notifications = useNotificationsStore()
  const builds = useBuildsStore()

  const versions = ref<P4dVersion[]>([])
  const versionsLoading = ref(false)
  const versionsError = ref<string | null>(null)
  const release = ref<string | null>(null)
  const submitting = ref(false)
  const submitError = ref<string | null>(null)

  // A release already in the queue cannot be asked for twice.
  const fetching = computed(() => (builds.status?.fetches ?? []).map(fetch => fetch.release))

  const versionItems = computed(() => versions.value.map(version => ({
    release: version.release,
    title: `${version.version} (${version.release})`,
    fetching: fetching.value.includes(version.release),
  })))

  function itemProps (item: { fetching: boolean }) {
    return item.fetching ? { disabled: true, subtitle: t('downloadBuild.alreadyFetching') } : {}
  }

  watch(open, value => {
    if (value) {
      release.value = null
      submitError.value = null
      void loadVersions()
    }
  }, { immediate: true })

  async function loadVersions () {
    versionsLoading.value = true
    versionsError.value = null
    try {
      versions.value = await listVersions()
      release.value ??= versionItems.value.find(item => !item.fetching)?.release ?? null
    } catch (error) {
      versionsError.value = error instanceof Error ? error.message : String(error)
    } finally {
      versionsLoading.value = false
    }
  }

  async function submit () {
    if (!release.value) {
      return
    }

    submitting.value = true
    submitError.value = null
    try {
      await downloadBuild(release.value)
      notifications.notify(t('builds.downloading', { release: release.value }), 'info')
    } catch (error) {
      submitError.value = error instanceof Error ? error.message : String(error)
      return
    } finally {
      submitting.value = false
    }

    open.value = false
  }
</script>

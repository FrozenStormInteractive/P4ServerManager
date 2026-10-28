<template>
  <v-dialog v-model="open" max-width="560">
    <v-card :title="t('upgradeInstance.title', { name: instance.name })">
      <v-card-text>
        <p class="text-body-medium mb-4">
          {{ t('upgradeInstance.current', { version: currentVersion }) }}
        </p>

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
          :hint="t('upgradeInstance.versionHint')"
          item-title="title"
          item-value="release"
          :items="versionItems"
          :label="t('createInstance.version')"
          :loading="versionsLoading"
          name="release"
          :no-data-text="t('upgradeInstance.noVersions')"
          persistent-hint
        />

        <!-- Always in place, so the dialog does not grow as the release changes. -->
        <v-select
          v-model="build"
          class="mt-4"
          :disabled="submitting || buildItems.length <= 1"
          :hint="t('createInstance.buildHint')"
          item-title="title"
          item-value="build"
          :items="buildItems"
          :label="t('createInstance.build')"
          name="build"
          persistent-hint
        />

        <v-alert
          class="mt-4"
          density="compact"
          type="warning"
          variant="tonal"
        >
          {{ t(instance.runtimeStatus === 'Running' ? 'upgradeInstance.warningRunning' : 'upgradeInstance.warningStopped') }}
        </v-alert>

        <v-alert
          v-if="submitError"
          class="mt-4"
          density="compact"
          type="error"
          variant="tonal"
        >
          {{ t('upgradeInstance.error', { message: submitError }) }}
        </v-alert>
      </v-card-text>

      <v-card-actions>
        <v-spacer />
        <v-btn :disabled="submitting" :text="t('actions.cancel')" @click="open = false" />

        <v-btn
          color="primary"
          :disabled="!release"
          :loading="submitting"
          :text="t('actions.upgrade')"
          variant="flat"
          @click="submit"
        />
      </v-card-actions>
    </v-card>
  </v-dialog>
</template>

<script lang="ts" setup>
  // Types
  import type { Instance, P4dVersion } from '@/api/types'

  // Composables
  import { computed, ref, watch } from 'vue'
  import { useI18n } from 'vue-i18n'

  // API, stores and utilities
  import { listVersions } from '@/api/p4d'
  import { useInstancesStore } from '@/stores/instances'
  import { compareReleases } from '@/utils/release'

  /**
   * Upgrades an instance to the latest build of a p4d release, or to a build of it from the archive: its own release,
   * for a newer patch, or a newer one. A database cannot go back to an older release or build, so those are not
   * offered.
   */
  const props = defineProps<{
    instance: Instance
  }>()

  const emit = defineEmits<{
    upgraded: []
  }>()

  const open = defineModel<boolean>({ default: false })

  const { t } = useI18n()
  const instances = useInstancesStore()

  const versions = ref<P4dVersion[]>([])
  const versionsLoading = ref(false)
  const versionsError = ref<string | null>(null)
  const release = ref<string | null>(null)
  // Null for the latest build of the release.
  const build = ref<string | null>(null)
  const submitting = ref(false)
  const submitError = ref<string | null>(null)

  const currentVersion = computed(() => props.instance.version
    ? `${props.instance.version}/${props.instance.build} (${props.instance.release})`
    : props.instance.release)

  const versionItems = computed(() => versions.value
    .filter(version => compareReleases(version.release, props.instance.release) >= 0)
    .map(version => ({
      release: version.release,
      title: version.release === props.instance.release
        ? t('upgradeInstance.latestPatch', { version: version.version, release: version.release })
        : `${version.version} (${version.release})`,
    })))

  // The builds of the archive for the chosen release, after the default: its latest build. Within the release of the
  // instance, only the newer builds.
  const buildItems = computed(() => {
    const version = versions.value.find(v => v.release === release.value)
    const builds = (version?.cachedBuilds ?? []).filter(cached =>
      version?.release !== props.instance.release || Number(cached.build) > Number(props.instance.build))
    return [
      { build: null, title: t('createInstance.latestBuild') },
      ...builds.map(cached => ({
        build: cached.build,
        title: t(cached.extracted ? 'createInstance.extractedBuild' : 'createInstance.archivedBuild', {
          build: `${version!.version}/${cached.build}`,
        }),
      })),
    ]
  })

  watch(release, () => {
    build.value = null
  })

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
      // Newest first: the newest release is offered by default.
      release.value ??= versionItems.value[0]?.release ?? null
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
      await instances.upgrade(props.instance.id, release.value, build.value ?? undefined)
    } catch (error) {
      submitError.value = error instanceof Error ? error.message : String(error)
      return
    } finally {
      submitting.value = false
    }

    open.value = false
    emit('upgraded')
  }
</script>

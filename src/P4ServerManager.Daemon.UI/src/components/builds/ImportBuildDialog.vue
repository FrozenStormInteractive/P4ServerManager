<template>
  <v-dialog v-model="open" max-width="560" :persistent="submitting">
    <v-card :title="t('importBuild.title')">
      <v-card-text>
        <p class="text-body-medium mb-4">{{ t('importBuild.text') }}</p>

        <v-btn-toggle
          v-model="mode"
          class="mb-4"
          color="primary"
          density="compact"
          :disabled="submitting"
          mandatory
          variant="outlined"
        >
          <v-btn :text="t('importBuild.files')" value="files" />
          <v-btn :text="t('importBuild.package')" value="package" />
        </v-btn-toggle>

        <v-form ref="form" :disabled="submitting" @submit.prevent="submit">
          <template v-if="mode === 'files'">
            <v-file-input
              v-model="p4d"
              :label="t('importBuild.p4d')"
              name="p4d"
              prepend-icon="mdi-file-cog-outline"
              :rules="rules.file"
            />

            <v-file-input
              v-model="p4"
              :label="t('importBuild.p4')"
              name="p4"
              prepend-icon="mdi-file-cog-outline"
              :rules="rules.file"
            />
          </template>

          <v-file-input
            v-else
            v-model="archive"
            accept=".tgz,.tar.gz,.tar"
            :hint="t('importBuild.packageHint')"
            :label="t('importBuild.packageFile')"
            name="package"
            persistent-hint
            prepend-icon="mdi-folder-zip-outline"
            :rules="rules.file"
          />
        </v-form>

        <v-alert
          v-if="submitting"
          class="mt-4"
          density="compact"
          type="info"
          variant="tonal"
        >
          {{ t('importBuild.compressing') }}
        </v-alert>

        <v-alert
          v-if="submitError"
          class="mt-4"
          density="compact"
          type="error"
          variant="tonal"
        >
          {{ t('importBuild.error', { message: submitError }) }}
        </v-alert>
      </v-card-text>

      <v-card-actions>
        <v-spacer />
        <v-btn :disabled="submitting" :text="t('actions.cancel')" @click="open = false" />

        <v-btn
          color="primary"
          :loading="submitting"
          :text="t('actions.import')"
          variant="flat"
          @click="submit"
        />
      </v-card-actions>
    </v-card>
  </v-dialog>
</template>

<script lang="ts" setup>
  // Types
  import type { VForm } from 'vuetify/components'

  // Composables
  import { ref, useTemplateRef, watch } from 'vue'
  import { useI18n } from 'vue-i18n'

  // Stores
  import { useBuildsStore } from '@/stores/builds'
  import { useNotificationsStore } from '@/stores/notifications'

  type Rule = (value: unknown) => true | string

  /**
   * Imports a p4d build into the archive, from p4d and p4, or from the server package of Perforce.
   */
  const open = defineModel<boolean>({ default: false })

  const { t } = useI18n()
  const builds = useBuildsStore()
  const notifications = useNotificationsStore()
  const form = useTemplateRef<VForm>('form')

  const mode = ref<'files' | 'package'>('files')
  const p4d = ref<File | null>(null)
  const p4 = ref<File | null>(null)
  const archive = ref<File | null>(null)
  const submitting = ref(false)
  const submitError = ref<string | null>(null)

  const rules: Record<string, Rule[]> = {
    file: [value => !!value || t('importBuild.rules.fileRequired')],
  }

  watch(open, value => {
    if (value) {
      mode.value = 'files'
      p4d.value = null
      p4.value = null
      archive.value = null
      submitError.value = null
    }
  }, { immediate: true })

  async function submit () {
    const result = await form.value?.validate()
    if (!result?.valid) {
      return
    }

    submitting.value = true
    submitError.value = null
    try {
      const build = await builds.importBuild(mode.value === 'package'
        ? { package: archive.value! }
        : { p4d: p4d.value!, p4: p4.value! })
      notifications.notify(t('builds.imported', { version: `${build.version}/${build.build}` }))
    } catch (error) {
      submitError.value = error instanceof Error ? error.message : String(error)
      return
    } finally {
      submitting.value = false
    }

    open.value = false
  }
</script>

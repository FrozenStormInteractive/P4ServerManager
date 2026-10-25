<template>
  <v-dialog v-model="open" max-width="640" scrollable>
    <v-card :title="t('importInstance.title', { name: profile?.name ?? profile?.id ?? '' })">
      <v-card-text>
        <v-form id="import-instance-form" ref="form" @submit.prevent="submit">
          <v-alert class="mb-4" density="compact" type="warning" variant="tonal">
            {{ t('importInstance.warning') }}
          </v-alert>

          <v-text-field
            class="mb-2"
            :hint="t('importInstance.rootHint')"
            :label="t('importInstance.root')"
            :model-value="profile?.rootPath"
            name="rootPath"
            persistent-hint
            readonly
          />

          <v-text-field
            v-model="fields.name"
            autofocus
            class="mb-2"
            :error-messages="serverErrors.name"
            :label="t('createInstance.name')"
            name="name"
            :rules="rules.name"
          />

          <v-textarea
            v-model="fields.description"
            auto-grow
            class="mb-2"
            :counter="MAX_DESCRIPTION_LENGTH"
            :error-messages="serverErrors.description"
            :label="t('createInstance.description')"
            name="description"
            rows="1"
            :rules="rules.description"
          />

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

          <v-row density="compact">
            <v-col cols="12" sm="8">
              <v-select
                v-model="fields.release"
                :error-messages="serverErrors.release"
                :hint="t('importInstance.versionHint')"
                item-title="title"
                item-value="release"
                :items="versionItems"
                :label="t('createInstance.version')"
                :loading="versionsLoading"
                name="release"
                :no-data-text="t('createInstance.noVersions')"
                persistent-hint
                :rules="rules.release"
              />
            </v-col>

            <v-col cols="12" sm="4">
              <v-text-field
                v-model.number="fields.port"
                :error-messages="serverErrors.port"
                :label="t('createInstance.port')"
                max="65535"
                min="1024"
                name="port"
                :rules="rules.port"
                type="number"
              />
            </v-col>
          </v-row>

          <v-switch
            v-model="fields.caseSensitive"
            class="mt-2"
            color="primary"
            :hint="t('importInstance.caseSensitiveHint')"
            :label="t('createInstance.caseSensitive')"
            name="caseSensitive"
            persistent-hint
          />

          <v-switch
            v-model="fields.autoStart"
            color="primary"
            :hint="t('createInstance.autoStartHint')"
            :label="t('createInstance.autoStart')"
            name="autoStart"
            persistent-hint
          />

          <p class="text-body-medium text-medium-emphasis mt-4">{{ t('importInstance.unicodeNote') }}</p>

          <v-alert
            v-if="submitError"
            class="mt-4"
            density="compact"
            type="error"
            variant="tonal"
          >
            {{ t('importInstance.error', { message: submitError }) }}
          </v-alert>
        </v-form>
      </v-card-text>

      <v-card-actions>
        <v-spacer />
        <v-btn :disabled="submitting" :text="t('actions.cancel')" @click="open = false" />

        <v-btn
          color="primary"
          form="import-instance-form"
          :loading="submitting"
          :text="t('actions.import')"
          type="submit"
          variant="flat"
        />
      </v-card-actions>
    </v-card>
  </v-dialog>
</template>

<script lang="ts" setup>
  // Types
  import type { ImportLegacyProfileRequest, Instance, LegacyProfile, P4dVersion } from '@/api/types'
  import type { VForm } from 'vuetify/components'

  // Composables
  import { computed, reactive, ref, useTemplateRef, watch } from 'vue'
  import { useI18n } from 'vue-i18n'
  import { useRouter } from 'vue-router'

  // Stores and utilities
  import { ApiError } from '@/api/client'
  import { importLegacyProfile } from '@/api/legacyProfiles'
  import { listVersions } from '@/api/p4d'
  import { useInstancesStore } from '@/stores/instances'

  type Rule = (value: unknown) => true | string

  const props = defineProps<{
    profile: LegacyProfile | null
  }>()

  const emit = defineEmits<{
    imported: [instance: Instance]
  }>()

  const open = defineModel<boolean>({ default: false })

  const { t } = useI18n()
  const router = useRouter()
  const instances = useInstancesStore()
  const form = useTemplateRef<VForm>('form')

  const DEFAULT_PORT = 1666
  const NAME_PATTERN = /^[A-Z][\w-]{0,31}$/i
  const MAX_DESCRIPTION_LENGTH = 512

  function createFields () {
    return {
      name: props.profile?.name ?? '',
      description: '',
      release: null as string | null,
      port: (props.profile?.port ?? DEFAULT_PORT) as number | string,
      caseSensitive: props.profile?.caseSensitive ?? true,
      autoStart: props.profile?.autoStart ?? true,
    }
  }

  const fields = reactive(createFields())
  const serverErrors = ref<Record<string, string[]>>({})
  const submitError = ref<string | null>(null)
  const submitting = ref(false)

  const versions = ref<P4dVersion[]>([])
  const versionsLoading = ref(false)
  const versionsError = ref<string | null>(null)

  const versionItems = computed(() => versions.value.map(version => ({
    release: version.release,
    title: `${version.version} (${version.release})`,
  })))

  const rules: Record<string, Rule[]> = {
    name: [
      value => !!value || t('createInstance.rules.nameRequired'),
      value => NAME_PATTERN.test(String(value)) || t('createInstance.rules.nameFormat'),
    ],
    description: [
      value => String(value ?? '').length <= MAX_DESCRIPTION_LENGTH || t('createInstance.rules.descriptionLength'),
    ],
    release: [
      value => !!value || t('createInstance.rules.versionRequired'),
    ],
    port: [
      value => {
        const port = Number(value)
        return (value !== '' && Number.isInteger(port) && port >= 1024 && port <= 65_535)
          || t('createInstance.rules.portRange')
      },
    ],
  }

  // A server error no longer applies once its field changes.
  const fieldKeys = ['name', 'description', 'release', 'port'] as const
  for (const key of fieldKeys) {
    watch(() => fields[key], () => {
      if (serverErrors.value[key]) {
        const { [key]: _, ...rest } = serverErrors.value
        serverErrors.value = rest
      }
    })
  }

  watch(open, value => {
    if (value) {
      Object.assign(fields, createFields())
      serverErrors.value = {}
      submitError.value = null
      void loadVersions()
    }
  }, { immediate: true })

  async function loadVersions () {
    versionsLoading.value = true
    versionsError.value = null
    try {
      versions.value = await listVersions()
      fields.release ??= versions.value[0]?.release ?? null
    } catch (error) {
      versionsError.value = error instanceof Error ? error.message : String(error)
    } finally {
      versionsLoading.value = false
    }
  }

  async function submit () {
    const result = await form.value?.validate()
    if (!result?.valid || !fields.release || !props.profile) {
      return
    }

    const request: ImportLegacyProfileRequest = {
      name: fields.name,
      description: fields.description.trim() || null,
      release: fields.release,
      port: Number(fields.port),
      caseSensitive: fields.caseSensitive,
      autoStart: fields.autoStart,
    }

    submitting.value = true
    submitError.value = null
    serverErrors.value = {}
    let instance: Instance
    try {
      instance = await importLegacyProfile(props.profile.id, request)
    } catch (error) {
      if (error instanceof ApiError) {
        serverErrors.value = error.fieldErrors
        const others = Object.entries(error.fieldErrors)
          .filter(([key]) => !(fieldKeys as readonly string[]).includes(key))
          .flatMap(([, messages]) => messages)
        if (others.length > 0 || Object.keys(error.fieldErrors).length === 0) {
          submitError.value = [error.message, ...others].join(' ')
        }
      } else {
        submitError.value = error instanceof Error ? error.message : String(error)
      }
      return
    } finally {
      submitting.value = false
    }

    // The instance exists from here: a failed navigation is not a failed import.
    instances.upsertInstance(instance)
    emit('imported', instance)
    open.value = false
    await router.push({ name: 'instance', params: { id: instance.id } })
  }
</script>

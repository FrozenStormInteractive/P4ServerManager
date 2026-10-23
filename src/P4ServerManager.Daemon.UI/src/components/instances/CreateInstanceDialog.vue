<template>
  <v-dialog v-model="open" max-width="640" scrollable>
    <v-card :title="t('createInstance.title')">
      <v-card-text>
        <v-form id="create-instance-form" ref="form" @submit.prevent="submit">
          <v-text-field
            v-model="fields.name"
            autofocus
            class="mb-2"
            :error-messages="serverErrors.name"
            :hint="t('createInstance.nameHint')"
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
                item-title="title"
                item-value="release"
                :items="versionItems"
                :label="t('createInstance.version')"
                :loading="versionsLoading"
                name="release"
                :no-data-text="t('createInstance.noVersions')"
                :rules="rules.release"
              >
                <template #item="{ props: itemProps, item }">
                  <v-list-item v-bind="itemProps">
                    <template v-if="item.cached" #append>
                      <v-chip color="success" label size="x-small" :text="t('createInstance.cached')" />
                    </template>
                  </v-list-item>
                </template>
              </v-select>
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

          <v-text-field
            v-model="fields.rootPath"
            class="mb-2"
            :error-messages="serverErrors.rootPath"
            :hint="t('createInstance.rootPathHint')"
            :label="t('createInstance.rootPath')"
            name="rootPath"
            persistent-hint
            :rules="rules.rootPath"
          />

          <v-row density="compact">
            <v-col cols="12" sm="6">
              <v-switch
                v-model="fields.caseSensitive"
                color="primary"
                :hint="t('createInstance.caseSensitiveHint')"
                :label="t('createInstance.caseSensitive')"
                name="caseSensitive"
                persistent-hint
              />
            </v-col>

            <v-col cols="12" sm="6">
              <v-switch
                v-model="fields.unicode"
                color="primary"
                :hint="t('createInstance.unicodeHint')"
                :label="t('createInstance.unicode')"
                name="unicode"
                persistent-hint
              />
            </v-col>
          </v-row>

          <div class="text-title-small mt-6">{{ t('createInstance.superUser') }}</div>
          <p class="text-body-medium text-medium-emphasis mb-3">{{ t('createInstance.superUserHint') }}</p>

          <v-text-field
            v-model="fields.superUserName"
            autocomplete="off"
            class="mb-2"
            :error-messages="serverErrors['superUser.name']"
            :label="t('createInstance.superUserName')"
            name="superUserName"
            :rules="rules.superUserName"
          />

          <v-text-field
            v-model="fields.password"
            autocomplete="new-password"
            class="mb-2"
            :error-messages="serverErrors['superUser.password']"
            :hint="t('createInstance.passwordHint')"
            :label="t('createInstance.password')"
            name="password"
            persistent-hint
            :rules="rules.password"
            :type="showPassword ? 'text' : 'password'"
          >
            <template #append-inner>
              <v-icon-btn
                :active="showPassword"
                :aria-label="t('createInstance.showPassword')"
                :aria-pressed="showPassword"
                :icon="showPassword ? 'mdi-eye-off' : 'mdi-eye'"
                size="small"
                variant="text"
                @click="showPassword = !showPassword"
              />
            </template>
          </v-text-field>

          <v-text-field
            v-model="fields.confirmPassword"
            autocomplete="new-password"
            :label="t('createInstance.confirmPassword')"
            name="confirmPassword"
            :rules="rules.confirmPassword"
            :type="showPassword ? 'text' : 'password'"
          />

          <v-alert
            v-if="submitError"
            class="mt-4"
            density="compact"
            type="error"
            variant="tonal"
          >
            {{ t('createInstance.error', { message: submitError }) }}
          </v-alert>
        </v-form>
      </v-card-text>

      <v-card-actions>
        <v-spacer />
        <v-btn :disabled="submitting" :text="t('actions.cancel')" @click="open = false" />

        <v-btn
          color="primary"
          form="create-instance-form"
          :loading="submitting"
          :text="t('actions.create')"
          type="submit"
          variant="flat"
        />
      </v-card-actions>
    </v-card>
  </v-dialog>
</template>

<script lang="ts" setup>
  // Types
  import type { CreateInstanceRequest, Instance, P4dVersion } from '@/api/types'
  import type { VForm } from 'vuetify/components'

  // Composables
  import { computed, reactive, ref, useTemplateRef, watch } from 'vue'
  import { useI18n } from 'vue-i18n'
  import { useRouter } from 'vue-router'

  // Stores and utilities
  import { ApiError } from '@/api/client'
  import { listVersions } from '@/api/p4d'
  import { useInstancesStore } from '@/stores/instances'

  type Rule = (value: unknown) => true | string

  const open = defineModel<boolean>({ default: false })

  const { t } = useI18n()
  const router = useRouter()
  const instances = useInstancesStore()
  const form = useTemplateRef<VForm>('form')

  const DEFAULT_PORT = 1666
  const NAME_PATTERN = /^[A-Z][\w-]{0,31}$/i
  const USER_NAME_PATTERN = /^[A-Z][\w.-]{0,63}$/i
  const MIN_PASSWORD_LENGTH = 8
  const MAX_DESCRIPTION_LENGTH = 512

  function createFields () {
    return {
      name: '',
      description: '',
      release: null as string | null,
      port: DEFAULT_PORT as number | string,
      rootPath: '',
      caseSensitive: true,
      unicode: false,
      superUserName: 'admin',
      password: '',
      confirmPassword: '',
    }
  }

  const fields = reactive(createFields())
  const serverErrors = ref<Record<string, string[]>>({})
  const submitError = ref<string | null>(null)
  const submitting = ref(false)
  const showPassword = ref(false)

  const versions = ref<P4dVersion[]>([])
  const versionsLoading = ref(false)
  const versionsError = ref<string | null>(null)

  const versionItems = computed(() => versions.value.map(version => ({
    release: version.release,
    title: `${version.version} (${version.release})`,
    cached: version.cachedBuilds.length > 0,
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
    rootPath: [
      value => !value || isAbsolutePath(String(value)) || t('createInstance.rules.rootPathAbsolute'),
    ],
    superUserName: [
      value => !!value || t('createInstance.rules.userNameRequired'),
      value => USER_NAME_PATTERN.test(String(value)) || t('createInstance.rules.userNameFormat'),
    ],
    password: [
      value => String(value ?? '').length >= MIN_PASSWORD_LENGTH || t('createInstance.rules.passwordLength'),
    ],
    confirmPassword: [
      value => value === fields.password || t('createInstance.rules.passwordMatch'),
    ],
  }

  // A server error no longer applies once its field changes.
  const fieldKeys: Record<string, keyof ReturnType<typeof createFields>> = {
    'name': 'name',
    'description': 'description',
    'release': 'release',
    'port': 'port',
    'rootPath': 'rootPath',
    'superUser.name': 'superUserName',
    'superUser.password': 'password',
  }
  for (const [errorKey, field] of Object.entries(fieldKeys)) {
    watch(() => fields[field], () => {
      if (serverErrors.value[errorKey]) {
        const { [errorKey]: _, ...rest } = serverErrors.value
        serverErrors.value = rest
      }
    })
  }

  watch(open, value => {
    if (value) {
      Object.assign(fields, createFields())
      serverErrors.value = {}
      submitError.value = null
      showPassword.value = false
      void loadVersions()
    }
  }, { immediate: true })

  function isAbsolutePath (path: string) {
    // The daemon runs on Linux; Windows paths are accepted for development.
    return path.startsWith('/') || /^[A-Z]:[\\/]/i.test(path)
  }

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
    if (!result?.valid || !fields.release) {
      return
    }

    const request: CreateInstanceRequest = {
      name: fields.name,
      description: fields.description.trim() || null,
      release: fields.release,
      port: Number(fields.port),
      caseSensitive: fields.caseSensitive,
      unicode: fields.unicode,
      rootPath: fields.rootPath.trim() || null,
      superUser: { name: fields.superUserName, password: fields.password },
    }

    submitting.value = true
    submitError.value = null
    serverErrors.value = {}
    let instance: Instance
    try {
      instance = await instances.create(request)
    } catch (error) {
      if (error instanceof ApiError) {
        const known = Object.keys(fieldKeys)
        serverErrors.value = error.fieldErrors
        const others = Object.entries(error.fieldErrors)
          .filter(([key]) => !known.includes(key))
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

    // The instance exists from here: a failed navigation is not a failed creation.
    open.value = false
    await router.push({ name: 'instance', params: { id: instance.id } })
  }
</script>

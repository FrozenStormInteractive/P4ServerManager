<template>
  <v-dialog v-model="open" max-width="640" scrollable>
    <v-card v-if="source" :title="t('restores.dialog.title')">
      <v-card-text>
        <p class="mb-4" data-testid="restore-source">
          {{ t('restores.dialog.source', {
            kind: t(`backups.kinds.${source.kind}`),
            name: source.sourceName,
            date: d(new Date(source.createdAt), 'long'),
            destination: source.destinationName,
          }) }}
        </p>

        <v-radio-group
          v-if="instance"
          v-model="mode"
          class="mb-2"
          hide-details
          name="mode"
        >
          <v-radio :label="t('restores.dialog.replace', { name: instance.name })" value="replace" />
          <v-radio :label="t('restores.dialog.new')" value="new" />
        </v-radio-group>

        <v-form id="restore-form" ref="form" @submit.prevent="submit">
          <template v-if="mode === 'replace' && instance">
            <v-alert class="my-4" density="compact" type="warning" variant="tonal">
              {{ t('restores.dialog.replaceWarning', { name: instance.name }) }}
            </v-alert>

            <v-text-field
              v-model="confirmation"
              autocomplete="off"
              :label="t('restores.dialog.confirm', { name: instance.name })"
              name="confirmation"
              :rules="[value => value === instance?.name || t('restores.dialog.confirmMismatch')]"
            />
          </template>

          <template v-else>
            <p class="text-body-medium text-medium-emphasis my-4">{{ t('restores.dialog.newHint') }}</p>

            <v-row class="mb-2" density="compact">
              <v-col cols="12" sm="8">
                <v-text-field
                  v-model="fields.name"
                  :error-messages="serverErrors.name"
                  :hint="t('createInstance.nameHint')"
                  :label="t('createInstance.name')"
                  name="name"
                  :rules="rules.name"
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

            <ServerDirectoryField
              v-model="fields.serverDirectory"
              class="mb-2"
              :error-messages="serverErrors.serverDirectory"
              :hint="t('createInstance.serverDirectoryHint')"
              :label="t('createInstance.serverDirectory')"
              name="serverDirectory"
              persistent-hint
              :rules="rules.serverDirectory"
            />

            <ServerDirectoryCaseWarning :name="fields.name" :server-directory="fields.serverDirectory" />

            <v-row density="compact">
              <v-col cols="12" sm="6">
                <v-switch
                  v-model="fields.ssl"
                  color="primary"
                  :hint="t('restores.dialog.sslHint')"
                  :label="t('createInstance.ssl')"
                  name="ssl"
                  persistent-hint
                />
              </v-col>

              <v-col cols="12" sm="6">
                <v-switch
                  v-model="fields.includeLicense"
                  color="primary"
                  :hint="t('restores.dialog.licenseHint')"
                  :label="t('restores.dialog.license')"
                  name="includeLicense"
                  persistent-hint
                />
              </v-col>
            </v-row>

            <div class="text-title-small mt-6">{{ t('createInstance.superUser') }}</div>
            <p class="text-body-medium text-medium-emphasis mb-3">{{ t('restores.dialog.superUserHint') }}</p>

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
              autocomplete="current-password"
              :error-messages="serverErrors['superUser.password']"
              :label="t('createInstance.password')"
              name="password"
              :rules="rules.password"
              type="password"
            />
          </template>

          <v-alert
            v-if="submitError"
            class="mt-4"
            data-testid="restore-error"
            density="compact"
            type="error"
            variant="tonal"
          >
            {{ submitError }}
          </v-alert>
        </v-form>
      </v-card-text>

      <v-card-actions>
        <v-spacer />
        <v-btn :disabled="submitting" :text="t('actions.cancel')" @click="open = false" />

        <v-btn
          :color="mode === 'replace' ? 'error' : 'primary'"
          form="restore-form"
          :loading="submitting"
          :text="t('restores.dialog.submit')"
          type="submit"
          variant="flat"
        />
      </v-card-actions>
    </v-card>
  </v-dialog>
</template>

<script lang="ts" setup>
  // Types
  import type { Instance, RestoreInstanceRequest } from '@/api/types'
  import type { RestoreSource } from '@/utils/restore'
  import type { VForm } from 'vuetify/components'

  // Composables
  import { reactive, ref, useTemplateRef, watch } from 'vue'
  import { useI18n } from 'vue-i18n'
  import { useRouter } from 'vue-router'

  // Components, stores and utilities
  import { ApiError } from '@/api/client'
  import ServerDirectoryCaseWarning from '@/components/filesystem/ServerDirectoryCaseWarning.vue'
  import ServerDirectoryField from '@/components/filesystem/ServerDirectoryField.vue'
  import { useBackupsStore } from '@/stores/backups'
  import { useNotificationsStore } from '@/stores/notifications'

  type Rule = (value: unknown) => true | string

  /**
   * Restores a backup point: over the data of an instance, once its name is typed again, or into a new instance with
   * the super user of the restored server. Both run in the daemon; a new instance is opened once created.
   */
  const props = defineProps<{
    source: RestoreSource | null
    /**
     * The instance whose data the point may replace. Without it, the point can only become a new instance.
     */
    instance?: Instance | null
  }>()

  const open = defineModel<boolean>({ default: false })

  /**
   * The restore started: the dialog that opened this one has nothing left to show, and closes too.
   */
  const emit = defineEmits<{
    started: []
  }>()

  const { t, d } = useI18n()
  const router = useRouter()
  const backups = useBackupsStore()
  const notifications = useNotificationsStore()
  const form = useTemplateRef<VForm>('form')

  const DEFAULT_PORT = 1666
  const NAME_PATTERN = /^[A-Z][\w-]{0,31}$/i
  const USER_NAME_PATTERN = /^[A-Z][\w.-]{0,63}$/i

  function createFields () {
    return {
      name: '',
      port: DEFAULT_PORT as number | string,
      serverDirectory: '',
      ssl: false,
      includeLicense: true,
      superUserName: 'admin',
      password: '',
    }
  }

  const mode = ref<'replace' | 'new'>('new')
  const confirmation = ref('')
  const fields = reactive(createFields())
  const serverErrors = ref<Record<string, string[]>>({})
  const submitError = ref<string | null>(null)
  const submitting = ref(false)

  const rules: Record<string, Rule[]> = {
    name: [
      value => !!value || t('createInstance.rules.nameRequired'),
      value => NAME_PATTERN.test(String(value)) || t('createInstance.rules.nameFormat'),
    ],
    port: [
      value => {
        const port = Number(value)
        return (value !== '' && Number.isInteger(port) && port >= 1024 && port <= 65_535)
          || t('createInstance.rules.portRange')
      },
    ],
    serverDirectory: [
      value => !String(value ?? '').trim() || isAbsolutePath(String(value).trim())
        || t('createInstance.rules.serverDirectoryAbsolute'),
    ],
    superUserName: [
      value => !!value || t('createInstance.rules.userNameRequired'),
      value => USER_NAME_PATTERN.test(String(value)) || t('createInstance.rules.userNameFormat'),
    ],
    password: [
      value => !!value || t('restores.dialog.passwordRequired'),
    ],
  }

  watch(open, value => {
    if (value) {
      Object.assign(fields, createFields())
      fields.name = props.source ? suggestName(props.source.sourceName) : ''
      mode.value = props.instance ? 'replace' : 'new'
      confirmation.value = ''
      serverErrors.value = {}
      submitError.value = null
    }
  }, { immediate: true })

  // A name for the copy: the source name with a suffix, within the 32 characters of a name.
  function suggestName (sourceName: string) {
    const suffix = '-restored'
    return `${sourceName.slice(0, 32 - suffix.length)}${suffix}`
  }

  function isAbsolutePath (path: string) {
    // The daemon runs on Linux; Windows paths are accepted for development.
    return path.startsWith('/') || /^[A-Z]:[\\/]/i.test(path)
  }

  async function submit () {
    const source = props.source
    const result = await form.value?.validate()
    if (!source || !result?.valid) {
      return
    }

    submitting.value = true
    submitError.value = null
    serverErrors.value = {}
    try {
      if (mode.value === 'replace' && props.instance) {
        await backups.startRestore(props.instance.id, source.destinationId, source.snapshotKey)
        notifications.notify(t('restores.dialog.started', { name: props.instance.name }), 'info')
        open.value = false
        emit('started')
        return
      }

      const request: RestoreInstanceRequest = {
        name: fields.name,
        description: null,
        port: Number(fields.port),
        release: null,
        build: null,
        serverDirectory: fields.serverDirectory.trim() || null,
        ssl: fields.ssl,
        autoStart: true,
        includeLicense: fields.includeLicense,
        superUser: { name: fields.superUserName, password: fields.password },
        source: { destinationId: source.destinationId, snapshotKey: source.snapshotKey },
      }
      const instance = await backups.restoreInstance(request)
      open.value = false
      emit('started')
      await router.push({ name: 'instance', params: { id: instance.id } })
    } catch (error) {
      if (error instanceof ApiError && Object.keys(error.fieldErrors).length > 0) {
        serverErrors.value = error.fieldErrors
        submitError.value = error.message
      } else {
        submitError.value = error instanceof Error ? error.message : String(error)
      }
    } finally {
      submitting.value = false
    }
  }
</script>

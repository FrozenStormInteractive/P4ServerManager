<template>
  <v-dialog v-model="open" max-width="640" scrollable>
    <v-card :title="destination ? t('backupDestinations.dialog.editTitle', { name: destination.name }) : t('backupDestinations.dialog.createTitle')">
      <v-card-text>
        <v-form id="backup-destination-form" ref="form" @submit.prevent="submit">
          <v-text-field
            v-model="name"
            autofocus
            :error-messages="fieldErrors.name"
            :label="t('backupDestinations.dialog.name')"
            name="name"
            :rules="[required]"
          />

          <v-select
            v-model="kind"
            :disabled="!!destination"
            :items="kindItems"
            :label="t('backupDestinations.dialog.kind')"
            name="kind"
          />

          <template v-if="kind === 'Local'">
            <v-text-field
              v-model="local.path"
              :error-messages="fieldErrors['local.path']"
              :hint="t('backupDestinations.dialog.pathHint')"
              :label="t('backupDestinations.dialog.path')"
              name="local.path"
              persistent-hint
              :rules="[required, absolutePath]"
            />
          </template>

          <template v-else-if="kind === 'S3' || kind === 'S3Compatible'">
            <v-text-field
              v-if="kind === 'S3Compatible'"
              v-model="s3.endpoint"
              :error-messages="fieldErrors['s3.endpoint']"
              :hint="t('backupDestinations.dialog.endpointHint')"
              :label="t('backupDestinations.dialog.endpoint')"
              name="s3.endpoint"
              persistent-hint
              :rules="[required]"
            />

            <v-text-field
              v-model="s3.bucket"
              :label="t('backupDestinations.dialog.bucket')"
              name="s3.bucket"
              :rules="[required]"
            />

            <v-text-field
              v-model="s3.prefix"
              :hint="t('backupDestinations.dialog.prefixHint')"
              :label="t('backupDestinations.dialog.prefix')"
              name="s3.prefix"
            />

            <v-text-field
              v-model="s3.region"
              :label="t('backupDestinations.dialog.region')"
              name="s3.region"
              :rules="[required]"
            />

            <v-checkbox
              v-if="kind === 'S3Compatible'"
              v-model="s3.forcePathStyle"
              density="compact"
              :label="t('backupDestinations.dialog.forcePathStyle')"
            />

            <v-text-field
              v-model="s3.accessKeyId"
              :label="t('backupDestinations.dialog.accessKeyId')"
              name="s3.accessKeyId"
              :rules="[required]"
            />

            <v-text-field
              v-if="askSecret"
              v-model="secrets.secretAccessKey"
              autocomplete="new-password"
              :label="t('backupDestinations.dialog.secretAccessKey')"
              name="credentials.secretAccessKey"
              :rules="[required]"
              type="password"
            />

            <v-text-field
              v-model="s3.storageClass"
              :hint="t('backupDestinations.dialog.storageClassHint')"
              :label="t('backupDestinations.dialog.storageClass')"
              name="s3.storageClass"
            />
          </template>

          <template v-else-if="kind === 'GoogleCloudStorage'">
            <v-text-field
              v-model="gcs.bucket"
              :label="t('backupDestinations.dialog.bucket')"
              name="gcs.bucket"
              :rules="[required]"
            />

            <v-text-field
              v-model="gcs.prefix"
              :hint="t('backupDestinations.dialog.prefixHint')"
              :label="t('backupDestinations.dialog.prefix')"
              name="gcs.prefix"
            />

            <v-textarea
              v-if="askSecret"
              v-model="secrets.serviceAccountJson"
              :hint="t('backupDestinations.dialog.serviceAccountJsonHint')"
              :label="t('backupDestinations.dialog.serviceAccountJson')"
              name="credentials.serviceAccountJson"
              persistent-hint
              rows="4"
              :rules="[required]"
            />
          </template>

          <template v-else-if="kind === 'AzureBlob'">
            <v-text-field
              v-model="azure.accountUrl"
              :error-messages="fieldErrors['azure.accountUrl']"
              :hint="t('backupDestinations.dialog.accountUrlHint')"
              :label="t('backupDestinations.dialog.accountUrl')"
              name="azure.accountUrl"
              persistent-hint
              :rules="[required]"
            />

            <v-text-field
              v-model="azure.container"
              :label="t('backupDestinations.dialog.container')"
              name="azure.container"
              :rules="[required]"
            />

            <v-text-field
              v-model="azure.prefix"
              :hint="t('backupDestinations.dialog.prefixHint')"
              :label="t('backupDestinations.dialog.prefix')"
              name="azure.prefix"
            />

            <template v-if="askSecret">
              <v-radio-group v-model="azureAuth" inline :label="t('backupDestinations.dialog.azureAuth')">
                <v-radio :label="t('backupDestinations.dialog.accountKey')" value="key" />
                <v-radio :label="t('backupDestinations.dialog.sasToken')" value="sas" />
              </v-radio-group>

              <v-text-field
                v-model="azureSecret"
                autocomplete="new-password"
                :label="t(azureAuth === 'key' ? 'backupDestinations.dialog.accountKey' : 'backupDestinations.dialog.sasToken')"
                name="credentials.azure"
                :rules="[required]"
                type="password"
              />
            </template>
          </template>

          <template v-else-if="kind === 'Ftp'">
            <v-text-field
              v-model="ftp.host"
              :label="t('backupDestinations.dialog.host')"
              name="ftp.host"
              :rules="[required]"
            />

            <v-select
              v-model="ftp.encryption"
              :items="encryptionItems"
              :label="t('backupDestinations.dialog.encryption')"
              name="ftp.encryption"
            />

            <v-alert
              v-if="ftp.encryption === 'None'"
              class="mb-4"
              density="compact"
              type="warning"
              variant="tonal"
            >
              {{ t('backupDestinations.dialog.insecureFtp') }}
            </v-alert>

            <v-text-field
              v-model.number="ftp.port"
              :hint="t('backupDestinations.dialog.portHint')"
              :label="t('backupDestinations.dialog.port')"
              name="ftp.port"
              type="number"
            />

            <v-text-field
              v-model="ftp.path"
              :label="t('backupDestinations.dialog.ftpPath')"
              name="ftp.path"
              :rules="[required]"
            />

            <v-text-field
              v-model="ftp.userName"
              :label="t('backupDestinations.dialog.userName')"
              name="ftp.userName"
              :rules="[required]"
            />

            <v-text-field
              v-if="askSecret"
              v-model="secrets.password"
              autocomplete="new-password"
              :label="t('backupDestinations.dialog.password')"
              name="credentials.password"
              :rules="[required]"
              type="password"
            />

            <v-text-field
              v-if="ftp.encryption !== 'None'"
              v-model="ftp.certificateFingerprint"
              :hint="t('backupDestinations.dialog.certificateFingerprintHint')"
              :label="t('backupDestinations.dialog.certificateFingerprint')"
              name="ftp.certificateFingerprint"
            />
          </template>

          <v-checkbox
            v-if="destination && needsSecret"
            v-model="changeSecret"
            density="compact"
            :label="t('backupDestinations.dialog.changeSecret')"
          />

          <template v-if="!destination">
            <v-divider class="my-4" />

            <v-text-field
              v-model="passphrase"
              autocomplete="new-password"
              :error-messages="fieldErrors.passphrase"
              :hint="t('backupDestinations.dialog.passphraseHint')"
              :label="t('backupDestinations.dialog.passphrase')"
              name="passphrase"
              persistent-hint
              :rules="[required, longEnough]"
              type="password"
            />

            <v-text-field
              v-model="passphraseConfirm"
              autocomplete="new-password"
              :label="t('backupDestinations.dialog.passphraseConfirm')"
              name="passphraseConfirm"
              :rules="[matchesPassphrase]"
              type="password"
            />

            <v-alert density="compact" type="info" variant="tonal">
              {{ t('backupDestinations.dialog.passphraseWarning') }}
            </v-alert>
          </template>
        </v-form>

        <v-alert
          v-if="submitError"
          class="mt-4"
          data-testid="destination-error"
          density="compact"
          type="error"
          variant="tonal"
        >
          {{ submitError }}
        </v-alert>
      </v-card-text>

      <v-card-actions>
        <v-spacer />
        <v-btn :disabled="submitting" :text="t('actions.cancel')" @click="open = false" />

        <v-btn
          color="primary"
          form="backup-destination-form"
          :loading="submitting"
          :text="destination ? t('actions.save') : t('actions.create')"
          type="submit"
          variant="flat"
        />
      </v-card-actions>
    </v-card>
  </v-dialog>
</template>

<script lang="ts" setup>
  // Types
  import type {
    AzureBlobDestinationSettings,
    BackupDestination,
    BackupDestinationCredentials,
    BackupDestinationKind,
    BackupDestinationSettings,
    FtpDestinationSettings,
    GcsDestinationSettings,
    S3DestinationSettings,
  } from '@/api/types'
  import type { VForm } from 'vuetify/components'

  // Composables
  import { computed, reactive, ref, useTemplateRef, watch } from 'vue'
  import { useI18n } from 'vue-i18n'

  // Stores
  import { ApiError } from '@/api/client'
  import { useBackupsStore } from '@/stores/backups'
  import { useNotificationsStore } from '@/stores/notifications'

  /**
   * Adds a backup destination, or edits one. A new destination asks for the passphrase of its repository: the daemon
   * creates the repository, or attaches the one already there when the passphrase opens it. Secrets are never shown
   * back: editing keeps them unless asked to change them.
   */
  const props = defineProps<{
    destination?: BackupDestination | null
  }>()

  const open = defineModel<boolean>({ default: false })

  const { t } = useI18n()
  const backups = useBackupsStore()
  const notifications = useNotificationsStore()
  const form = useTemplateRef<VForm>('form')

  const MIN_PASSPHRASE = 12
  const kinds: BackupDestinationKind[] = ['Local', 'S3', 'S3Compatible', 'GoogleCloudStorage', 'AzureBlob', 'Ftp']

  const name = ref('')
  const kind = ref<BackupDestinationKind>('Local')
  const local = reactive({ path: '' })
  const s3 = reactive<S3DestinationSettings>(emptyS3())
  const gcs = reactive<GcsDestinationSettings>({ bucket: '', prefix: null })
  const azure = reactive<AzureBlobDestinationSettings>({ accountUrl: '', container: '', prefix: null })
  const ftp = reactive<FtpDestinationSettings>(emptyFtp())
  const secrets = reactive<BackupDestinationCredentials>({})
  const azureAuth = ref<'key' | 'sas'>('key')
  const azureSecret = ref('')
  const changeSecret = ref(false)
  const passphrase = ref('')
  const passphraseConfirm = ref('')
  const fieldErrors = ref<Record<string, string[]>>({})
  const submitError = ref<string | null>(null)
  const submitting = ref(false)

  const kindItems = computed(() => kinds.map(value => ({ value, title: t(`backupDestinations.kinds.${value}`) })))
  const encryptionItems = computed(() => (['Explicit', 'Implicit', 'None'] as const).map(value => ({
    value,
    title: t(`backupDestinations.dialog.encryptions.${value}`),
  })))

  const needsSecret = computed(() => kind.value !== 'Local')
  const askSecret = computed(() => needsSecret.value && (!props.destination || changeSecret.value))

  function required (value: unknown): true | string {
    return (typeof value === 'string' ? value.trim().length > 0 : value !== null && value !== undefined)
      || t('backupDestinations.dialog.required')
  }

  function absolutePath (value: string): true | string {
    // A Unix path, a Windows path with its drive, or a UNC path.
    return /^(?:\/|[a-z]:[\\/]|\\\\)/i.test(value) || t('backupDestinations.dialog.absolutePath')
  }

  function longEnough (value: string): true | string {
    return value.length >= MIN_PASSPHRASE || t('backupDestinations.dialog.passphraseTooShort')
  }

  function matchesPassphrase (value: string): true | string {
    return value === passphrase.value || t('backupDestinations.dialog.passphraseMismatch')
  }

  function emptyS3 (): S3DestinationSettings {
    return {
      bucket: '',
      prefix: null,
      region: '',
      endpoint: null,
      forcePathStyle: true,
      accessKeyId: '',
      storageClass: null,
    }
  }

  function emptyFtp (): FtpDestinationSettings {
    return { host: '', port: null, encryption: 'Explicit', path: '', userName: '', certificateFingerprint: null }
  }

  watch(open, value => {
    if (!value) {
      return
    }
    const current = props.destination
    name.value = current?.name ?? ''
    kind.value = current?.kind ?? 'Local'
    local.path = current?.local?.path ?? ''
    Object.assign(s3, current?.s3 ?? emptyS3())
    Object.assign(gcs, current?.gcs ?? { bucket: '', prefix: null })
    Object.assign(azure, current?.azure ?? { accountUrl: '', container: '', prefix: null })
    Object.assign(ftp, current?.ftp ?? emptyFtp())
    for (const key of Object.keys(secrets) as (keyof BackupDestinationCredentials)[]) {
      delete secrets[key]
    }
    azureAuth.value = 'key'
    azureSecret.value = ''
    changeSecret.value = false
    passphrase.value = ''
    passphraseConfirm.value = ''
    fieldErrors.value = {}
    submitError.value = null
  }, { immediate: true })

  /**
   * Empty optional texts go to the daemon as null.
   */
  function orNull (value: string | null): string | null {
    return value && value.trim().length > 0 ? value.trim() : null
  }

  function buildSettings (): BackupDestinationSettings {
    switch (kind.value) {
      case 'Local': {
        return { local: { path: local.path.trim() } }
      }
      case 'S3':
      case 'S3Compatible': {
        return {
          s3: {
            ...s3,
            prefix: orNull(s3.prefix),
            endpoint: kind.value === 'S3Compatible' ? orNull(s3.endpoint) : null,
            forcePathStyle: kind.value === 'S3Compatible' && s3.forcePathStyle,
            storageClass: orNull(s3.storageClass),
          },
        }
      }
      case 'GoogleCloudStorage': {
        return { gcs: { ...gcs, prefix: orNull(gcs.prefix) } }
      }
      case 'AzureBlob': {
        return { azure: { ...azure, prefix: orNull(azure.prefix) } }
      }
      case 'Ftp': {
        const port = ftp.port as number | string | null
        return {
          ftp: {
            ...ftp,
            port: port === '' || port === null ? null : Number(port),
            certificateFingerprint: ftp.encryption === 'None' ? null : orNull(ftp.certificateFingerprint),
          },
        }
      }
    }
  }

  function buildCredentials (): BackupDestinationCredentials | null {
    if (!askSecret.value) {
      return null
    }
    switch (kind.value) {
      case 'S3':
      case 'S3Compatible': {
        return { secretAccessKey: secrets.secretAccessKey ?? null }
      }
      case 'GoogleCloudStorage': {
        return { serviceAccountJson: secrets.serviceAccountJson ?? null }
      }
      case 'AzureBlob': {
        return azureAuth.value === 'key' ? { accountKey: azureSecret.value } : { sasToken: azureSecret.value }
      }
      case 'Ftp': {
        return { password: secrets.password ?? null }
      }
      default: {
        return null
      }
    }
  }

  async function submit () {
    const result = await form.value?.validate()
    if (!result?.valid) {
      return
    }

    submitting.value = true
    submitError.value = null
    fieldErrors.value = {}
    try {
      const settings = buildSettings()
      const credentials = buildCredentials()
      if (props.destination) {
        const saved = await backups.updateDestination(props.destination.id, {
          name: name.value.trim(),
          ...settings,
          credentials,
        })
        notifications.notify(t('backupDestinations.dialog.saved', { name: saved.name }))
      } else {
        const created = await backups.createDestination({
          name: name.value.trim(),
          kind: kind.value,
          ...settings,
          credentials,
          passphrase: passphrase.value,
        })
        notifications.notify(t('backupDestinations.dialog.created', { name: created.name }))
      }
    } catch (error) {
      if (error instanceof ApiError && Object.keys(error.fieldErrors).length > 0) {
        fieldErrors.value = error.fieldErrors
        submitError.value = Object.values(error.fieldErrors).flat().join(' ')
      } else {
        submitError.value = error instanceof Error ? error.message : String(error)
      }
      return
    } finally {
      submitting.value = false
    }

    open.value = false
  }
</script>

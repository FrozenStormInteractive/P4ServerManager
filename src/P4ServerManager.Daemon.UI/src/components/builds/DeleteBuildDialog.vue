<template>
  <v-dialog v-model="open" max-width="480">
    <v-card v-if="build" :title="t('deleteBuild.title', { version: version })">
      <v-card-text>
        <p class="text-body-medium">
          {{ t(build.source === 'Imported' ? 'deleteBuild.textImported' : 'deleteBuild.textFtp') }}
        </p>

        <v-alert
          v-if="submitError"
          class="mt-4"
          density="compact"
          type="error"
          variant="tonal"
        >
          {{ t('deleteBuild.error', { message: submitError }) }}
        </v-alert>
      </v-card-text>

      <v-card-actions>
        <v-spacer />
        <v-btn :disabled="submitting" :text="t('actions.cancel')" @click="open = false" />

        <v-btn
          color="error"
          :loading="submitting"
          :text="t('actions.delete')"
          variant="flat"
          @click="submit"
        />
      </v-card-actions>
    </v-card>
  </v-dialog>
</template>

<script lang="ts" setup>
  // Types
  import type { Build } from '@/api/types'

  // Composables
  import { computed, ref, watch } from 'vue'
  import { useI18n } from 'vue-i18n'

  // Stores
  import { useBuildsStore } from '@/stores/builds'
  import { useNotificationsStore } from '@/stores/notifications'

  /**
   * Deletes a build from the archive, with its extracted files.
   */
  const props = defineProps<{
    build: Build | null
  }>()

  const open = defineModel<boolean>({ default: false })

  const { t } = useI18n()
  const builds = useBuildsStore()
  const notifications = useNotificationsStore()

  const submitting = ref(false)
  const submitError = ref<string | null>(null)

  const version = computed(() => props.build ? `${props.build.version}/${props.build.build}` : '')

  watch(open, value => {
    if (value) {
      submitError.value = null
    }
  }, { immediate: true })

  async function submit () {
    if (!props.build) {
      return
    }

    submitting.value = true
    submitError.value = null
    try {
      await builds.remove(props.build.id)
      notifications.notify(t('builds.deleted', { version: version.value }))
    } catch (error) {
      submitError.value = error instanceof Error ? error.message : String(error)
      return
    } finally {
      submitting.value = false
    }

    open.value = false
  }
</script>

<template>
  <v-dialog v-model="open" max-width="560">
    <v-card :title="t('deleteInstance.title', { name: instance.name })">
      <v-card-text>
        <p class="text-body-medium mb-4">{{ t('deleteInstance.text') }}</p>

        <!-- Nothing is chosen at first: the user decides what happens to the files. -->
        <v-radio-group
          v-model="deleteFiles"
          :disabled="submitting"
          hide-details
          :label="t('deleteInstance.filesChoice')"
          name="deleteFiles"
        >
          <v-radio :label="t('deleteInstance.keepFiles')" :value="false" />
          <v-radio color="error" :label="t('deleteInstance.deleteFiles')" :value="true" />
        </v-radio-group>

        <p v-if="deleteFiles === true" class="text-body-medium text-medium-emphasis mt-4">
          {{ t('deleteInstance.deleteFilesHint') }}
        </p>

        <p v-else-if="deleteFiles === false" class="text-body-medium text-medium-emphasis mt-4">
          {{ t('deleteInstance.keptFiles', { path: instance.rootPath }) }}
        </p>

        <!-- A deletion cannot be undone: the name is typed, not just clicked through, whatever the choice. -->
        <v-text-field
          v-model="confirmation"
          autocomplete="off"
          class="mt-4"
          :disabled="submitting"
          :label="t('deleteInstance.confirmName', { name: instance.name })"
          name="confirmation"
        />

        <v-alert
          v-if="submitError"
          class="mt-4"
          density="compact"
          type="error"
          variant="tonal"
        >
          {{ t('deleteInstance.error', { message: submitError }) }}
        </v-alert>
      </v-card-text>

      <v-card-actions>
        <v-spacer />
        <v-btn :disabled="submitting" :text="t('actions.cancel')" @click="open = false" />

        <v-btn
          color="error"
          :disabled="!canSubmit"
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
  import type { Instance } from '@/api/types'

  // Composables
  import { computed, ref, watch } from 'vue'
  import { useI18n } from 'vue-i18n'

  // Stores
  import { useInstancesStore } from '@/stores/instances'

  const props = defineProps<{
    instance: Instance
  }>()

  const emit = defineEmits<{
    deleted: []
  }>()

  const open = defineModel<boolean>({ default: false })

  const { t } = useI18n()
  const instances = useInstancesStore()

  // Null until the user has chosen.
  const deleteFiles = ref<boolean | null>(null)
  const confirmation = ref('')
  const submitting = ref(false)
  const submitError = ref<string | null>(null)

  const canSubmit = computed(() => deleteFiles.value !== null && confirmation.value === props.instance.name)

  watch(open, value => {
    if (value) {
      deleteFiles.value = null
      confirmation.value = ''
      submitError.value = null
    }
  }, { immediate: true })

  async function submit () {
    if (deleteFiles.value === null) {
      return
    }

    submitting.value = true
    submitError.value = null
    try {
      await instances.remove(props.instance.id, deleteFiles.value)
    } catch (error) {
      submitError.value = error instanceof Error ? error.message : String(error)
      return
    } finally {
      submitting.value = false
    }

    open.value = false
    emit('deleted')
  }
</script>

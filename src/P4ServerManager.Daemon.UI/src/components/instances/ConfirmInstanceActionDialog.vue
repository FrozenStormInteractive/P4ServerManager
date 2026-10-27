<template>
  <v-dialog v-model="open" max-width="480">
    <v-card :title="title">
      <v-card-text class="text-body-medium">
        <p>{{ text }}</p>

        <!-- With one server, its name is in the title. -->
        <ul v-if="names && names.length > 1" class="mt-4 ml-6">
          <li v-for="name in names" :key="name">{{ name }}</li>
        </ul>
      </v-card-text>

      <v-card-actions>
        <v-spacer />
        <v-btn :text="t('actions.cancel')" @click="open = false" />

        <v-btn
          color="primary"
          :text="t(`actions.${action}`)"
          variant="flat"
          @click="confirm"
        />
      </v-card-actions>
    </v-card>
  </v-dialog>
</template>

<script lang="ts" setup>
  // Composables
  import { computed } from 'vue'
  import { useI18n } from 'vue-i18n'

  /**
   * Confirms a stop or a restart: it disconnects the users of the servers. A start is never confirmed.
   */
  const props = defineProps<{
    action: 'stop' | 'restart'
    /**
     * The servers the action applies to, or null for all of them.
     */
    names: string[] | null
  }>()

  const emit = defineEmits<{
    confirm: []
  }>()

  const open = defineModel<boolean>({ default: false })

  const { t } = useI18n()

  const title = computed(() => {
    if (props.names === null) {
      return t(`instances.confirmAll.${props.action}.title`)
    }
    const count = props.names.length
    return t(`instances.confirmAction.${props.action}.title`, { count, name: props.names[0] }, count)
  })

  const text = computed(() => props.names === null
    ? t(`instances.confirmAll.${props.action}.text`)
    : t(`instances.confirmAction.${props.action}.text`, props.names.length))

  function confirm () {
    open.value = false
    emit('confirm')
  }
</script>

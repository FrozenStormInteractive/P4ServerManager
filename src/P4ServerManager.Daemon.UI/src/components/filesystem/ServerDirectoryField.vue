<template>
  <!-- The path can still be typed: the folder to choose may not exist yet. -->
  <v-text-field v-bind="attrs" v-model="model" autocomplete="off" spellcheck="false">
    <template #append-inner>
      <v-icon-btn
        :aria-label="t('directoryPicker.browse')"
        icon="mdi-folder-open-outline"
        size="small"
        variant="text"
        @click.stop="browsing = true"
      />
    </template>
  </v-text-field>

  <ServerDirectoryDialog
    v-model="browsing"
    :allow-create="allowCreate"
    :start="model"
    :title="pickerTitle"
    @select="model = $event"
  />
</template>

<script lang="ts" setup>
  /**
   * A text field for a folder of the machine of the daemon, with a button that opens a folder picker. The other
   * attributes (label, hint, rules, name...) go to the text field.
   */

  // Composables
  import { computed, ref, useAttrs } from 'vue'
  import { useI18n } from 'vue-i18n'

  // Components
  import ServerDirectoryDialog from './ServerDirectoryDialog.vue'

  defineOptions({ inheritAttrs: false })

  const model = defineModel<string>({ default: '' })

  withDefaults(defineProps<{
    /**
     * Whether new folders can be added to the path from the picker. They are never created on the machine.
     */
    allowCreate?: boolean
  }>(), {
    allowCreate: true,
  })

  const { t } = useI18n()
  const attrs = useAttrs()
  const browsing = ref(false)

  // The picker is titled after the field it fills.
  const pickerTitle = computed(() => typeof attrs.label === 'string' ? attrs.label : undefined)
</script>

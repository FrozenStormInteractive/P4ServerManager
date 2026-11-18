<template>
  <v-alert
    v-if="preview?.caseVariant"
    class="server-directory-case-warning mb-4"
    density="compact"
    type="warning"
    variant="tonal"
  >
    {{ t('serverDirectoryCaseWarning', { variant: preview.caseVariant, path: preview.path }) }}
  </v-alert>
</template>

<script lang="ts" setup>
  /**
   * Warns, while a new instance is set up, of an existing folder next to its server directory whose name differs by its
   * case only, such as `main` for `Main`. The creation is not refused: on the file system of the daemon both folders
   * live side by side, but they are easy to mix up, especially from Windows. The daemon is asked once the name or the
   * folder stops changing.
   */

  // Types
  import type { ServerDirectoryPreview } from '@/api/types'

  // Composables
  import { onBeforeUnmount, ref, watch } from 'vue'
  import { useI18n } from 'vue-i18n'

  // Utilities
  import { previewServerDirectory } from '@/api/instances'

  const props = defineProps<{
    /**
     * Name of the new instance, which names its default server directory.
     */
    name: string
    /**
     * Server directory typed in the form, empty for the default one.
     */
    serverDirectory: string
  }>()

  const DELAY_MS = 300
  const NAME_PATTERN = /^[A-Z][\w-]{0,31}$/i

  const { t } = useI18n()
  const preview = ref<ServerDirectoryPreview | null>(null)

  let timer: ReturnType<typeof setTimeout> | undefined
  // Only the answer to the last request counts.
  let latest = 0

  function isAbsolutePath (path: string) {
    return path.startsWith('/') || /^[A-Z]:[\\/]/i.test(path)
  }

  watch(() => [props.name, props.serverDirectory] as const, ([name, serverDirectory]) => {
    clearTimeout(timer)
    const request = ++latest
    const directory = serverDirectory.trim()
    const askable = directory ? isAbsolutePath(directory) : NAME_PATTERN.test(name)
    if (!askable) {
      preview.value = null
      return
    }
    timer = setTimeout(async () => {
      try {
        const result = await previewServerDirectory(name, directory || null)
        if (request === latest) {
          preview.value = result
        }
      } catch {
        // Only a warning: the creation reports what is wrong with the name or the folder.
        if (request === latest) {
          preview.value = null
        }
      }
    }, DELAY_MS)
  }, { immediate: true })

  onBeforeUnmount(() => clearTimeout(timer))
</script>

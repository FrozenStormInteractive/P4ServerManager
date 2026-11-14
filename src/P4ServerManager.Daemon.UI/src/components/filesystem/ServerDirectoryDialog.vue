<template>
  <v-dialog v-model="open" max-width="600" scrollable>
    <v-card class="server-directory-dialog" :title="title ?? t('directoryPicker.title')">
      <div class="d-flex align-center ga-1 px-2 pb-1">
        <v-icon-btn
          :aria-label="t('directoryPicker.up')"
          :disabled="!parent"
          icon="mdi-arrow-up"
          name="directoryUp"
          size="small"
          variant="text"
          @click="go(parent)"
        />

        <v-menu v-if="(listing?.roots.length ?? 0) > 1">
          <template #activator="{ props: menuProps }">
            <v-icon-btn
              v-bind="menuProps"
              :aria-label="t('directoryPicker.roots')"
              icon="mdi-harddisk"
              size="small"
              variant="text"
            />
          </template>

          <v-list density="compact">
            <v-list-item
              v-for="root in listing!.roots"
              :key="root.path"
              :active="ancestors[0]?.path === root.path"
              :title="root.name"
              @click="go(root.path)"
            />
          </v-list>
        </v-menu>

        <!-- The breadcrumb scrolls on its own, so that a deep folder keeps the buttons in place. -->
        <nav :aria-label="t('directoryPicker.location')" class="directory-breadcrumb d-flex align-center flex-grow-1">
          <template v-for="(ancestor, index) in ancestors" :key="ancestor.path">
            <!-- The root ends with its own separator: '/' only goes between two folders. -->
            <span v-if="index > 1" aria-hidden="true" class="breadcrumb-separator text-medium-emphasis">/</span>

            <v-btn
              :aria-current="index === ancestors.length - 1 ? 'location' : undefined"
              :class="['text-none px-1', { 'font-italic': ancestor.virtual }]"
              density="comfortable"
              :disabled="index === ancestors.length - 1"
              min-width="0"
              size="small"
              :text="ancestor.name"
              variant="text"
              @click="go(ancestor.path)"
            />
          </template>
        </nav>

        <v-icon-btn
          :aria-label="t('directoryPicker.refresh')"
          :disabled="!listing"
          icon="mdi-refresh"
          name="directoryRefresh"
          size="small"
          variant="text"
          @click="refresh"
        />

        <v-icon-btn
          v-if="allowCreate"
          :active="creating"
          :aria-label="t('directoryPicker.newFolder')"
          :aria-pressed="creating"
          :disabled="!listing || !writable"
          icon="mdi-folder-plus-outline"
          name="newFolder"
          size="small"
          variant="text"
          @click="toggleCreating"
        />
      </div>

      <v-expand-transition>
        <!-- Its own form: the field must not join the validation of the form that opened the dialog. -->
        <v-form
          v-if="creating"
          id="new-folder-form"
          ref="newFolderForm"
          class="d-flex align-start ga-2 px-4 pt-2"
          @submit.prevent="addFolder"
        >
          <v-text-field
            v-model="newFolderName"
            autofocus
            density="compact"
            :hint="t('directoryPicker.newFolderHint')"
            :label="t('directoryPicker.newFolderName')"
            name="newFolderName"
            persistent-hint
            :rules="[folderNameRule]"
          />

          <v-btn
            class="mt-1"
            :text="t('directoryPicker.add')"
            type="submit"
            variant="tonal"
          />
        </v-form>
      </v-expand-transition>

      <v-divider class="mt-1" />

      <v-card-text class="directory-list pa-0">
        <v-progress-linear v-if="loading" absolute indeterminate />

        <v-alert
          v-if="loadError"
          class="ma-4"
          density="compact"
          type="error"
          variant="tonal"
        >
          {{ loadError }}
          <template #append>
            <v-btn :text="t('actions.retry')" variant="text" @click="retry" />
          </template>
        </v-alert>

        <v-list v-if="directories.length > 0" density="compact">
          <!-- A read-only folder can still be opened, to reach the folders inside it. -->
          <v-list-item
            v-for="directory in directories"
            :key="directory.path"
            :class="{ 'read-only': !directory.writable, 'font-italic': directory.virtual }"
            :prepend-icon="directory.virtual ? 'mdi-folder-plus-outline' : directory.writable ? 'mdi-folder' : 'mdi-folder-lock-outline'"
            :title="directory.name"
            @click="go(directory.path)"
          >
            <template v-if="directory.virtual || !directory.writable" #append>
              <v-chip
                v-if="directory.virtual"
                color="primary"
                label
                size="x-small"
                :text="t('directoryPicker.newBadge')"
              />

              <v-chip
                v-else
                label
                prepend-icon="mdi-lock-outline"
                size="x-small"
                :text="t('directoryPicker.readOnly')"
              />
            </template>
          </v-list-item>
        </v-list>

        <p
          v-else-if="listing && !loading"
          class="text-body-medium text-medium-emphasis text-center pa-6"
        >
          {{ t('directoryPicker.empty') }}
        </p>
      </v-card-text>

      <v-divider />

      <div v-if="listing && !writable" class="read-only-notice d-flex align-center ga-2 px-4 pt-2 text-body-small text-medium-emphasis">
        <v-icon icon="mdi-lock-outline" size="small" />
        {{ t('directoryPicker.readOnlyFolder') }}
      </div>

      <v-card-actions>
        <span class="selected-path text-body-medium text-truncate px-2" :title="currentPath ?? undefined">
          {{ currentPath }}
        </span>

        <v-spacer />
        <v-btn :text="t('actions.cancel')" @click="open = false" />

        <v-btn
          color="primary"
          :disabled="!listing || !writable"
          name="selectDirectory"
          :text="t('directoryPicker.select')"
          variant="flat"
          @click="choose"
        />
      </v-card-actions>
    </v-card>
  </v-dialog>
</template>

<script lang="ts" setup>
  // Types
  import type { DirectoryEntry, DirectoryListing } from '@/api/types'
  import type { VForm } from 'vuetify/components'

  // Composables
  import { computed, ref, useTemplateRef, watch } from 'vue'
  import { useI18n } from 'vue-i18n'

  // Utilities
  import { ApiError } from '@/api/client'
  import { listDirectories } from '@/api/fileSystem'
  import { isValidFolderName, joinPath, parentPath, pathName } from '@/utils/serverPath'

  /**
   * A folder as the dialog shows it: one of the machine, or one added in the dialog, that does not exist yet.
   */
  interface ShownDirectory extends DirectoryEntry {
    virtual: boolean
  }

  const open = defineModel<boolean>({ default: false })

  const props = withDefaults(defineProps<{
    /**
     * Folder shown first. The part of the path that does not exist yet is shown as new folders.
     */
    start?: string | null
    title?: string
    /**
     * Whether new folders can be added to the path from the dialog. They are never created on the machine.
     */
    allowCreate?: boolean
  }>(), {
    start: null,
    title: undefined,
    allowCreate: true,
  })

  const emit = defineEmits<{
    select: [path: string]
  }>()

  const { t } = useI18n()
  const newFolderForm = useTemplateRef<VForm>('newFolderForm')

  // The last folder of the machine listed: the current folder, or the closest existing ancestor of a new folder.
  const listing = ref<DirectoryListing | null>(null)
  // The folders added in the dialog, which do not exist on the machine.
  const virtualFolders = ref<string[]>([])
  // The new folder shown, inside the listed folder; null when the listed folder itself is shown.
  const virtualPath = ref<string | null>(null)

  const loading = ref(false)
  const loadError = ref<string | null>(null)
  // The folder whose listing failed, to try again.
  const failedPath = ref<string | null>(null)

  const creating = ref(false)
  const newFolderName = ref('')

  // Only the answer to the last request is shown, however fast the user clicks.
  let lastRequest = 0

  const currentPath = computed(() => virtualPath.value ?? listing.value?.path ?? null)

  // A new folder would be created in its closest existing ancestor: the listed folder.
  const writable = computed(() => listing.value?.writable ?? false)

  const parent = computed(() => virtualPath.value ? parentPath(virtualPath.value) : listing.value?.parent ?? null)

  const ancestors = computed<ShownDirectory[]>(() => {
    if (!listing.value) {
      return []
    }
    const existing = listing.value.ancestors.map(ancestor => ({ ...ancestor, virtual: false }))
    const added: string[] = []
    for (let path = virtualPath.value; path && path !== listing.value.path; path = parentPath(path)) {
      added.unshift(path)
    }
    return [...existing, ...added.map(path => toVirtual(path))]
  })

  const directories = computed<ShownDirectory[]>(() => {
    if (!listing.value || !currentPath.value) {
      return []
    }
    const existing = virtualPath.value
      ? []
      : listing.value.directories.map(directory => ({ ...directory, virtual: false }))
    const added = virtualFolders.value
      .filter(path => parentPath(path) === currentPath.value && !existing.some(e => e.path === path))
      .map(path => toVirtual(path))
    return [...existing, ...added]
      .toSorted((a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: 'base' }))
  })

  watch(open, value => {
    if (value) {
      listing.value = null
      virtualFolders.value = []
      virtualPath.value = null
      loadError.value = null
      creating.value = false
      void openAt(props.start?.trim() || null)
    }
  }, { immediate: true })

  function toVirtual (path: string): ShownDirectory {
    return { name: pathName(path), path, writable: writable.value, virtual: true }
  }

  function folderNameRule (value: unknown): true | string {
    const name = String(value ?? '')
    if (!name) {
      return t('directoryPicker.rules.nameRequired')
    }
    if (!isValidFolderName(name)) {
      return t('directoryPicker.rules.nameFormat')
    }
    return !directories.value.some(directory => directory.name === name) || t('directoryPicker.rules.nameTaken')
  }

  function messageOf (error: unknown) {
    return error instanceof Error ? error.message : String(error)
  }

  /**
   * Shows the start folder. When it does not exist yet, its closest existing ancestor is listed and the rest of the
   * path is shown as new folders.
   */
  async function openAt (start: string | null) {
    const request = ++lastRequest
    loading.value = true
    let path = start
    // The segments that do not exist, from the deepest one; null once a path was refused for another reason.
    let missing: string[] | null = []
    try {
      for (;;) {
        try {
          const result = await listDirectories(path)
          if (request !== lastRequest) {
            return
          }
          listing.value = result
          loadError.value = null
          if (missing && missing.length > 0) {
            let added = result.path
            for (const name of missing.toReversed()) {
              added = joinPath(added, name)
              virtualFolders.value.push(added)
            }
            virtualPath.value = added
          }
          return
        } catch (error) {
          const notFound = error instanceof ApiError && error.status === 404
          const invalid = error instanceof ApiError && error.status === 400
          if ((!notFound && !invalid) || path === null || request !== lastRequest) {
            throw error
          }
          missing = notFound && missing ? [...missing, pathName(path)] : null
          path = parentPath(path)
        }
      }
    } catch (error) {
      if (request === lastRequest) {
        failedPath.value = start
        loadError.value = t('directoryPicker.loadError', { message: messageOf(error) })
      }
    } finally {
      if (request === lastRequest) {
        loading.value = false
      }
    }
  }

  /**
   * Lists a folder of the machine. A new folder to show inside it can be given.
   */
  async function navigate (path: string | null, virtual: string | null = null) {
    const request = ++lastRequest
    loading.value = true
    try {
      const result = await listDirectories(path)
      if (request === lastRequest) {
        listing.value = result
        virtualPath.value = virtual
        loadError.value = null
      }
    } catch (error) {
      // The folder shown so far stays, under the error.
      if (request === lastRequest) {
        failedPath.value = path
        loadError.value = t('directoryPicker.loadError', { message: messageOf(error) })
      }
    } finally {
      if (request === lastRequest) {
        loading.value = false
      }
    }
  }

  /**
   * Opens a folder: a new folder is shown at once, a folder of the machine is listed.
   */
  function go (path: string | null) {
    creating.value = false
    if (path && listing.value && path === listing.value.path) {
      virtualPath.value = null
    } else if (path && virtualFolders.value.includes(path)) {
      virtualPath.value = path
    } else {
      void navigate(path)
    }
  }

  function refresh () {
    if (listing.value) {
      void navigate(listing.value.path, virtualPath.value)
    }
  }

  function retry () {
    void (listing.value ? navigate(failedPath.value) : openAt(failedPath.value))
  }

  function toggleCreating () {
    creating.value = !creating.value
    newFolderName.value = ''
  }

  async function addFolder () {
    const result = await newFolderForm.value?.validate()
    if (!result?.valid || !currentPath.value) {
      return
    }

    // Nothing is created on the machine: the folder is added to the path, and opened, ready to be selected.
    const path = joinPath(currentPath.value, newFolderName.value)
    virtualFolders.value.push(path)
    virtualPath.value = path
    creating.value = false
    newFolderName.value = ''
  }

  function choose () {
    if (!currentPath.value || !writable.value) {
      return
    }
    emit('select', currentPath.value)
    open.value = false
  }
</script>

<style scoped>
  .directory-breadcrumb {
    min-width: 0;
    overflow-x: auto;
    white-space: nowrap;
    scrollbar-width: thin;
  }

  /* A steady height, so that the dialog does not jump from one folder to the next. */
  .directory-list {
    position: relative;
    height: min(360px, 50vh);
  }

  .read-only :deep(.v-list-item-title),
  .read-only :deep(.v-list-item__prepend) {
    opacity: var(--v-medium-emphasis-opacity);
  }

  .selected-path {
    min-width: 0;
    font-family: monospace;
  }
</style>

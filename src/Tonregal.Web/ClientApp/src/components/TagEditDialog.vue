<!-- SPDX-License-Identifier: AGPL-3.0-or-later -->
<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import Dialog from 'primevue/dialog'
import Button from 'primevue/button'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import { api, type AlbumDetail, type TagEditResult, type TrackDto } from '@/api'
import { albumGenre, changes, formFor, validate, type TagForm } from '@/tagEdit'

/**
 * Tag-Editor für einen Track oder, ohne `track`, das ganze Album. Gespeichert wird in die Dateien; was den Pfad
 * bestimmt (Interpret, Album, Jahr, Titel, Nummer), benennt sie nach der Ordnerregel um.
 */
const props = defineProps<{ album: AlbumDetail; track: TrackDto | null }>()
const visible = defineModel<boolean>('visible', { default: false })
const emit = defineEmits<{ saved: [result: TagEditResult, coverChanged: boolean] }>()

const single = computed(() => props.track !== null)
const original = ref<TagForm>(formFor(props.album, props.track))
const form = ref<TagForm>({ ...original.value })
const coverFile = ref<File | null>(null)
const coverPreview = ref<string | null>(null)
const saving = ref(false)
const error = ref<string | null>(null)
const fileErrors = ref<string[]>([])
const genreMixed = computed(() => !single.value && albumGenre(props.album).mixed)

watch(
  () => [visible.value, props.track?.id, props.album] as const,
  ([open]) => {
    if (!open) return
    original.value = formFor(props.album, props.track)
    form.value = { ...original.value }
    setCover(null)
    error.value = null
    fileErrors.value = []
  },
  { immediate: true },
)

function setCover(file: File | null) {
  if (coverPreview.value) URL.revokeObjectURL(coverPreview.value)
  coverFile.value = file
  coverPreview.value = file ? URL.createObjectURL(file) : null
}
onBeforeUnmount(() => setCover(null))

function pickCover(event: Event) {
  const file = (event.target as HTMLInputElement).files?.[0] ?? null
  if (file && !['image/jpeg', 'image/png'].includes(file.type)) {
    error.value = 'Das Cover muss ein JPEG- oder PNG-Bild sein.'
    return
  }
  error.value = null
  setCover(file)
}

const edit = computed(() => changes(form.value, original.value, single.value))
const dirty = computed(() => Object.keys(edit.value).length > 0 || coverFile.value !== null)

async function save() {
  error.value = validate(form.value, single.value)
  if (error.value) return
  saving.value = true
  fileErrors.value = []
  try {
    let result: TagEditResult = { albumId: props.album.id, changed: 0, moved: 0, errors: [] }
    if (Object.keys(edit.value).length > 0) {
      result = props.track ? await api.editTrackTags(props.track.id, edit.value) : await api.editAlbumTags(props.album.id, edit.value)
    }
    if (coverFile.value && result.albumId !== null) {
      const cover = await api.uploadCover(result.albumId, coverFile.value)
      result = { ...result, changed: Math.max(result.changed, cover.changed), errors: [...result.errors, ...cover.errors] }
    }
    fileErrors.value = result.errors
    emit('saved', result, coverFile.value !== null)
    if (result.errors.length === 0) visible.value = false
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  } finally {
    saving.value = false
  }
}

</script>

<template>
  <Dialog
    v-model:visible="visible"
    modal
    :header="track ? `Bearbeiten: ${track.title}` : `Album bearbeiten: ${album.title}`"
    class="w-full max-w-xl"
    :dismissable-mask="!saving"
  >
    <form class="flex flex-col gap-3" data-testid="tag-form" @submit.prevent="save">
      <template v-if="track">
        <label class="flex flex-col gap-1">
          <span class="text-sm text-surface-500">Titel</span>
          <InputText v-model="form.title" data-testid="tag-title" />
        </label>
        <label class="flex flex-col gap-1">
          <span class="text-sm text-surface-500">Interpreten <span class="text-xs">(mehrere mit ; trennen)</span></span>
          <InputText v-model="form.artists" data-testid="tag-artists" />
        </label>
        <div class="flex gap-3">
          <label class="flex flex-col gap-1 w-24">
            <span class="text-sm text-surface-500">Nummer</span>
            <InputText v-model="form.number" inputmode="numeric" data-testid="tag-number" />
          </label>
          <label class="flex flex-col gap-1 w-24">
            <span class="text-sm text-surface-500">Disc</span>
            <InputText v-model="form.disc" inputmode="numeric" data-testid="tag-disc" />
          </label>
        </div>
      </template>
      <label class="flex flex-col gap-1">
        <span class="text-sm text-surface-500">Album</span>
        <InputText v-model="form.album" data-testid="tag-album" />
      </label>
      <label class="flex flex-col gap-1">
        <span class="text-sm text-surface-500">Album-Interpret</span>
        <InputText v-model="form.albumArtist" data-testid="tag-album-artist" />
        <span v-if="!track" class="text-xs text-surface-500">
          Tracks dieses Interpreten ziehen mit, Gäste bleiben. „Various Artists“ macht das Album zur Compilation.
        </span>
      </label>
      <div class="flex gap-3">
        <label class="flex flex-col gap-1 w-28">
          <span class="text-sm text-surface-500">Jahr</span>
          <InputText v-model="form.year" inputmode="numeric" data-testid="tag-year" />
        </label>
        <label class="flex flex-col gap-1 grow">
          <span class="text-sm text-surface-500">Genre</span>
          <InputText v-model="form.genre" :placeholder="genreMixed ? 'verschieden, bleibt je Track' : ''" data-testid="tag-genre" />
        </label>
      </div>
      <p v-if="track && (form.album.trim() !== original.album.trim() || form.albumArtist.trim() !== original.albumArtist.trim())" class="text-xs text-surface-500">
        Der Track wandert in ein anderes Album; gibt es das noch nicht, legt Tonregal es an.
      </p>

      <div v-if="!track" class="flex items-center gap-3">
        <img v-if="coverPreview" :src="coverPreview" alt="Neues Cover" class="size-16 rounded object-cover" />
        <label class="p-button p-button-secondary p-button-sm cursor-pointer" data-testid="tag-cover-label">
          <i class="pi pi-image mr-2" />{{ coverFile ? 'Anderes Cover' : 'Cover hochladen' }}
          <input type="file" accept="image/jpeg,image/png" class="hidden" data-testid="tag-cover" @change="pickCover" />
        </label>
        <span v-if="coverFile" class="text-sm text-surface-500 truncate">{{ coverFile.name }}</span>
      </div>

      <Message v-if="error" severity="error" data-testid="tag-error">{{ error }}</Message>
      <Message v-if="fileErrors.length" severity="warn" data-testid="tag-file-errors">
        Nicht alle Dateien ließen sich ändern:
        <ul class="list-disc pl-5 text-sm">
          <li v-for="message in fileErrors" :key="message">{{ message }}</li>
        </ul>
      </Message>
      <p class="text-xs text-surface-500">
        Die Änderung landet in den Dateien. Ändern sich Interpret, Album, Jahr, Titel oder Nummer, bekommt die Datei einen
        neuen Platz nach der Ordnerregel; Bewertungen, Playlists und Songtexte gehen mit.
      </p>
      <div class="flex justify-end gap-2">
        <Button label="Abbrechen" text :disabled="saving" @click="visible = false" />
        <Button type="submit" label="Speichern" icon="pi pi-check" :loading="saving" :disabled="!dirty" data-testid="tag-save" />
      </div>
    </form>
  </Dialog>
</template>

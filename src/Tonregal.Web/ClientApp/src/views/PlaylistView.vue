<!-- SPDX-License-Identifier: AGPL-3.0-or-later -->
<script setup lang="ts">
import { computed, ref, watchEffect } from 'vue'
import { useRouter } from 'vue-router'
import Button from 'primevue/button'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import PlaylistCover from '@/components/PlaylistCover.vue'
import ShuffleIcon from '@/components/ShuffleIcon.vue'
import { api, formatDuration, type PlaylistDetail, type PlaylistItem } from '@/api'
import { moved, useReorder } from '@/reorder'
import { usePlayerStore } from '@/stores/player'
import { usePlaylistsStore } from '@/stores/playlists'

const props = defineProps<{ id: number }>()
const player = usePlayerStore()
const playlists = usePlaylistsStore()
const router = useRouter()
const detail = ref<PlaylistDetail | null>(null)
const error = ref<string | null>(null)
const editing = ref<{ name: string; comment: string } | null>(null)
const confirmingDelete = ref(false)

watchEffect(async () => {
  try {
    detail.value = await api.playlist(props.id)
    error.value = null
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  }
})

const items = computed(() => detail.value?.items ?? [])
const total = computed(() => formatDuration(items.value.reduce((sum, i) => sum + i.durationMs, 0)))

function show(next: PlaylistDetail) {
  detail.value = next
  playlists.put(next.playlist)
}

/** Sofort zeigen, dann speichern; sagt der Server nein, gilt wieder sein Stand. */
async function reorder(next: PlaylistItem[]) {
  if (!detail.value) return
  const before = detail.value
  detail.value = { ...before, items: next }
  error.value = null
  try {
    show(await api.setPlaylistTracks(props.id, next.map((i) => i.trackId)))
  } catch (e) {
    detail.value = before
    error.value = e instanceof Error ? e.message : String(e)
  }
}

const { dragFrom, dragTo, dragStart, dragMove, dragEnd, moveByKey } = useReorder(
  () => items.value.length,
  (from, to) => reorder(moved(items.value, from, to)),
)

async function remove(item: PlaylistItem) {
  error.value = null
  try {
    await api.removeFromPlaylist(props.id, item.trackId)
    show(await api.playlist(props.id))
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  }
}

function edit() {
  if (!detail.value) return
  editing.value = { name: detail.value.playlist.name, comment: detail.value.playlist.comment ?? '' }
}

async function save() {
  if (!editing.value || !detail.value) return
  error.value = null
  try {
    const saved = await api.updatePlaylist(props.id, editing.value.name, editing.value.comment || null)
    detail.value = { ...detail.value, playlist: saved }
    playlists.put(saved)
    editing.value = null
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  }
}

async function destroy() {
  error.value = null
  try {
    await api.deletePlaylist(props.id)
    playlists.forget(props.id)
    await router.push({ name: 'playlists' })
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  }
}

const isCurrent = (item: PlaylistItem) => player.current?.trackId === item.trackId
</script>

<template>
  <RouterLink :to="{ name: 'playlists' }" class="text-sm text-surface-500 hover:underline"><i class="pi pi-arrow-left text-xs" /> Playlists</RouterLink>
  <Message v-if="error" severity="error" :closable="false" class="mt-2">{{ error }}</Message>
  <template v-if="detail">
    <div class="flex flex-col sm:flex-row gap-6 mt-2 mb-6">
      <div class="w-48 shrink-0"><PlaylistCover :album-ids="detail.playlist.albumIds" :alt="detail.playlist.name" /></div>
      <div class="flex flex-col justify-end min-w-0">
        <form v-if="editing" class="flex flex-col gap-2" data-testid="edit-form" @submit.prevent="save">
          <InputText v-model="editing.name" required maxlength="100" aria-label="Name" data-testid="edit-name" />
          <InputText v-model="editing.comment" maxlength="500" placeholder="Beschreibung (optional)" aria-label="Beschreibung" />
          <div class="flex gap-2">
            <Button type="submit" label="Speichern" icon="pi pi-check" size="small" :disabled="!editing.name.trim()" />
            <Button label="Abbrechen" text size="small" @click="editing = null" />
          </div>
        </form>
        <template v-else>
          <h1 class="text-2xl font-semibold" data-testid="playlist-title">{{ detail.playlist.name }}</h1>
          <p v-if="detail.playlist.comment" class="m-0 text-surface-500">{{ detail.playlist.comment }}</p>
        </template>
        <div class="text-surface-500">{{ items.length }} Stück{{ items.length === 1 ? '' : 'e' }} · {{ total }}</div>
        <div class="mt-3 flex flex-wrap items-center gap-2">
          <Button label="Abspielen" icon="pi pi-play" size="small" :disabled="!items.length" data-testid="play-playlist" @click="player.play(items)" />
          <Button label="Anhängen" icon="pi pi-plus" size="small" severity="secondary" :disabled="!items.length" @click="player.enqueue(items)" />
          <Button label="Zufällig" size="small" severity="secondary" :disabled="!items.length" data-testid="shuffle-playlist" @click="player.playShuffled(items)">
            <template #icon="{ class: iconClass }"><ShuffleIcon :class="iconClass" /></template>
          </Button>
          <Button v-if="!editing" label="Bearbeiten" icon="pi pi-pencil" size="small" severity="secondary" text data-testid="edit" @click="edit" />
          <Button v-if="!confirmingDelete" label="Löschen" icon="pi pi-trash" size="small" severity="danger" text data-testid="delete" @click="confirmingDelete = true" />
        </div>
        <div v-if="confirmingDelete" class="mt-3 flex flex-wrap items-center gap-2">
          <span class="text-sm">Playlist löschen? Die Stücke bleiben in der Bibliothek.</span>
          <Button label="Ja, löschen" severity="danger" size="small" data-testid="confirm-delete" @click="destroy" />
          <Button label="Abbrechen" text size="small" @click="confirmingDelete = false" />
        </div>
      </div>
    </div>

    <p v-if="!items.length" class="text-surface-500" data-testid="empty">
      Noch leer. Stücke kommen über das Lesezeichen <i class="pi pi-bookmark text-sm" /> in der Albumansicht, im großen Player oder in der
      Warteschlange hinein.
    </p>
    <ol class="flex flex-col max-w-3xl" @pointermove="dragMove" @pointerup="dragEnd" @pointercancel="dragEnd">
      <li
        v-for="(item, i) in items"
        :key="item.trackId"
        ref="rows"
        class="flex items-center gap-1 rounded-md px-1 py-1.5 hover:bg-surface-100 dark:hover:bg-surface-800"
        :class="[
          isCurrent(item) && 'text-primary font-semibold',
          i === dragFrom && 'bg-surface-100 dark:bg-surface-800 shadow',
          dragFrom >= 0 && i === dragTo && i !== dragFrom && (dragTo > dragFrom ? 'border-b-2 border-primary' : 'border-t-2 border-primary'),
        ]"
        data-testid="playlist-item"
      >
        <span
          data-handle
          tabindex="0"
          role="button"
          class="drag-handle shrink-0 cursor-grab px-2 py-1 text-surface-400"
          :aria-label="`${item.title} verschieben (Pfeiltasten)`"
          title="Ziehen zum Verschieben"
          data-testid="playlist-handle"
          @pointerdown.prevent="dragStart($event, i)"
          @keydown="moveByKey($event, i)"
        >
          <i class="pi pi-bars" />
        </span>
        <span class="w-6 shrink-0 text-right tabular-nums text-surface-500 text-sm">
          <i v-if="isCurrent(item)" class="pi pi-volume-up text-primary" />
          <template v-else>{{ i + 1 }}</template>
        </span>
        <button type="button" class="flex-1 min-w-0 text-left px-2 cursor-pointer" title="Ab hier abspielen" data-testid="play-from" @click="player.play(items, i)">
          <div class="truncate">{{ item.title }}</div>
          <div class="truncate text-sm text-surface-500 font-normal">
            {{ item.artist }} ·
            <RouterLink :to="{ name: 'album', params: { id: item.albumId } }" class="hover:underline" @click.stop>{{ item.album }}</RouterLink>
          </div>
        </button>
        <span class="hidden sm:inline text-sm text-surface-500 tabular-nums">{{ formatDuration(item.durationMs) }}</span>
        <Button
          icon="pi pi-plus"
          text
          rounded
          size="small"
          aria-label="Als Nächstes"
          title="Als Nächstes spielen"
          @click="player.playNext([item])"
        />
        <Button
          icon="pi pi-times"
          text
          rounded
          size="small"
          severity="secondary"
          aria-label="Aus der Playlist entfernen"
          title="Aus der Playlist entfernen"
          data-testid="remove"
          @click="remove(item)"
        />
      </li>
    </ol>
  </template>
</template>

<style scoped>
/* Nur der Griff nimmt den Finger, damit die Liste daneben weiter scrollt. */
.drag-handle {
  touch-action: none;
}
</style>

<!-- SPDX-License-Identifier: AGPL-3.0-or-later -->
<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import Button from 'primevue/button'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import PlaylistCover from '@/components/PlaylistCover.vue'
import ShuffleIcon from '@/components/ShuffleIcon.vue'
import { api, formatDuration, type PlaylistSummary } from '@/api'
import { usePlayerStore } from '@/stores/player'
import { usePlaylistsStore } from '@/stores/playlists'

/** Eigene Playlists: feste Stücke in fester Reihenfolge, gefüllt über „Zur Playlist“ an Alben, Stücken und im Player. */
const playlists = usePlaylistsStore()
const player = usePlayerStore()
const router = useRouter()
const error = ref<string | null>(null)
const creating = ref(false)
const name = ref('')
const saving = ref(false)
const loading = ref<number | null>(null)

onMounted(async () => {
  try {
    await playlists.load()
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  }
})

async function create() {
  saving.value = true
  error.value = null
  try {
    const created = await playlists.create(name.value)
    creating.value = false
    name.value = ''
    await router.push({ name: 'playlist', params: { id: created.id } })
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  } finally {
    saving.value = false
  }
}

async function play(p: PlaylistSummary, shuffle = false) {
  loading.value = p.id
  error.value = null
  try {
    const { items } = await api.playlist(p.id)
    if (shuffle) player.playShuffled(items)
    else player.play(items)
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  } finally {
    loading.value = null
  }
}

/** „1:02:03“ wird bei langen Listen zu „1 Std. 2 Min.“, das liest sich in einer Übersicht leichter. */
function length(ms: number) {
  const minutes = Math.round(ms / 60000)
  return minutes >= 60 ? `${Math.floor(minutes / 60)} Std. ${minutes % 60} Min.` : formatDuration(ms)
}
</script>

<template>
  <div class="flex flex-col gap-4">
    <div class="flex flex-wrap items-center justify-between gap-2">
      <h1 class="text-2xl font-semibold m-0">Playlists</h1>
      <Button v-if="!creating" label="Neue Playlist" icon="pi pi-plus" data-testid="new-playlist" @click="creating = true" />
    </div>
    <p class="m-0 text-surface-500 max-w-3xl">
      Deine eigenen Listen mit festen Stücken. Füllen lassen sie sich über das Lesezeichen
      <i class="pi pi-bookmark text-sm" /> an Alben, Stücken, im großen Player und in der Warteschlange. Regeln statt fester Stücke
      gibt es bei den <RouterLink to="/smart-playlists" class="underline">Smart Playlists</RouterLink>.
    </p>
    <Message v-if="error" severity="error" :closable="false">{{ error }}</Message>
    <form v-if="creating" class="flex flex-wrap items-center gap-2" data-testid="create-form" @submit.prevent="create">
      <InputText v-model="name" placeholder="Name der Playlist" autofocus required maxlength="100" data-testid="playlist-name" />
      <Button type="submit" label="Anlegen" icon="pi pi-check" :loading="saving" :disabled="!name.trim()" />
      <Button label="Abbrechen" text @click="creating = false" />
    </form>

    <p v-if="playlists.loaded && playlists.list.length === 0" class="m-0 text-surface-500" data-testid="empty">Noch keine eigenen Playlists.</p>
    <div class="grid grid-cols-2 sm:grid-cols-3 lg:grid-cols-5 gap-4">
      <div v-for="p in playlists.list" :key="p.id" class="flex flex-col gap-2" :data-testid="`playlist-${p.id}`">
        <RouterLink :to="{ name: 'playlist', params: { id: p.id } }" class="block hover:opacity-90">
          <PlaylistCover :album-ids="p.albumIds" :alt="p.name" />
        </RouterLink>
        <div class="min-w-0">
          <RouterLink :to="{ name: 'playlist', params: { id: p.id } }" class="block truncate font-medium hover:underline">{{ p.name }}</RouterLink>
          <div class="text-sm text-surface-500" data-testid="summary">
            {{ p.trackCount }} Stück{{ p.trackCount === 1 ? '' : 'e' }}<template v-if="p.trackCount"> · {{ length(p.durationMs) }}</template>
          </div>
        </div>
        <div class="flex gap-1">
          <Button
            icon="pi pi-play"
            rounded
            size="small"
            aria-label="Abspielen"
            title="Abspielen"
            :disabled="!p.trackCount"
            :loading="loading === p.id"
            data-testid="play"
            @click="play(p)"
          />
          <Button text rounded size="small" aria-label="Zufällig abspielen" title="Zufällig abspielen" :disabled="!p.trackCount || loading === p.id" @click="play(p, true)">
            <template #icon="{ class: iconClass }"><ShuffleIcon :class="[iconClass, 'text-base']" /></template>
          </Button>
        </div>
      </div>
    </div>
  </div>
</template>

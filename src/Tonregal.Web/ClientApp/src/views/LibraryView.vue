<!-- SPDX-License-Identifier: AGPL-3.0-or-later -->
<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import Button from 'primevue/button'
import IconField from 'primevue/iconfield'
import InputIcon from 'primevue/inputicon'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import ScanButton from '@/components/ScanButton.vue'
import ShuffleIcon from '@/components/ShuffleIcon.vue'
import AlbumCover from '@/components/AlbumCover.vue'
import { api, formatDuration, type ArtistSummary, type LibrarySearchResult } from '@/api'
import { usePlayerStore } from '@/stores/player'

const artists = ref<ArtistSummary[]>([])
/** Bleibt für die Sitzung, damit „zurück“ aus einem Album wieder bei den Treffern landet. */
const queryKey = 'tonregal.librarySearch'
const filter = ref(readQuery())
const results = ref<LibrarySearchResult | null>(null)
const searching = ref(false)
let searchTimer: ReturnType<typeof setTimeout> | undefined
let searchToken = 0
const error = ref<string | null>(null)
const loaded = ref(false)
const player = usePlayerStore()
const shuffling = ref(false)

async function shuffleAll() {
  shuffling.value = true
  try {
    player.play(await api.shuffle())
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  } finally {
    shuffling.value = false
  }
}

async function load() {
  try {
    artists.value = await api.artists()
    error.value = null
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  } finally {
    loaded.value = true
  }
}

function readQuery(): string {
  try {
    return sessionStorage.getItem(queryKey) ?? ''
  } catch {
    return ''
  }
}

/** Kurz warten, damit nicht jeder Tastendruck die Bibliothek durchsucht; nur die letzte Antwort zählt. */
async function search(query: string) {
  const token = ++searchToken
  if (!query) {
    results.value = null
    searching.value = false
    return
  }
  searching.value = true
  try {
    const found = await api.search(query)
    if (token === searchToken) results.value = found
  } catch (e) {
    if (token === searchToken) error.value = e instanceof Error ? e.message : String(e)
  } finally {
    if (token === searchToken) searching.value = false
  }
}

watch(
  filter,
  (value) => {
    const query = value.trim()
    try {
      if (query) sessionStorage.setItem(queryKey, value)
      else sessionStorage.removeItem(queryKey)
    } catch {
      // Ohne Speicher (privates Fenster) geht die Suche nach dem Zurückgehen eben verloren.
    }
    clearTimeout(searchTimer)
    if (!query) void search('')
    else searchTimer = setTimeout(() => void search(query), 250)
  },
  { immediate: true },
)

onMounted(load)
onUnmounted(() => clearTimeout(searchTimer))

const tracks = computed(() => (filter.value.trim() ? (results.value?.tracks ?? []) : []))
const albums = computed(() => (filter.value.trim() ? (results.value?.albums ?? []) : []))
const nothingFound = computed(
  () => !!filter.value.trim() && !searching.value && results.value !== null && !tracks.value.length && !albums.value.length && !visible.value.length,
)

const visible = computed(() => {
  const f = filter.value.trim().toLowerCase()
  return f ? artists.value.filter((a) => a.name.toLowerCase().includes(f)) : artists.value
})

/** Gruppiert nach Anfangsbuchstabe des Sortiernamens. */
const groups = computed(() => {
  const map = new Map<string, ArtistSummary[]>()
  for (const artist of visible.value) {
    const first = artist.sortName.charAt(0).toUpperCase()
    const letter = /[A-ZÄÖÜ]/.test(first) ? first : '#'
    map.set(letter, [...(map.get(letter) ?? []), artist])
  }
  return [...map.entries()]
})
</script>

<template>
  <div class="flex flex-wrap items-center gap-3 mb-6">
    <h1 class="text-2xl font-semibold mr-auto">Bibliothek</h1>
    <IconField>
      <InputIcon :class="searching ? 'pi pi-spin pi-spinner' : 'pi pi-search'" />
      <InputText v-model="filter" type="search" placeholder="Titel, Album oder Künstler" size="small" aria-label="Bibliothek durchsuchen" data-testid="search" />
    </IconField>
    <Button
      label="Alles zufällig"
      size="small"
      severity="secondary"
      :loading="shuffling"
      :disabled="!artists.length"
      data-testid="shuffle-library"
      @click="shuffleAll"
    >
      <template #icon="{ class: iconClass }"><ShuffleIcon :class="iconClass" /></template>
    </Button>
    <ScanButton @finished="load" />
  </div>

  <Message v-if="error" severity="error">{{ error }}</Message>
  <p v-else-if="loaded && artists.length === 0" class="text-surface-500">
    Noch nichts in der Bibliothek. Lege Alben unter <code>Library/</code> ab und starte einen Scan.
  </p>

  <p v-if="nothingFound" class="text-surface-500" data-testid="no-results">Nichts gefunden für „{{ filter.trim() }}“.</p>

  <section v-if="tracks.length" class="mb-8" data-testid="track-results">
    <h2 class="text-sm font-semibold text-surface-500 mb-2">
      Titel<span v-if="results && results.trackCount > tracks.length"> · die ersten {{ tracks.length }} von {{ results.trackCount }}</span>
    </h2>
    <ul class="divide-y divide-surface-200 dark:divide-surface-800">
      <li v-for="(track, i) in tracks" :key="track.trackId" class="flex items-center gap-3 py-1.5" data-testid="track">
        <button
          type="button"
          class="flex flex-1 min-w-0 items-center gap-3 rounded-md px-2 py-1 text-left hover:bg-surface-100 dark:hover:bg-surface-800"
          :class="player.current?.trackId === track.trackId && 'text-primary-600 dark:text-primary-300'"
          :title="`${track.title} abspielen, danach die übrigen Treffer`"
          @click="player.play(tracks, i)"
        >
          <div class="size-10 shrink-0"><AlbumCover :album-id="track.albumId" :alt="track.album" /></div>
          <div class="min-w-0 flex-1">
            <div class="truncate font-medium">{{ track.title }}</div>
            <div class="truncate text-sm text-surface-500">{{ track.artist }} · {{ track.album }}</div>
          </div>
          <span class="text-sm text-surface-500 tabular-nums">{{ formatDuration(track.durationMs) }}</span>
        </button>
        <Button
          icon="pi pi-plus"
          text
          rounded
          size="small"
          aria-label="Als Nächstes"
          title="Als Nächstes spielen"
          data-testid="play-next"
          @click="player.playNext([track])"
        />
        <RouterLink
          :to="{ name: 'album', params: { id: track.albumId } }"
          class="hidden sm:inline text-sm text-surface-500 hover:underline"
          title="Zum Album"
        ><i class="pi pi-arrow-right text-xs" /></RouterLink>
      </li>
    </ul>
  </section>

  <section v-if="albums.length" class="mb-8" data-testid="album-results">
    <h2 class="text-sm font-semibold text-surface-500 mb-2">Alben</h2>
    <div class="grid grid-cols-3 sm:grid-cols-4 lg:grid-cols-6 gap-4">
      <RouterLink v-for="album in albums" :key="album.id" :to="{ name: 'album', params: { id: album.id } }" class="group" data-testid="album">
        <AlbumCover :album-id="album.id" :alt="album.title" />
        <div class="mt-1 truncate text-sm font-medium group-hover:underline">{{ album.title }}</div>
        <div class="truncate text-xs text-surface-500">{{ album.albumArtist.name }}<span v-if="album.year"> · {{ album.year }}</span></div>
      </RouterLink>
    </div>
  </section>

  <h2 v-if="filter.trim() && visible.length" class="text-sm font-semibold text-surface-500 mb-2">Künstler</h2>
  <section v-for="[letter, items] in groups" :key="letter" class="mb-6">
    <h2 v-if="!filter.trim()" class="text-sm font-semibold text-surface-500 mb-2">{{ letter }}</h2>
    <ul class="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-2">
      <li v-for="artist in items" :key="artist.id">
        <RouterLink
          :to="{ name: 'artist', params: { id: artist.id } }"
          class="flex items-center justify-between rounded-md px-3 py-2 hover:bg-surface-100 dark:hover:bg-surface-800"
          data-testid="artist"
        >
          <span>{{ artist.name }}</span>
          <span class="text-sm text-surface-500">{{ artist.albumCount }} {{ artist.albumCount === 1 ? 'Album' : 'Alben' }}</span>
        </RouterLink>
      </li>
    </ul>
  </section>
</template>

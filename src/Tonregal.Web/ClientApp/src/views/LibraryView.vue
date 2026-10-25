<!-- SPDX-License-Identifier: AGPL-3.0-or-later -->
<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import ScanButton from '@/components/ScanButton.vue'
import { api, type ArtistSummary } from '@/api'

const artists = ref<ArtistSummary[]>([])
const filter = ref('')
const error = ref<string | null>(null)
const loaded = ref(false)

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

onMounted(load)

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
    <InputText v-model="filter" placeholder="Künstler suchen" size="small" />
    <ScanButton @finished="load" />
  </div>

  <Message v-if="error" severity="error">{{ error }}</Message>
  <p v-else-if="loaded && artists.length === 0" class="text-surface-500">
    Noch nichts in der Bibliothek. Lege Alben unter <code>Library/</code> ab und starte einen Scan.
  </p>

  <section v-for="[letter, items] in groups" :key="letter" class="mb-6">
    <h2 class="text-sm font-semibold text-surface-500 mb-2">{{ letter }}</h2>
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

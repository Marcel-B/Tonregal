<!-- SPDX-License-Identifier: AGPL-3.0-or-later -->
<script setup lang="ts">
import { ref, watchEffect } from 'vue'
import Message from 'primevue/message'
import AlbumCover from '@/components/AlbumCover.vue'
import { api, type ArtistDetail } from '@/api'

const props = defineProps<{ id: number }>()
const artist = ref<ArtistDetail | null>(null)
const error = ref<string | null>(null)

watchEffect(async () => {
  try {
    artist.value = await api.artist(props.id)
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  }
})
</script>

<template>
  <RouterLink to="/library" class="text-sm text-surface-500 hover:underline"><i class="pi pi-arrow-left text-xs" /> Bibliothek</RouterLink>
  <Message v-if="error" severity="error" class="mt-4">{{ error }}</Message>
  <template v-if="artist">
    <h1 class="text-2xl font-semibold mt-2 mb-6">{{ artist.name }}</h1>
    <div class="grid grid-cols-2 sm:grid-cols-3 lg:grid-cols-5 gap-4">
      <RouterLink v-for="album in artist.albums" :key="album.id" :to="{ name: 'album', params: { id: album.id } }" class="group" data-testid="album">
        <AlbumCover :album-id="album.id" :alt="album.title" />
        <div class="mt-2 font-medium group-hover:underline">{{ album.title }}</div>
        <div class="text-sm text-surface-500">
          {{ album.year ?? 'ohne Jahr' }} · {{ album.trackCount }} Tracks<span v-if="album.kind !== 'Album'"> · {{ album.kind }}</span>
        </div>
      </RouterLink>
    </div>
  </template>
</template>

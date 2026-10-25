<!-- SPDX-License-Identifier: AGPL-3.0-or-later -->
<script setup lang="ts">
import { computed, ref, watchEffect } from 'vue'
import DataTable from 'primevue/datatable'
import Column from 'primevue/column'
import Tag from 'primevue/tag'
import Message from 'primevue/message'
import AlbumCover from '@/components/AlbumCover.vue'
import { api, formatDuration, type AlbumDetail, type TrackDto } from '@/api'

const props = defineProps<{ id: number }>()
const album = ref<AlbumDetail | null>(null)
const error = ref<string | null>(null)

watchEffect(async () => {
  try {
    album.value = await api.album(props.id)
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  }
})

const totalDuration = computed(() => formatDuration(album.value?.tracks.reduce((sum, t) => sum + t.durationMs, 0) ?? 0))

/** Weitere Artists neben dem Album-Artist, z. B. Gäste oder die Interpreten einer Compilation. */
function guests(track: TrackDto) {
  return track.artists.filter((a) => a !== album.value?.albumArtist.name).join(', ')
}

const lossless = new Set(['Flac', 'Alac', 'Wav'])
</script>

<template>
  <Message v-if="error" severity="error">{{ error }}</Message>
  <template v-if="album">
    <RouterLink :to="{ name: 'artist', params: { id: album.albumArtist.id } }" class="text-sm text-surface-500 hover:underline">
      <i class="pi pi-arrow-left text-xs" /> {{ album.albumArtist.name }}
    </RouterLink>
    <div class="flex flex-col sm:flex-row gap-6 mt-2 mb-6">
      <div class="w-48 shrink-0"><AlbumCover :album-id="album.id" :alt="album.title" /></div>
      <div class="flex flex-col justify-end">
        <h1 class="text-2xl font-semibold">{{ album.title }}</h1>
        <div class="text-surface-500">
          {{ album.albumArtist.name }} · {{ album.year ?? 'ohne Jahr' }} · {{ album.tracks.length }} Tracks · {{ totalDuration }}
        </div>
      </div>
    </div>

    <DataTable :value="album.tracks" data-key="id" size="small" :row-group-mode="album.discCount > 1 ? 'subheader' : undefined" group-rows-by="disc">
      <template #groupheader="{ data }"><span class="font-semibold">Disc {{ data.disc }}</span></template>
      <Column field="number" header="#" class="w-12 tabular-nums" />
      <Column header="Titel">
        <template #body="{ data }">
          <div data-testid="track-title">{{ data.title }}</div>
          <div v-if="guests(data)" class="text-sm text-surface-500">{{ guests(data) }}</div>
        </template>
      </Column>
      <Column header="Dauer" class="w-20 tabular-nums">
        <template #body="{ data }">{{ formatDuration(data.durationMs) }}</template>
      </Column>
      <Column header="BPM" class="w-16 tabular-nums">
        <template #body="{ data }">{{ data.bpm ? Math.round(data.bpm) : '' }}</template>
      </Column>
      <Column field="initialKey" header="Tonart" class="w-20" />
      <Column header="Format" class="w-40">
        <template #body="{ data }">
          <div class="flex gap-1 flex-wrap">
            <Tag
              v-for="file in data.files"
              :key="file.id"
              :value="file.format.toUpperCase()"
              :severity="lossless.has(file.format) ? 'success' : 'secondary'"
              :title="`${file.path} · ${Math.round(file.bitrate)} kbit/s · ${file.sampleRate} Hz`"
            />
          </div>
        </template>
      </Column>
    </DataTable>
  </template>
</template>

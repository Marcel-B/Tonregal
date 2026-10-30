<!-- SPDX-License-Identifier: AGPL-3.0-or-later -->
<script setup lang="ts">
import { computed, ref, watchEffect } from 'vue'
import DataTable from 'primevue/datatable'
import Column from 'primevue/column'
import Tag from 'primevue/tag'
import Message from 'primevue/message'
import Button from 'primevue/button'
import AlbumCover from '@/components/AlbumCover.vue'
import MatchDialog from '@/components/MatchDialog.vue'
import { api, formatDuration, type AlbumDetail, type FileDto, type TrackDto } from '@/api'
import { albumQueue, usePlayerStore } from '@/stores/player'

const props = defineProps<{ id: number }>()
const player = usePlayerStore()
const album = ref<AlbumDetail | null>(null)
const error = ref<string | null>(null)

async function load() {
  try {
    album.value = await api.album(props.id)
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  }
}
watchEffect(load)

const coverVersion = ref(0)
const fetchingCover = ref(false)
const coverError = ref<string | null>(null)
async function fetchCover() {
  fetchingCover.value = true
  coverError.value = null
  try {
    await api.fetchCover(props.id)
    coverVersion.value++
    await load()
  } catch (e) {
    coverError.value = e instanceof Error ? e.message : String(e)
  } finally {
    fetchingCover.value = false
  }
}

const aligning = ref(false)
const alignMessage = ref<string | null>(null)
async function alignRelease() {
  aligning.value = true
  alignMessage.value = null
  try {
    const result = await api.alignRelease(props.id)
    alignMessage.value =
      result.skipped > 0
        ? `${result.changed} Tracks angepasst, ${result.skipped} stehen nicht auf diesem Release.`
        : `${result.changed} Tracks angepasst.`
    await load()
  } catch (e) {
    alignMessage.value = e instanceof Error ? e.message : String(e)
  } finally {
    aligning.value = false
  }
}

const totalDuration = computed(() => formatDuration(album.value?.tracks.reduce((sum, t) => sum + t.durationMs, 0) ?? 0))

/** Weitere Artists neben dem Album-Artist, z. B. Gäste oder die Interpreten einer Compilation. */
function guests(track: TrackDto) {
  return track.artists.filter((a) => a !== album.value?.albumArtist.name).join(', ')
}

const lossless = new Set(['Flac', 'Alac', 'Wav'])

const matchTrack = ref<TrackDto | null>(null)
const matchFile = ref<FileDto | null>(null)
const matchVisible = ref(false)

const isCurrent = (track: TrackDto) => player.current?.trackId === track.id
const trackItems = (track: TrackDto) => albumQueue(album.value!).filter((i) => i.trackId === track.id)

function openMatch(track: TrackDto) {
  matchTrack.value = track
  matchFile.value = track.files[0] ?? null
  matchVisible.value = true
}
</script>

<template>
  <Message v-if="error" severity="error">{{ error }}</Message>
  <template v-if="album">
    <RouterLink :to="{ name: 'artist', params: { id: album.albumArtist.id } }" class="text-sm text-surface-500 hover:underline">
      <i class="pi pi-arrow-left text-xs" /> {{ album.albumArtist.name }}
    </RouterLink>
    <div class="flex flex-col sm:flex-row gap-6 mt-2 mb-6">
      <div class="w-48 shrink-0"><AlbumCover :key="coverVersion" :album-id="album.id" :alt="album.title" /></div>
      <div class="flex flex-col justify-end">
        <h1 class="text-2xl font-semibold">{{ album.title }}</h1>
        <div class="text-surface-500">
          {{ album.albumArtist.name }} · {{ album.year ?? 'ohne Jahr' }} · {{ album.tracks.length }} Tracks · {{ totalDuration }}
        </div>
        <div class="mt-3 flex flex-wrap items-center gap-2">
          <Button label="Abspielen" icon="pi pi-play" size="small" :disabled="!album.tracks.length" data-testid="play-album" @click="player.playAlbum(album)" />
          <Button
            label="Anhängen"
            icon="pi pi-plus"
            size="small"
            severity="secondary"
            :disabled="!album.tracks.length"
            data-testid="enqueue-album"
            @click="player.enqueue(albumQueue(album))"
          />
          <Button
            v-if="album.tracks.length"
            as="a"
            :href="api.albumDownloadUrl(album.id)"
            download
            label="Herunterladen"
            icon="pi pi-download"
            size="small"
            severity="secondary"
            title="Album als ZIP mit den Originaldateien und dem Cover"
            data-testid="download-album"
          />
        </div>
        <div v-if="album.canFetchCover" class="mt-3 flex items-center gap-3">
          <Button label="Cover suchen" icon="pi pi-image" severity="secondary" size="small" :loading="fetchingCover" data-testid="fetch-cover" @click="fetchCover" />
          <span v-if="coverError" class="text-sm text-red-500">{{ coverError }}</span>
        </div>
        <div v-if="album.releaseCount > 1 || alignMessage" class="mt-3 flex items-center gap-3">
          <Button
            v-if="album.releaseCount > 1"
            :label="`${album.releaseCount} Ausgaben vereinheitlichen`"
            icon="pi pi-clone"
            severity="secondary"
            size="small"
            :loading="aligning"
            data-testid="align-release"
            title="Die Tracks tragen verschiedene MusicBrainz-Releases, deshalb zeigt Navidrome das Album mehrfach."
            @click="alignRelease"
          />
          <span v-if="alignMessage" class="text-sm text-surface-500" data-testid="align-message">{{ alignMessage }}</span>
        </div>
      </div>
    </div>

    <DataTable :value="album.tracks" data-key="id" size="small" :row-group-mode="album.discCount > 1 ? 'subheader' : undefined" group-rows-by="disc">
      <template #groupheader="{ data }"><span class="font-semibold">Disc {{ data.disc }}</span></template>
      <Column header="#" class="w-12 tabular-nums">
        <template #body="{ data }">
          <i v-if="isCurrent(data)" class="pi pi-volume-up text-primary" data-testid="track-current" />
          <template v-else>{{ data.number }}</template>
        </template>
      </Column>
      <Column header="Titel">
        <template #body="{ data }">
          <div data-testid="track-title" :class="isCurrent(data) && 'text-primary font-semibold'">{{ data.title }}</div>
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
      <Column class="w-40">
        <template #body="{ data }">
          <Button
            icon="pi pi-play"
            text
            rounded
            size="small"
            aria-label="Ab hier abspielen"
            title="Album ab hier abspielen"
            :disabled="!data.files.length"
            data-testid="play-track"
            @click="player.playAlbum(album, data.id)"
          />
          <Button
            icon="pi pi-plus"
            text
            rounded
            size="small"
            aria-label="Als Nächstes"
            title="Als Nächstes spielen"
            :disabled="!data.files.length"
            data-testid="play-next"
            @click="player.playNext(trackItems(data))"
          />
          <Button
            v-if="data.files.length"
            as="a"
            :href="api.downloadUrl(data.files[0].id)"
            download
            icon="pi pi-download"
            text
            rounded
            size="small"
            aria-label="Herunterladen"
            :title="`Originaldatei herunterladen (${data.files[0].format.toUpperCase()})`"
            data-testid="download-track"
          />
          <Button icon="pi pi-search" text rounded size="small" aria-label="Abgleichen" title="Bei AcoustID/MusicBrainz abgleichen" @click="openMatch(data)" />
        </template>
      </Column>
    </DataTable>
    <MatchDialog v-model:visible="matchVisible" :track="matchTrack" :file="matchFile" />
  </template>
</template>

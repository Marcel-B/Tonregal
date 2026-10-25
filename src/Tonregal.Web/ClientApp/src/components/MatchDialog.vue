<!-- SPDX-License-Identifier: AGPL-3.0-or-later -->
<script setup lang="ts">
import { ref, watch } from 'vue'
import Dialog from 'primevue/dialog'
import DataTable from 'primevue/datatable'
import Column from 'primevue/column'
import Message from 'primevue/message'
import ProgressSpinner from 'primevue/progressspinner'
import { api, formatDuration, type FileDto, type MatchResponse, type RecordingCandidate, type TrackDto } from '@/api'

/** Zeigt, was AcoustID und MusicBrainz zu einer Datei finden – ohne etwas zu ändern. */
const props = defineProps<{ track: TrackDto | null; file: FileDto | null }>()
const visible = defineModel<boolean>('visible', { default: false })

const result = ref<MatchResponse | null>(null)
const error = ref<string | null>(null)
const loading = ref(false)

watch(
  () => [visible.value, props.file?.id] as const,
  async ([open, fileId]) => {
    if (!open || fileId === undefined) return
    result.value = null
    error.value = null
    loading.value = true
    try {
      result.value = await api.candidates(fileId)
    } catch (e) {
      error.value = e instanceof Error ? e.message : String(e)
    } finally {
      loading.value = false
    }
  },
  { immediate: true },
)

// Ausgaben desselben Albums unterscheiden sich meist nur in Datum, Land und Medium.
const releaseDetails = (c: RecordingCandidate) =>
  [c.releaseArtist, c.releaseDate ?? c.year, c.country, c.mediumFormat].filter(Boolean).join(' · ')
const percent = (score: number) => `${Math.round(score * 100)} %`
const position = (c: { disc: number | null; trackNumber: number | null; trackCount: number | null }) =>
  c.trackNumber ? `${c.disc && c.disc > 1 ? `${c.disc}-` : ''}${c.trackNumber}${c.trackCount ? `/${c.trackCount}` : ''}` : ''
</script>

<template>
  <Dialog v-model:visible="visible" modal :header="`Abgleich: ${track?.title ?? ''}`" class="w-full max-w-4xl" :dismissable-mask="true">
    <p v-if="file" class="text-sm text-surface-500 mb-4 break-all">{{ file.path }}</p>
    <div v-if="loading" class="flex items-center gap-3 py-6">
      <ProgressSpinner style="width: 2rem; height: 2rem" stroke-width="6" />
      <span>Fingerprint berechnen und bei AcoustID/MusicBrainz nachschlagen …</span>
    </div>
    <Message v-else-if="error" severity="error">{{ error }}</Message>
    <template v-else-if="result">
      <Message v-if="result.candidates.length === 0" severity="info">AcoustID kennt diese Aufnahme nicht.</Message>
      <DataTable v-else :value="result.candidates" size="small" scrollable scroll-height="60vh">
        <Column header="Score" class="w-20 tabular-nums">
          <template #body="{ data }">{{ percent(data.score) }}</template>
        </Column>
        <Column header="Titel">
          <template #body="{ data }">
            <a :href="`https://musicbrainz.org/recording/${data.recordingId}`" target="_blank" rel="noopener" class="hover:underline" data-testid="candidate-title">{{ data.title }}</a>
            <div class="text-sm text-surface-500">{{ data.artists.join(', ') }}</div>
          </template>
        </Column>
        <Column header="Release">
          <template #body="{ data }">
            <a v-if="data.releaseId" :href="`https://musicbrainz.org/release/${data.releaseId}`" target="_blank" rel="noopener" class="hover:underline">{{ data.releaseTitle }}</a>
            <div class="text-sm text-surface-500">{{ releaseDetails(data) }}</div>
          </template>
        </Column>
        <Column header="Typ" class="w-28">
          <template #body="{ data }">{{ data.releaseType ?? '' }}<span v-if="data.releaseStatus && data.releaseStatus !== 'Official'" class="text-surface-500"> ({{ data.releaseStatus }})</span></template>
        </Column>
        <Column header="Track" class="w-20 tabular-nums">
          <template #body="{ data }">{{ position(data) }}</template>
        </Column>
        <Column header="Dauer" class="w-20 tabular-nums">
          <template #body="{ data }">{{ data.durationMs ? formatDuration(data.durationMs) : '' }}</template>
        </Column>
      </DataTable>
    </template>
  </Dialog>
</template>

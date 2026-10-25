<!-- SPDX-License-Identifier: AGPL-3.0-or-later -->
<script setup lang="ts">
import DataTable from 'primevue/datatable'
import Column from 'primevue/column'
import Button from 'primevue/button'
import ReleaseCover from '@/components/ReleaseCover.vue'
import { formatDuration, type RecordingCandidate } from '@/api'

/** Kandidaten aus AcoustID/MusicBrainz; der gewählte (falls angegeben) ist hervorgehoben. */
const props = defineProps<{
  candidates: RecordingCandidate[]
  selectedRecordingId?: string | null
  selectedReleaseId?: string | null
  /** Zeigt pro Zeile „Diesen nehmen“. */
  selectable?: boolean
}>()
const emit = defineEmits<{ select: [candidate: RecordingCandidate] }>()

const percent = (score: number) => `${Math.round(score * 100)} %`
const position = (c: RecordingCandidate) =>
  c.trackNumber ? `${c.disc && c.disc > 1 ? `${c.disc}-` : ''}${c.trackNumber}${c.trackCount ? `/${c.trackCount}` : ''}` : ''
// Ausgaben desselben Albums unterscheiden sich meist nur in Datum, Land und Medium.
const releaseDetails = (c: RecordingCandidate) =>
  [c.releaseArtist, c.releaseDate ?? c.year, c.country, c.mediumFormat].filter(Boolean).join(' · ')
const isSelected = (c: RecordingCandidate) =>
  !!props.selectedRecordingId && c.recordingId === props.selectedRecordingId && c.releaseId === props.selectedReleaseId
</script>

<template>
  <DataTable :value="candidates" size="small" scrollable scroll-height="60vh" :row-class="(c: RecordingCandidate) => (isSelected(c) ? 'font-semibold' : '')">
    <Column header="Score" class="w-20 tabular-nums">
      <template #body="{ data }">
        <i v-if="isSelected(data)" class="pi pi-check text-xs text-primary mr-1" title="gewählt" data-testid="selected" />{{ percent(data.score) }}
      </template>
    </Column>
    <Column header="Titel">
      <template #body="{ data }">
        <a :href="`https://musicbrainz.org/recording/${data.recordingId}`" target="_blank" rel="noopener" class="hover:underline" data-testid="candidate-title">{{ data.title }}</a>
        <div class="text-sm text-surface-500">{{ data.artists.join(', ') }}</div>
      </template>
    </Column>
    <Column header="Release">
      <template #body="{ data }">
        <div class="flex items-center gap-3">
          <ReleaseCover :release-id="data.releaseId" class="w-10 h-10 shrink-0" />
          <div>
            <a v-if="data.releaseId" :href="`https://musicbrainz.org/release/${data.releaseId}`" target="_blank" rel="noopener" class="hover:underline">{{ data.releaseTitle }}</a>
            <div class="text-sm text-surface-500">{{ releaseDetails(data) }}</div>
          </div>
        </div>
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
    <Column v-if="selectable" class="w-28">
      <template #body="{ data }">
        <Button v-if="!isSelected(data)" label="Nehmen" size="small" severity="secondary" data-testid="select-candidate" @click="emit('select', data)" />
      </template>
    </Column>
  </DataTable>
</template>

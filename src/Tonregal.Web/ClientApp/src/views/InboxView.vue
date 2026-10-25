<!-- SPDX-License-Identifier: AGPL-3.0-or-later -->
<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import DataTable from 'primevue/datatable'
import Column from 'primevue/column'
import Tag from 'primevue/tag'
import Button from 'primevue/button'
import Message from 'primevue/message'
import SelectButton from 'primevue/selectbutton'
import Dialog from 'primevue/dialog'
import CandidateTable from '@/components/CandidateTable.vue'
import { useStatusStore } from '@/stores/status'
import { api, formatDuration, type ImportJob, type ImportJobCandidates, type ImportState } from '@/api'

const status = useStatusStore()
const jobs = ref<ImportJob[]>([])
const error = ref<string | null>(null)
const loaded = ref(false)

async function load() {
  try {
    const response = await api.inbox()
    jobs.value = response.jobs
    status.inbox = response.status
    error.value = null
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  } finally {
    loaded.value = true
  }
}

// Jeder Fortschritt der Pipeline kommt per SignalR; die Liste kurz danach neu laden.
let reloadTimer: ReturnType<typeof setTimeout> | undefined
watch(
  () => status.inbox,
  () => {
    clearTimeout(reloadTimer)
    reloadTimer = setTimeout(load, 300)
  },
)
onMounted(load)
onUnmounted(() => clearTimeout(reloadTimer))

type Filter = 'all' | 'review' | 'matched' | 'pending'
const filter = ref<Filter>('all')
const stateFilter: Record<Filter, (s: ImportState) => boolean> = {
  all: () => true,
  review: (s) => s === 'NeedsReview',
  matched: (s) => s === 'Matched',
  pending: (s) => s === 'New' || s === 'Fingerprinted',
}
const filterOptions = computed(() =>
  (
    [
      ['all', 'Alle'],
      ['review', 'Prüfen'],
      ['matched', 'Erkannt'],
      ['pending', 'In Arbeit'],
    ] as const
  ).map(([value, label]) => ({ value, label: `${label} (${jobs.value.filter((j) => stateFilter[value](j.state)).length})` })),
)
const visibleJobs = computed(() => jobs.value.filter((j) => stateFilter[filter.value](j.state)))

const stateTag: Record<ImportState, { label: string; severity: string }> = {
  New: { label: 'In Arbeit', severity: 'secondary' },
  Fingerprinted: { label: 'In Arbeit', severity: 'secondary' },
  Matched: { label: 'Erkannt', severity: 'success' },
  NeedsReview: { label: 'Prüfen', severity: 'warn' },
  Imported: { label: 'Importiert', severity: 'info' },
  Rejected: { label: 'Verworfen', severity: 'danger' },
}

const fileName = (job: ImportJob) => job.path.slice(job.path.lastIndexOf('/') + 1)
const percent = (score: number) => `${Math.round(score * 100)} %`
const releaseLine = (job: ImportJob) => {
  const s = job.selected
  return s ? [s.releaseTitle, s.releaseDate ?? s.year, s.country, s.mediumFormat].filter(Boolean).join(' · ') : ''
}

const progress = computed(() => {
  const s = status.inbox
  if (!s) return ''
  if (s.running) return s.currentPath ? `Verarbeite ${s.currentPath} …` : 'Prüfe die Inbox …'
  return s.waiting ? `${s.waiting} Datei(en) werden noch kopiert` : ''
})

async function scan() {
  status.inbox = await api.scanInbox()
}

async function retry(job: ImportJob) {
  await api.retryImport(job.id)
  await load()
}

const candidatesFor = ref<ImportJob | null>(null)
const candidates = ref<ImportJobCandidates | null>(null)
const candidatesVisible = ref(false)

async function showCandidates(job: ImportJob) {
  candidatesFor.value = job
  candidates.value = null
  candidatesVisible.value = true
  candidates.value = await api.importCandidates(job.id)
}
</script>

<template>
  <div class="flex flex-wrap items-center gap-3 mb-6">
    <h1 class="text-2xl font-semibold mr-auto">Inbox</h1>
    <span v-if="progress" class="text-sm text-surface-500" data-testid="inbox-progress">{{ progress }}</span>
    <Button label="Inbox prüfen" icon="pi pi-refresh" severity="secondary" size="small" :loading="status.inbox?.running" @click="scan" />
  </div>

  <Message v-if="error" severity="error" class="mb-4">{{ error }}</Message>
  <Message v-if="status.inbox?.lastError" severity="warn" class="mb-4">{{ status.inbox.lastError }}</Message>

  <Message v-if="loaded && !jobs.length" severity="info">
    Die Inbox ist leer. Lege neue Alben als Ordner in <code>Musik/Inbox</code> auf der T7 ab, Tonregal erkennt sie dann automatisch.
  </Message>

  <template v-else-if="jobs.length">
    <SelectButton v-model="filter" :options="filterOptions" option-label="label" option-value="value" :allow-empty="false" class="mb-4" />
    <DataTable :value="visibleJobs" data-key="id" size="small" row-group-mode="subheader" group-rows-by="group">
      <template #groupheader="{ data }">
        <span class="font-semibold"><i class="pi pi-folder text-surface-500 mr-2" />{{ data.group || 'Ohne Ordner' }}</span>
      </template>
      <Column header="Status" class="w-28">
        <template #body="{ data }">
          <Tag :value="stateTag[data.state as ImportState].label" :severity="stateTag[data.state as ImportState].severity" data-testid="job-state" />
        </template>
      </Column>
      <Column header="Datei">
        <template #body="{ data }">
          <div :title="data.path">{{ data.tagTitle ?? fileName(data) }}</div>
          <div class="text-sm text-surface-500">
            {{ [data.tagArtist, data.tagAlbum].filter(Boolean).join(' · ') || fileName(data) }}
            <span v-if="data.durationMs"> · {{ formatDuration(data.durationMs) }}</span>
          </div>
        </template>
      </Column>
      <Column header="Treffer">
        <template #body="{ data }">
          <template v-if="data.selected">
            <div data-testid="job-match">{{ data.selected.title }} <span class="text-surface-500">· {{ data.selected.artists.join(', ') }}</span></div>
            <div class="text-sm text-surface-500">{{ releaseLine(data) }}</div>
          </template>
          <div v-if="data.reviewReason" class="text-sm text-orange-500" data-testid="job-reason">{{ data.reviewReason }}</div>
          <div v-if="data.error" class="text-sm text-red-500 break-all">{{ data.error }}</div>
        </template>
      </Column>
      <Column header="Score" class="w-20 tabular-nums">
        <template #body="{ data }">{{ data.matchScore != null ? percent(data.matchScore) : '' }}</template>
      </Column>
      <Column class="w-24">
        <template #body="{ data }">
          <div class="flex justify-end">
            <Button
              v-if="data.candidateCount"
              icon="pi pi-list"
              text
              rounded
              size="small"
              :aria-label="`${data.candidateCount} Kandidaten`"
              :title="`${data.candidateCount} Kandidaten`"
              @click="showCandidates(data)"
            />
            <Button v-if="data.error" icon="pi pi-replay" text rounded size="small" aria-label="Erneut versuchen" title="Erneut versuchen" @click="retry(data)" />
          </div>
        </template>
      </Column>
    </DataTable>
  </template>

  <Dialog
    v-model:visible="candidatesVisible"
    modal
    :header="`Kandidaten: ${candidatesFor?.tagTitle ?? (candidatesFor ? fileName(candidatesFor) : '')}`"
    class="w-full max-w-4xl"
    :dismissable-mask="true"
  >
    <p v-if="candidatesFor" class="text-sm text-surface-500 mb-4 break-all">{{ candidatesFor.path }}</p>
    <CandidateTable
      v-if="candidates"
      :candidates="candidates.candidates"
      :selected-recording-id="candidates.selectedRecordingId"
      :selected-release-id="candidates.selectedReleaseId"
    />
  </Dialog>
</template>

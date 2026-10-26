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
import InboxUpload from '@/components/InboxUpload.vue'
import ReleaseCover from '@/components/ReleaseCover.vue'
import { useStatusStore } from '@/stores/status'
import { api, formatDuration, type ImportJob, type ImportJobCandidates, type ImportState, type RecordingCandidate } from '@/api'

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
  // Die Liste zeigt nur automatisch verworfene Kopien von Dateien, die schon in der Library liegen.
  Rejected: { label: 'Schon da', severity: 'secondary' },
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
  if (s.running) {
    if (s.importsPending || s.imported) return `Übernehme in die Library … ${s.imported} fertig, ${s.importsPending} offen`
    return s.currentPath ? `Verarbeite ${s.currentPath} …` : 'Prüfe die Inbox …'
  }
  const parts = []
  if (s.imported) parts.push(`${s.imported} Datei(en) in die Library übernommen`)
  if (s.waiting) parts.push(`${s.waiting} Datei(en) werden noch kopiert`)
  return parts.join(' · ')
})

const matched = (group?: string) => jobs.value.filter((j) => j.state === 'Matched' && (group === undefined || j.group === group))
const actionError = ref<string | null>(null)

async function run(action: () => Promise<unknown>) {
  actionError.value = null
  try {
    await action()
  } catch (e) {
    actionError.value = e instanceof Error ? e.message : String(e)
  }
  await load()
}

const importJobs = (list: ImportJob[]) =>
  run(async () => {
    status.inbox = await api.importJobs(list.map((j) => j.id))
  })
// Im Review: den Vorschlag bestätigen und gleich übernehmen.
const accept = (job: ImportJob) =>
  run(async () => {
    await api.selectCandidate(job.id, job.selected!.recordingId, job.selected!.releaseId!)
    status.inbox = await api.importJobs([job.id])
  })
const reject = (job: ImportJob) => run(() => api.rejectImport(job.id))

async function scan() {
  status.inbox = await api.scanInbox()
}

const retry = (job: ImportJob) => run(() => api.retryImport(job.id))

const problems = computed(() => jobs.value.filter((j) => j.state === 'NeedsReview' || (j.error && j.state !== 'Rejected')))
const retryAll = () => run(() => api.retryAllImports())

// Bericht zum Weitergeben. Über http im Tailnet gibt es keine Clipboard-API, daher Textfeld plus execCommand.
const report = ref('')
const reportVisible = ref(false)
const reportField = ref<HTMLTextAreaElement>()
const copied = ref(false)
async function showReport() {
  copied.value = false
  report.value = 'Lade …'
  reportVisible.value = true
  try {
    report.value = await api.inboxReport()
  } catch (e) {
    report.value = e instanceof Error ? e.message : String(e)
  }
}
async function copyReport() {
  try {
    await navigator.clipboard.writeText(report.value)
  } catch {
    reportField.value?.select()
    document.execCommand('copy')
  }
  copied.value = true
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

async function select(candidate: RecordingCandidate) {
  const job = candidatesFor.value
  if (!job || !candidate.releaseId) return
  candidatesVisible.value = false
  await run(() => api.selectCandidate(job.id, candidate.recordingId, candidate.releaseId!))
}
</script>

<template>
  <div class="flex flex-wrap items-center gap-3 mb-6">
    <h1 class="text-2xl font-semibold mr-auto">Inbox</h1>
    <span v-if="progress" class="text-sm text-surface-500" data-testid="inbox-progress">{{ progress }}</span>
    <Button
      v-if="matched().length"
      :label="`Alle erkannten übernehmen (${matched().length})`"
      icon="pi pi-check"
      size="small"
      data-testid="import-all"
      @click="importJobs(matched())"
    />
    <Button
      v-if="problems.length"
      :label="`Fehler kopieren (${problems.length})`"
      icon="pi pi-copy"
      severity="secondary"
      size="small"
      data-testid="report"
      @click="showReport"
    />
    <Button
      v-if="problems.length"
      :label="`Alle erneut prüfen (${problems.length})`"
      icon="pi pi-replay"
      severity="secondary"
      size="small"
      data-testid="retry-all"
      @click="retryAll"
    />
    <Button label="Inbox prüfen" icon="pi pi-refresh" severity="secondary" size="small" :loading="status.inbox?.running" @click="scan" />
    <InboxUpload @uploaded="scan" />
  </div>
  <Message v-if="actionError" severity="error" class="mb-4">{{ actionError }}</Message>

  <Message v-if="error" severity="error" class="mb-4">{{ error }}</Message>
  <Message v-if="status.inbox?.lastError" severity="warn" class="mb-4">{{ status.inbox.lastError }}</Message>

  <Message v-if="loaded && !jobs.length" severity="info">
    Die Inbox ist leer. Zieh Album-Ordner oder Dateien einfach hierher, oder lege sie in <code>Musik/Inbox</code> auf der T7 ab.
    Tonregal erkennt sie dann automatisch.
  </Message>

  <template v-else-if="jobs.length">
    <SelectButton v-model="filter" :options="filterOptions" option-label="label" option-value="value" :allow-empty="false" class="mb-4" />
    <DataTable :value="visibleJobs" data-key="id" size="small" row-group-mode="subheader" group-rows-by="group">
      <template #groupheader="{ data }">
        <div class="flex items-center gap-3">
          <span class="font-semibold"><i class="pi pi-folder text-surface-500 mr-2" />{{ data.group || 'Ohne Ordner' }}</span>
          <Button
            v-if="matched(data.group).length"
            :label="`Ordner übernehmen (${matched(data.group).length})`"
            icon="pi pi-check"
            size="small"
            text
            data-testid="import-group"
            @click="importJobs(matched(data.group))"
          />
        </div>
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
          <div v-if="data.selected" class="flex items-center gap-3">
            <ReleaseCover :release-id="data.selected.releaseId" class="w-10 h-10 shrink-0" />
            <div>
              <div data-testid="job-match">{{ data.selected.title }} <span class="text-surface-500">· {{ data.selected.artists.join(', ') }}</span></div>
              <div class="text-sm text-surface-500">{{ releaseLine(data) }}</div>
            </div>
          </div>
          <div v-if="data.reviewReason" class="text-sm text-orange-500" data-testid="job-reason">{{ data.reviewReason }}</div>
          <div v-if="data.error" class="text-sm break-all" :class="data.state === 'Rejected' ? 'text-surface-500' : 'text-red-500'">{{ data.error }}</div>
        </template>
      </Column>
      <Column header="Score" class="w-20 tabular-nums">
        <template #body="{ data }">{{ data.matchScore != null ? percent(data.matchScore) : '' }}</template>
      </Column>
      <Column class="w-40">
        <template #body="{ data }">
          <div class="flex justify-end">
            <Button
              v-if="data.state === 'Matched'"
              icon="pi pi-check"
              text
              rounded
              size="small"
              aria-label="In die Library übernehmen"
              title="In die Library übernehmen"
              data-testid="import-job"
              @click="importJobs([data])"
            />
            <Button
              v-if="data.state === 'NeedsReview' && data.selected?.releaseId"
              icon="pi pi-check"
              text
              rounded
              size="small"
              severity="warn"
              aria-label="Vorschlag übernehmen"
              title="Vorschlag übernehmen"
              data-testid="accept-job"
              @click="accept(data)"
            />
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
            <Button v-if="data.state !== 'Rejected' && (data.error || data.state === 'NeedsReview')" icon="pi pi-replay" text rounded size="small" aria-label="Erneut abgleichen" title="Erneut abgleichen" @click="retry(data)" />
            <Button
              v-if="data.state !== 'Rejected'"
              icon="pi pi-times"
              text
              rounded
              size="small"
              severity="secondary"
              aria-label="Verwerfen"
              title="Verwerfen (Datei bleibt in der Inbox)"
              data-testid="reject-job"
              @click="reject(data)"
            />
          </div>
        </template>
      </Column>
    </DataTable>
  </template>

  <Dialog v-model:visible="reportVisible" modal header="Problemfälle" class="w-full max-w-4xl" :dismissable-mask="true">
    <textarea
      ref="reportField"
      :value="report"
      readonly
      rows="20"
      class="w-full font-mono text-xs p-3 rounded-md bg-surface-100 dark:bg-surface-800"
      data-testid="report-text"
    />
    <div class="flex items-center justify-end gap-3 mt-3">
      <span v-if="copied" class="text-sm text-green-500">Kopiert, einfach in den Chat einfügen</span>
      <Button label="Kopieren" icon="pi pi-copy" size="small" @click="copyReport" />
    </div>
  </Dialog>

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
      selectable
      :confirm-selected="candidatesFor?.state === 'NeedsReview'"
      @select="select"
    />
  </Dialog>
</template>

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
import {
  api,
  formatDuration,
  type ImportJob,
  type ImportJobCandidates,
  type ImportSource,
  type ImportState,
  type LegacySummary,
  type RecordingCandidate,
} from '@/api'

/** Inbox (Dateien werden verschoben) oder Altbestand (Massenlauf, Dateien werden nur kopiert). */
const props = withDefaults(defineProps<{ source?: ImportSource }>(), { source: 'Inbox' })
const isLegacy = computed(() => props.source === 'Legacy')

const status = useStatusStore()
const jobs = ref<ImportJob[]>([])
const legacy = ref<LegacySummary | null>(null)
const error = ref<string | null>(null)
const loaded = ref(false)

async function load() {
  try {
    const response = await api.inbox(props.source)
    jobs.value = response.jobs
    status.inbox = response.status
    legacy.value = isLegacy.value ? await api.legacy() : null
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
watch(() => props.source, () => {
  filter.value = 'all'
  load()
})
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
// Kein Treffer passt: mit Titel, Interpret und Album aus den Tags übernehmen, ohne Album ins Sammelalbum.
const importUnmatched = (job: ImportJob) => run(() => api.importUnmatched(job.id))
const unmatched = (group: string) => jobs.value.filter((j) => j.state === 'NeedsReview' && !j.selected && j.group === group)
const importGroupUnmatched = (group: string) =>
  run(async () => {
    for (const job of unmatched(group)) await api.importUnmatched(job.id)
  })
const startLegacy = () => run(() => api.scanLegacy())
const reject = (job: ImportJob) => run(() => api.rejectImport(job.id))

async function scan() {
  status.inbox = await api.scanInbox()
}

const retry = (job: ImportJob) => run(() => api.retryImport(job.id))

const problems = computed(() => jobs.value.filter((j) => j.state === 'NeedsReview' || (j.error && j.state !== 'Rejected')))
const retryAll = () => run(() => api.retryAllImports(props.source))

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
    report.value = await api.inboxReport(props.source)
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

// Tastenkürzel für lange Prüflisten: j/k oder Pfeile wählen eine Zeile, dann
// Enter übernehmen, l Kandidaten, u ohne Treffer, r erneut abgleichen, x verwerfen.
const focusedId = ref<number | null>(null)
const focused = computed(() => visibleJobs.value.find((j) => j.id === focusedId.value) ?? null)
function move(step: number) {
  const list = visibleJobs.value
  if (!list.length) return
  const index = list.findIndex((j) => j.id === focusedId.value)
  const next = index < 0 ? 0 : Math.min(list.length - 1, Math.max(0, index + step))
  focusedId.value = list[next]!.id
  requestAnimationFrame(() => document.querySelector('.focused-row')?.scrollIntoView?.({ block: 'nearest' }))
}
// Nach einer Aktion verschwindet die Zeile meist; dann rückt die nächste nach.
watch(visibleJobs, (list, before) => {
  if (focusedId.value === null || list.some((j) => j.id === focusedId.value)) return
  const index = before.findIndex((j) => j.id === focusedId.value)
  const next = list[Math.min(Math.max(index, 0), list.length - 1)]
  focusedId.value = next?.id ?? null
})
function onKey(event: KeyboardEvent) {
  if (event.metaKey || event.ctrlKey || event.altKey || candidatesVisible.value || reportVisible.value) return
  if (event.target instanceof HTMLElement && event.target.closest('input, textarea, select, [contenteditable]')) return
  const job = focused.value
  const actions: Record<string, () => unknown> = {
    j: () => move(1),
    ArrowDown: () => move(1),
    k: () => move(-1),
    ArrowUp: () => move(-1),
    Enter: () => job && (job.state === 'Matched' ? importJobs([job]) : canAccept(job) && accept(job)),
    l: () => job?.candidateCount && showCandidates(job),
    u: () => job?.state === 'NeedsReview' && importUnmatched(job),
    r: () => job && job.state !== 'Rejected' && retry(job),
    x: () => job && job.state !== 'Rejected' && reject(job),
  }
  const action = actions[event.key]
  if (!action) return
  event.preventDefault()
  action()
}
onMounted(() => window.addEventListener('keydown', onKey))
onUnmounted(() => window.removeEventListener('keydown', onKey))
const canAccept = (job: ImportJob) => job.state === 'NeedsReview' && !!job.selected?.releaseId

async function select(candidate: RecordingCandidate) {
  const job = candidatesFor.value
  if (!job || !candidate.releaseId) return
  candidatesVisible.value = false
  await run(() => api.selectCandidate(job.id, candidate.recordingId, candidate.releaseId!))
}
</script>

<template>
  <div class="flex flex-wrap items-center gap-3 mb-6">
    <h1 class="text-2xl font-semibold mr-auto">{{ isLegacy ? 'Altbestand' : 'Inbox' }}</h1>
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
    <template v-if="isLegacy">
      <Button
        label="Altbestand einlesen"
        icon="pi pi-play"
        size="small"
        :loading="status.inbox?.running"
        :disabled="legacy?.exists === false"
        data-testid="start-legacy"
        @click="startLegacy"
      />
    </template>
    <template v-else>
      <Button label="Inbox prüfen" icon="pi pi-refresh" severity="secondary" size="small" :loading="status.inbox?.running" @click="scan" />
      <InboxUpload @uploaded="scan" />
    </template>
  </div>
  <!-- Eigene Zeile mit fester Höhe, damit lange Pfade die Knöpfe nicht verschieben. -->
  <div class="-mt-4 mb-4 h-5 truncate text-sm text-surface-500" :title="progress" data-testid="inbox-progress">{{ progress }}</div>
  <div v-if="isLegacy && legacy" class="mb-6 text-sm text-surface-500" data-testid="legacy-summary">
    <p v-if="!legacy.exists">
      Den Ordner <code>{{ legacy.path }}</code> gibt es nicht. Leg die MP3-Kopien dort ab oder trag den Pfad als
      <code>Tonregal:LegacyPath</code> in <code>appsettings.Production.json</code> ein.
    </p>
    <p v-else>
      <code>{{ legacy.path }}</code> · {{ legacy.total }} Dateien eingelesen: {{ legacy.imported }} übernommen,
      {{ legacy.alreadyInLibrary }} schon da, {{ legacy.needsReview }} zu prüfen, {{ legacy.pending }} offen.
      Erkannte Dateien werden ordnerweise automatisch in die Library kopiert, die Originale bleiben unverändert.
    </p>
  </div>
  <Message v-if="actionError" severity="error" class="mb-4">{{ actionError }}</Message>

  <Message v-if="error" severity="error" class="mb-4">{{ error }}</Message>
  <Message v-if="status.inbox?.lastError" severity="warn" class="mb-4">{{ status.inbox.lastError }}</Message>

  <Message v-if="loaded && !jobs.length && isLegacy" severity="info">Gerade ist nichts zu prüfen.</Message>
  <Message v-else-if="loaded && !jobs.length" severity="info">
    Die Inbox ist leer. Zieh Album-Ordner oder Dateien einfach hierher, oder lege sie in <code>Musik/Inbox</code> auf der T7 ab.
    Tonregal erkennt sie dann automatisch.
  </Message>

  <template v-else-if="jobs.length">
    <div class="flex flex-wrap items-center gap-4 mb-4">
      <SelectButton v-model="filter" :options="filterOptions" option-label="label" option-value="value" :allow-empty="false" />
      <span class="text-xs text-surface-500" data-testid="shortcuts">
        Tasten: <kbd>j</kbd>/<kbd>k</kbd> Zeile · <kbd>Enter</kbd> übernehmen · <kbd>l</kbd> Kandidaten · <kbd>u</kbd> ohne Treffer ·
        <kbd>r</kbd> neu abgleichen · <kbd>x</kbd> verwerfen
      </span>
    </div>
    <DataTable
      :value="visibleJobs"
      data-key="id"
      size="small"
      row-group-mode="subheader"
      group-rows-by="group"
      :row-class="(job: ImportJob) => (job.id === focusedId ? 'focused-row !bg-primary-50 dark:!bg-primary-900/30' : '')"
      @row-click="(e: { data: ImportJob }) => (focusedId = e.data.id)"
    >
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
          <Button
            v-if="unmatched(data.group).length"
            :label="`Ohne Treffer übernehmen (${unmatched(data.group).length})`"
            icon="pi pi-folder-open"
            size="small"
            text
            severity="secondary"
            title="Dateien ohne Vorschlag mit Titel, Interpret und Album aus den Tags übernehmen"
            data-testid="import-group-unmatched"
            @click="importGroupUnmatched(data.group)"
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
              v-if="data.state === 'NeedsReview'"
              icon="pi pi-folder-open"
              text
              rounded
              size="small"
              severity="secondary"
              aria-label="Ohne Treffer übernehmen"
              title="Ohne Treffer übernehmen: Album aus den Tags, sonst „Unbekanntes Album“ (u)"
              data-testid="import-unmatched"
              @click="importUnmatched(data)"
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
              :title="isLegacy ? 'Verwerfen (Datei bleibt im Altbestand)' : 'Verwerfen (Datei bleibt in der Inbox)'"
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

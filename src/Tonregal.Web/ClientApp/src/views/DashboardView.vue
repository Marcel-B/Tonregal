<!-- SPDX-License-Identifier: AGPL-3.0-or-later -->
<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import Button from 'primevue/button'
import Card from 'primevue/card'
import Message from 'primevue/message'
import Tag from 'primevue/tag'
import { api, formatBytes, type BackupRun, type HealthResponse, type LibraryStats } from '@/api'
import { useStatusStore } from '@/stores/status'

const store = useStatusStore()
const health = ref<HealthResponse | null>(null)
const stats = ref<LibraryStats | null>(null)
const runs = ref<BackupRun[]>([])
const error = ref<string | null>(null)

async function loadBackup() {
  const backup = await api.backup()
  store.backup = backup.status
  runs.value = backup.runs
}

onMounted(async () => {
  try {
    ;[health.value, stats.value] = await Promise.all([api.health(), api.stats(), loadBackup()])
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  }
})

// Nach einem Lauf die Historie neu laden.
watch(
  () => store.backup?.lastRun?.id,
  (id, old) => {
    if (id !== undefined && old !== undefined && id !== old) loadBackup().catch(() => {})
  },
)

const analyzedShare = computed(() =>
  stats.value?.files ? `${Math.floor((stats.value.analyzedFiles / stats.value.files) * 100)} %` : '–',
)

const tiles = computed<{ key: string; label: string; value: string | number | undefined }[]>(() => [
  { key: 'artists', label: 'Künstler', value: stats.value?.artists },
  { key: 'albums', label: 'Alben', value: stats.value?.albums },
  { key: 'tracks', label: 'Tracks', value: stats.value?.tracks },
  { key: 'libraryBytes', label: 'Bibliotheksgröße', value: stats.value ? formatBytes(stats.value.libraryBytes) : undefined },
  { key: 'analyzed', label: 'Analysiert', value: stats.value ? analyzedShare.value : undefined },
  { key: 'openReviews', label: 'Offene Reviews', value: stats.value?.openReviews },
  { key: 'openDuplicates', label: 'Offene Dubletten', value: stats.value?.openDuplicates },
  { key: 'files', label: 'Dateien', value: stats.value?.files },
])

const backup = computed(() => store.backup)
// Während ein neuer Lauf läuft, nicht mehr warnen – der kann es ja schon richten.
const failing = computed(() => (backup.value?.consecutiveFailures ?? 0) >= 2 && !backup.value?.running)
// Nur die letzte Zeile des Logs, ohne die langen WebDAV-URLs; das ganze Log steht in der Historie.
const failureReason = computed(() => {
  const lines = (backup.value?.lastRun?.log ?? '').split('\n').filter((l) => l.trim())
  return lines.at(-1)?.replace(/https?:\/\/\S+/g, '…') ?? ''
})
const expanded = ref<number | null>(null)
const toggleLog = (run: BackupRun) => (expanded.value = expanded.value === run.id || !run.log ? null : run.id)
const dateTime = (iso: string) => new Date(iso).toLocaleString('de-DE', { dateStyle: 'short', timeStyle: 'short' })
const duration = (run: BackupRun) => {
  if (!run.finishedAt) return ''
  const minutes = Math.round((Date.parse(run.finishedAt) - Date.parse(run.startedAt)) / 60000)
  return minutes < 60 ? `${minutes} min` : `${Math.floor(minutes / 60)} h ${minutes % 60} min`
}
const states = {
  Running: { label: 'Läuft', severity: 'info' },
  Succeeded: { label: 'OK', severity: 'success' },
  Failed: { label: 'Fehler', severity: 'danger' },
} as const

async function call(action: () => Promise<unknown>) {
  try {
    await action()
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  }
}

const runBackup = () => call(async () => (store.backup = { ...(await api.runBackup()), running: true }))
const cancelBackup = () => call(() => api.cancelBackup())
</script>

<template>
  <h1 class="text-2xl font-semibold mb-6">Übersicht</h1>
  <Message v-if="error" severity="error" class="mb-4">{{ error }}</Message>
  <Message v-else-if="health && !health.libraryMounted" severity="warn" class="mb-4">
    Mount-Marker nicht gefunden – ist die SSD gemountet?
  </Message>
  <div class="grid grid-cols-2 lg:grid-cols-4 gap-4">
    <Card v-for="tile in tiles" :key="tile.key">
      <template #subtitle>{{ tile.label }}</template>
      <template #content>
        <span class="text-3xl font-semibold tabular-nums" :data-testid="tile.key">{{ tile.value ?? '–' }}</span>
      </template>
    </Card>
  </div>

  <section class="mt-10" data-testid="backup">
    <div class="flex flex-wrap items-center gap-3 mb-3">
      <h2 class="text-xl font-semibold mr-auto">Backup nach Nextcloud</h2>
      <Button
        v-if="backup?.running"
        label="Abbrechen"
        icon="pi pi-stop"
        size="small"
        severity="secondary"
        data-testid="cancel-backup"
        @click="cancelBackup"
      />
      <Button v-else label="Jetzt sichern" icon="pi pi-cloud-upload" size="small" severity="secondary" data-testid="run-backup" @click="runBackup" />
    </div>
    <Message v-if="failing" severity="error" class="mb-3" data-testid="backup-failing">
      <div>Die letzten {{ backup?.consecutiveFailures }} Backups sind fehlgeschlagen. Details per Klick in der Historie.</div>
      <div v-if="failureReason" class="text-sm truncate" :title="failureReason">{{ failureReason }}</div>
    </Message>
    <p class="text-sm text-surface-500 mb-4" data-testid="backup-summary">
      <template v-if="backup?.running">{{ backup.step ?? 'Läuft' }} …</template>
      <template v-else>
        Letztes erfolgreiches Backup: {{ backup?.lastSuccessAt ? dateTime(backup.lastSuccessAt) : 'noch keins' }}<template
          v-if="backup?.nextRunAt"
        >
          · nächstes {{ dateTime(backup.nextRunAt) }}</template
        >
      </template>
    </p>
    <table v-if="runs.length" class="w-full text-sm">
      <thead class="text-left text-surface-500">
        <tr>
          <th class="py-1 font-normal">Start</th>
          <th class="py-1 font-normal">Status</th>
          <th class="py-1 font-normal text-right">Dateien</th>
          <th class="py-1 font-normal text-right">Übertragen</th>
          <th class="py-1 font-normal text-right">Dauer</th>
        </tr>
      </thead>
      <tbody>
        <template v-for="run in runs" :key="run.id">
          <tr
            class="border-t border-surface-200 dark:border-surface-700"
            :class="{ 'cursor-pointer': run.log }"
            data-testid="backup-run"
            @click="toggleLog(run)"
          >
            <td class="py-1.5">{{ dateTime(run.startedAt) }}</td>
            <td class="py-1.5"><Tag :value="states[run.status].label" :severity="states[run.status].severity" /></td>
            <td class="py-1.5 text-right tabular-nums">{{ run.filesSynced }}</td>
            <td class="py-1.5 text-right tabular-nums">{{ formatBytes(run.bytesSynced) }}</td>
            <td class="py-1.5 text-right tabular-nums">{{ duration(run) }}</td>
          </tr>
          <tr v-if="expanded === run.id">
            <td colspan="5" class="pb-2">
              <pre class="text-xs whitespace-pre-wrap break-all max-h-60 overflow-auto bg-surface-100 dark:bg-surface-800 rounded p-2" data-testid="backup-log">{{ run.log }}</pre>
            </td>
          </tr>
        </template>
      </tbody>
    </table>
  </section>

  <p v-if="health" class="mt-6 text-sm text-surface-500">Version {{ health.version }}</p>
</template>

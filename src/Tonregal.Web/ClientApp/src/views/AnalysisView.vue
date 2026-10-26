<!-- SPDX-License-Identifier: AGPL-3.0-or-later -->
<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import Button from 'primevue/button'
import Message from 'primevue/message'
import ProgressBar from 'primevue/progressbar'
import { api, type AnalysisSettings } from '@/api'
import { useStatusStore } from '@/stores/status'

const store = useStatusStore()
const settings = ref<AnalysisSettings | null>(null)
const error = ref<string | null>(null)

async function run(action: () => Promise<unknown>) {
  error.value = null
  try {
    await action()
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  }
}

onMounted(() =>
  run(async () => {
    const response = await api.analysis()
    settings.value = response.settings
    store.analysis = response.status
  }),
)

const status = computed(() => store.analysis)
const counts = computed(() => status.value?.counts ?? null)
const percent = computed(() => (counts.value?.files ? Math.floor((counts.value.analyzed / counts.value.files) * 100) : 0))
const active = computed(() => !!status.value && (status.value.running || status.value.requested))

const schedule = computed(() => {
  const s = settings.value
  if (!s) return ''
  if (!s.runAtNight) return 'Die Analyse startet nur per Knopf.'
  const busy = s.busyCheck ? ', solange kein KI-Job läuft' : ''
  return `Ohne Knopf läuft sie jede Nacht von ${s.nightStart} bis ${s.nightEnd}${busy}.`
})

const start = () => run(async () => (store.analysis = await api.startAnalysis()))
const stop = () => run(async () => (store.analysis = await api.stopAnalysis()))
</script>

<template>
  <div class="flex flex-wrap items-center gap-3 mb-6">
    <h1 class="text-2xl font-semibold mr-auto">Analyse</h1>
    <Button v-if="active" label="Anhalten" icon="pi pi-pause" severity="secondary" size="small" data-testid="stop-analysis" @click="stop" />
    <Button v-else label="Jetzt analysieren" icon="pi pi-play" size="small" :disabled="counts?.pending === 0" data-testid="start-analysis" @click="start" />
  </div>

  <p class="mb-6 text-sm text-surface-500">
    Ermittelt je Datei BPM und Tonart (librosa) und je Album die Lautheit (ReplayGain per rsgain) und schreibt sie in die Tags.
    Vorhandene BPM- und Tonart-Werte bleiben unverändert. {{ schedule }}
  </p>

  <Message v-if="error" severity="error" class="mb-4">{{ error }}</Message>
  <Message v-if="status?.lastError" severity="warn" class="mb-4" data-testid="analysis-error">{{ status.lastError }}</Message>

  <div v-if="counts" class="mb-6">
    <div class="flex items-baseline justify-between mb-2 text-sm">
      <span data-testid="analysis-counts">
        {{ counts.analyzed }} von {{ counts.files }} Dateien analysiert<template v-if="counts.withoutResult">, {{ counts.withoutResult }} ohne Ergebnis</template>
      </span>
      <span class="tabular-nums text-surface-500">{{ percent }} %</span>
    </div>
    <ProgressBar :value="percent" :show-value="false" class="h-2" />
  </div>

  <p class="mb-6 text-sm h-5 truncate" data-testid="analysis-state">
    <template v-if="status?.running && status.currentAlbum">Zuletzt fertig: {{ status.currentAlbum }}</template>
    <template v-else-if="status?.running">Analysiere …</template>
    <template v-else-if="status?.waiting">{{ status.waiting }}</template>
  </p>

  <template v-if="status?.recentErrors.length">
    <h2 class="text-lg font-semibold mb-2">Zuletzt nicht analysierbar</h2>
    <ul class="text-sm text-surface-500 space-y-1 break-all" data-testid="analysis-recent-errors">
      <li v-for="line in status.recentErrors" :key="line">{{ line }}</li>
    </ul>
  </template>
</template>

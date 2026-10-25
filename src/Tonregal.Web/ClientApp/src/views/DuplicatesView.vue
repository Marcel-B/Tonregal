<!-- SPDX-License-Identifier: AGPL-3.0-or-later -->
<script setup lang="ts">
import { computed, onMounted, onUnmounted, reactive, ref, watch } from 'vue'
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import Message from 'primevue/message'
import Tag from 'primevue/tag'
import { useStatusStore } from '@/stores/status'
import { api, formatDuration, type DuplicateFile, type DuplicateGroup, type DuplicateKind } from '@/api'

const status = useStatusStore()
const groups = ref<DuplicateGroup[]>([])
const loaded = ref(false)
const error = ref<string | null>(null)
/** Pro Gruppe die Dateien, die bleiben sollen; anfangs der Vorschlag. */
const keep = reactive<Record<number, number[]>>({})

async function load() {
  try {
    groups.value = await api.duplicates()
    for (const g of groups.value) keep[g.id] ??= g.suggestedFileId ? [g.suggestedFileId] : []
    error.value = null
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  } finally {
    loaded.value = true
  }
}
onMounted(load)

// Die Suche läuft nach jedem Library-Scan; ist er fertig, neu laden.
watch(
  () => status.scan?.running,
  (running, before) => {
    if (before && !running) void load()
  },
)

const scanning = computed(() => !!status.scan?.running)
async function scan() {
  status.scan = await api.scanDuplicates()
}

const kinds: Record<DuplicateKind, { label: string; hint: string; severity: string }> = {
  ExactHash: { label: 'Identische Datei', hint: 'Byte für Byte gleich', severity: 'danger' },
  Fingerprint: { label: 'Gleiche Aufnahme', hint: 'Gleiche MusicBrainz-ID oder AcoustID', severity: 'warn' },
  Metadata: { label: 'Wahrscheinlich gleich', hint: 'Interpret, Titel und Dauer stimmen überein; vor dem Löschen anhören', severity: 'info' },
}

const lossless = new Set(['Flac', 'Alac', 'Wav'])
const quality = (f: DuplicateFile) =>
  [f.format.toUpperCase(), lossless.has(f.format) ? null : `${f.bitrate} kbps`, `${(f.sampleRate / 1000).toFixed(1)} kHz`].filter(Boolean).join(' · ')
const size = (bytes: number) => `${(bytes / 1024 / 1024).toFixed(1)} MB`

const busy = ref<number | null>(null)
const actionError = ref<string | null>(null)
async function resolve(group: DuplicateGroup, ids: number[]) {
  stop()
  busy.value = group.id
  actionError.value = null
  try {
    await api.resolveDuplicate(group.id, ids)
    delete keep[group.id]
    await load()
  } catch (e) {
    actionError.value = e instanceof Error ? e.message : String(e)
  } finally {
    busy.value = null
  }
}
const trashCount = (group: DuplicateGroup) => group.files.length - (keep[group.id]?.length ?? 0)

// Vorhören: 10 Sekunden ab etwa einem Drittel, nicht vom stillen Anfang.
const audio = new Audio()
const playing = ref<number | null>(null)
let stopTimer: ReturnType<typeof setTimeout> | undefined
function stop() {
  clearTimeout(stopTimer)
  audio.pause()
  playing.value = null
}
function preview(file: DuplicateFile) {
  if (playing.value === file.id) return stop()
  stop()
  playing.value = file.id
  audio.src = api.audioUrl(file.id)
  audio.onloadedmetadata = () => {
    audio.currentTime = Math.min(30, audio.duration / 3 || 0)
    void audio.play().catch(stop)
    stopTimer = setTimeout(stop, 10_000)
  }
  audio.onerror = stop
}
onUnmounted(stop)
</script>

<template>
  <div class="flex flex-wrap items-center gap-3 mb-6">
    <h1 class="text-2xl font-semibold mr-auto">Dubletten</h1>
    <span v-if="groups.length" class="text-sm text-surface-500">{{ groups.length }} offen</span>
    <Button label="Neu suchen" icon="pi pi-refresh" severity="secondary" size="small" :loading="scanning" @click="scan" />
  </div>
  <Message v-if="error" severity="error" class="mb-4">{{ error }}</Message>
  <Message v-if="actionError" severity="error" class="mb-4">{{ actionError }}</Message>
  <Message v-if="loaded && !groups.length && !error" severity="success">
    Keine offenen Dubletten. Die Suche läuft nach jedem Library-Scan automatisch.
  </Message>

  <div class="flex flex-col gap-6">
    <section
      v-for="group in groups"
      :key="group.id"
      class="rounded-lg border border-surface-200 dark:border-surface-700 p-4"
      data-testid="duplicate-group"
    >
      <div class="flex flex-wrap items-center gap-3 mb-3">
        <Tag :value="kinds[group.kind].label" :severity="kinds[group.kind].severity" />
        <span class="text-sm text-surface-500">{{ kinds[group.kind].hint }}</span>
      </div>

      <div class="flex flex-col divide-y divide-surface-200 dark:divide-surface-700">
        <label v-for="file in group.files" :key="file.id" class="flex items-center gap-3 py-2 cursor-pointer" data-testid="duplicate-file">
          <Checkbox v-model="keep[group.id]" :value="file.id" :input-id="`keep-${file.id}`" />
          <Button
            :icon="playing === file.id ? 'pi pi-stop' : 'pi pi-play'"
            text
            rounded
            size="small"
            :aria-label="playing === file.id ? 'Anhalten' : '10 Sekunden anhören'"
            :title="playing === file.id ? 'Anhalten' : '10 Sekunden anhören'"
            data-testid="preview"
            @click.prevent="preview(file)"
          />
          <div class="min-w-0 flex-1">
            <div class="flex flex-wrap items-baseline gap-x-2">
              <span class="font-medium">{{ file.title }}</span>
              <span class="text-surface-500">{{ file.artists.join(', ') }}</span>
              <Tag v-if="file.id === group.suggestedFileId" value="Vorschlag" severity="success" class="text-xs" />
            </div>
            <div class="text-sm text-surface-500">
              <RouterLink :to="{ name: 'album', params: { id: file.albumId } }" class="hover:underline" @click.stop>{{ file.album }}</RouterLink>
              <span v-if="file.year"> · {{ file.year }}</span>
              · {{ formatDuration(file.durationMs) }} · {{ quality(file) }} · {{ size(file.sizeBytes) }}
              <span v-if="file.hasMusicBrainzId"> · MusicBrainz</span>
            </div>
            <div class="text-xs text-surface-400 break-all">{{ file.path }}</div>
          </div>
        </label>
      </div>

      <div class="flex flex-wrap justify-end gap-2 mt-3">
        <Button
          label="Alle behalten"
          severity="secondary"
          size="small"
          text
          :disabled="busy === group.id"
          data-testid="keep-all"
          @click="resolve(group, group.files.map((f) => f.id))"
        />
        <Button
          :label="`Auswahl behalten, ${trashCount(group)} in den Papierkorb`"
          icon="pi pi-trash"
          size="small"
          :disabled="!keep[group.id]?.length || trashCount(group) === 0"
          :loading="busy === group.id"
          data-testid="resolve"
          @click="resolve(group, keep[group.id] ?? [])"
        />
      </div>
    </section>
  </div>
  <p v-if="groups.length" class="text-sm text-surface-500 mt-6">
    Verworfene Dateien liegen 30 Tage in <code>Library/.trash</code>, danach werden sie gelöscht.
  </p>
</template>

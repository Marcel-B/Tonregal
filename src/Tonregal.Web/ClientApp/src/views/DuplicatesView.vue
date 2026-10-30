<!-- SPDX-License-Identifier: AGPL-3.0-or-later -->
<script setup lang="ts">
import { computed, onMounted, onUnmounted, reactive, ref, watch } from 'vue'
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import Dialog from 'primevue/dialog'
import Message from 'primevue/message'
import Tag from 'primevue/tag'
import { useStatusStore } from '@/stores/status'
import { api, formatDuration, type DuplicateFile, type DuplicateGroup, type DuplicateKind, type ResolveSuggestedResult } from '@/api'

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

// Alle Vorschläge auf einmal. Gruppen, deren Auswahl auf der Seite geändert wurde, bleiben draußen:
// Dort zählt die eigene Entscheidung, nicht der Vorschlag. „Wahrscheinlich gleich“ sollte man vorher
// anhören, deshalb nur auf ausdrücklichen Wunsch.
const followsSuggestion = (g: DuplicateGroup) =>
  g.suggestedFileId != null && keep[g.id]?.length === 1 && keep[g.id]![0] === g.suggestedFileId
const changedGroups = computed(() => groups.value.filter((g) => g.suggestedFileId != null && !followsSuggestion(g)).length)
const metadataGroups = computed(() => groups.value.filter((g) => g.kind === 'Metadata' && followsSuggestion(g)).length)
const includeMetadata = ref(false)
const bulkGroups = computed(() =>
  groups.value.filter((g) => followsSuggestion(g) && (includeMetadata.value || g.kind !== 'Metadata')),
)
const bulkTrash = computed(() => bulkGroups.value.reduce((n, g) => n + g.files.length - 1, 0))
const bulkVisible = ref(false)
const bulkBusy = ref(false)
const bulkResult = ref<ResolveSuggestedResult | null>(null)
const bulkError = ref<string | null>(null)

function openBulk() {
  stop()
  includeMetadata.value = false
  bulkResult.value = null
  bulkError.value = null
  bulkVisible.value = true
}
async function resolveAll() {
  const ids = bulkGroups.value.map((g) => g.id)
  if (!ids.length) return
  bulkBusy.value = true
  bulkError.value = null
  try {
    bulkResult.value = await api.resolveSuggested(ids)
    for (const id of ids) delete keep[id]
    await load()
  } catch (e) {
    bulkError.value = e instanceof Error ? e.message : String(e)
  } finally {
    bulkBusy.value = false
  }
}

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
    <Button
      v-if="groups.some((g) => g.suggestedFileId != null)"
      label="Alle Vorschläge übernehmen"
      icon="pi pi-check-square"
      size="small"
      :disabled="busy !== null"
      data-testid="resolve-all"
      @click="openBulk"
    />
    <Button label="Neu suchen" icon="pi pi-refresh" severity="secondary" size="small" :loading="scanning" @click="scan" />
  </div>

  <Dialog v-model:visible="bulkVisible" modal header="Alle Vorschläge übernehmen" class="w-full max-w-xl" :dismissable-mask="!bulkBusy">
    <Message v-if="bulkError" severity="error" class="mb-4">{{ bulkError }}</Message>
    <template v-if="bulkResult">
      <Message severity="success" class="mb-4" data-testid="resolve-all-result">
        {{ bulkResult.resolved }} Gruppe(n) entschieden, {{ bulkResult.trashed }} Datei(en) liegen jetzt in <code>Library/.trash</code>.
      </Message>
      <details v-if="bulkResult.skipped.length" class="text-sm">
        <summary class="cursor-pointer text-surface-500">{{ bulkResult.skipped.length }} Gruppe(n) übersprungen</summary>
        <ul class="mt-2">
          <li v-for="s in bulkResult.skipped" :key="s.groupId" class="py-0.5">Gruppe {{ s.groupId }} · {{ s.reason }}</li>
        </ul>
      </details>
    </template>
    <template v-else>
      <p class="mb-3" data-testid="resolve-all-summary">
        In <strong>{{ bulkGroups.length }} Gruppe(n)</strong> bleibt jeweils die vorgeschlagene Datei,
        <strong>{{ bulkTrash }} Datei(en)</strong> wandern in den Papierkorb (<code>Library/.trash</code>, 30 Tage).
      </p>
      <label v-if="metadataGroups" class="flex items-start gap-2 mb-3 text-sm cursor-pointer">
        <Checkbox v-model="includeMetadata" binary input-id="include-metadata" data-testid="include-metadata" />
        <span>
          Auch {{ metadataGroups }} Gruppe(n) „Wahrscheinlich gleich“ übernehmen. Die sind nur über Interpret, Titel und Dauer
          gefunden; besser vorher anhören.
        </span>
      </label>
      <p v-if="changedGroups" class="text-sm text-surface-500 mb-3">
        {{ changedGroups }} Gruppe(n) mit geänderter Auswahl bleiben unberührt; die entscheidest du einzeln.
      </p>
      <p class="text-sm text-surface-500">Der Server prüft jede Gruppe vor dem Verschieben noch einmal.</p>
    </template>
    <template #footer>
      <Button :label="bulkResult ? 'Schließen' : 'Abbrechen'" severity="secondary" text :disabled="bulkBusy" @click="bulkVisible = false" />
      <Button
        v-if="!bulkResult"
        :label="`${bulkGroups.length} übernehmen`"
        icon="pi pi-trash"
        :disabled="!bulkGroups.length"
        :loading="bulkBusy"
        data-testid="resolve-all-confirm"
        @click="resolveAll"
      />
    </template>
  </Dialog>
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

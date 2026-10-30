<!-- SPDX-License-Identifier: AGPL-3.0-or-later -->
<script setup lang="ts">
import { computed, ref } from 'vue'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import Message from 'primevue/message'
import { api, formatBytes, type LegacyCleanupPreview, type LegacyCleanupResult } from '@/api'

/**
 * „Erledigtes aufräumen“: zeigt, welche Altbestand-Dateien nachweislich in der Library liegen, und
 * verschiebt nach Bestätigung genau diese in den Papierkorb. Der Server prüft jede Datei dabei erneut.
 */
defineProps<{ disabled?: boolean }>()
const emit = defineEmits<{ done: [result: LegacyCleanupResult] }>()

const visible = ref(false)
const preview = ref<LegacyCleanupPreview | null>(null)
const result = ref<LegacyCleanupResult | null>(null)
const error = ref<string | null>(null)
const loading = ref(false)
const moving = ref(false)

async function open() {
  visible.value = true
  preview.value = null
  result.value = null
  error.value = null
  loading.value = true
  try {
    preview.value = await api.legacyCleanup()
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  } finally {
    loading.value = false
  }
}

async function confirm() {
  if (!preview.value?.count) return
  moving.value = true
  error.value = null
  try {
    result.value = await api.cleanLegacy(preview.value.files.map((f) => f.jobId))
    emit('done', result.value)
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  } finally {
    moving.value = false
  }
}

const already = computed(() => preview.value?.files.filter((f) => f.alreadyInLibrary).length ?? 0)
const folder = (path: string) => (path.includes('/') ? path.slice(0, path.lastIndexOf('/')) : '')
const name = (path: string) => path.slice(path.lastIndexOf('/') + 1)
</script>

<template>
  <Button
    label="Erledigtes aufräumen"
    icon="pi pi-trash"
    severity="secondary"
    size="small"
    :disabled="disabled"
    data-testid="legacy-cleanup"
    @click="open"
  />
  <Dialog v-model:visible="visible" modal header="Erledigtes aufräumen" class="w-full max-w-4xl" :dismissable-mask="!moving">
    <p v-if="loading" class="text-sm text-surface-500">Prüfe den Altbestand gegen die Library …</p>
    <Message v-if="error" severity="error" class="mb-4">{{ error }}</Message>

    <template v-if="result">
      <Message severity="success" class="mb-4" data-testid="cleanup-result">
        {{ result.moved }} Datei(en) ({{ formatBytes(result.sizeBytes) }}) liegen jetzt in
        <code>Library/{{ result.trashFolder }}</code>.
      </Message>
      <p v-if="result.skipped.length" class="text-sm text-surface-500">
        {{ result.skipped.length }} Datei(en) sind geblieben, weil der Nachweis beim Verschieben nicht mehr reichte.
      </p>
    </template>

    <template v-else-if="preview">
      <Message v-if="!preview.count" severity="info" class="mb-4" data-testid="cleanup-empty">
        Gerade gibt es nichts aufzuräumen: Keine Datei im Altbestand ist nachweislich in der Library.
      </Message>
      <template v-else>
        <p class="mb-2" data-testid="cleanup-summary">
          <strong>{{ preview.count }} Datei(en)</strong> ({{ formatBytes(preview.sizeBytes) }}) liegen nachweislich in der Library
          <span v-if="already">(davon {{ already }} schon vorher byte-gleich)</span> und wandern nach
          <code>Library/{{ preview.trashFolder }}</code>.
        </p>
        <p class="mb-4 text-sm text-surface-500">
          Verschoben wird nur, was Tonregal übernommen hat oder was identisch schon da war, sich seitdem nicht geändert hat und
          dessen Kopie jetzt in der Library liegt. Ordnerstruktur und Dateinamen bleiben im Papierkorb erhalten; er wird nach
          30 Tagen geleert. Covers und andere Nicht-Audio-Dateien bleiben im Altbestand.
        </p>
        <div class="max-h-80 overflow-auto rounded-md border border-surface-200 dark:border-surface-700 text-sm" data-testid="cleanup-files">
          <div
            v-for="file in preview.files"
            :key="file.jobId"
            class="flex flex-wrap gap-x-3 px-3 py-1 border-b border-surface-100 dark:border-surface-800 last:border-0"
          >
            <span class="text-surface-500 truncate max-w-64" :title="folder(file.path)">{{ folder(file.path) }}</span>
            <span class="font-medium">{{ name(file.path) }}</span>
            <span class="ml-auto text-xs text-surface-500 truncate max-w-96" :title="file.libraryPath">→ {{ file.libraryPath }}</span>
          </div>
        </div>
      </template>

      <details v-if="preview.skipped.length" class="mt-4 text-sm" data-testid="cleanup-skipped">
        <summary class="cursor-pointer text-surface-500">
          {{ preview.skipped.length }} übernommene Datei(en) bleiben, weil der Nachweis fehlt
        </summary>
        <ul class="mt-2 max-h-48 overflow-auto">
          <li v-for="s in preview.skipped" :key="s.jobId" class="py-0.5">
            <span class="break-all">{{ s.path }}</span> <span class="text-surface-500">· {{ s.reason }}</span>
          </li>
        </ul>
      </details>
    </template>

    <template #footer>
      <Button :label="result ? 'Schließen' : 'Abbrechen'" severity="secondary" size="small" :disabled="moving" @click="visible = false" />
      <Button
        v-if="!result && preview?.count"
        :label="`${preview.count} Datei(en) in den Papierkorb`"
        icon="pi pi-trash"
        severity="danger"
        size="small"
        :loading="moving"
        data-testid="cleanup-confirm"
        @click="confirm"
      />
    </template>
  </Dialog>
</template>

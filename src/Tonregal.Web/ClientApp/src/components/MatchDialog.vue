<!-- SPDX-License-Identifier: AGPL-3.0-or-later -->
<script setup lang="ts">
import { ref, watch } from 'vue'
import Dialog from 'primevue/dialog'
import Message from 'primevue/message'
import ProgressSpinner from 'primevue/progressspinner'
import CandidateTable from '@/components/CandidateTable.vue'
import { api, type FileDto, type MatchResponse, type TrackDto } from '@/api'

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
      <CandidateTable v-else :candidates="result.candidates" />
    </template>
  </Dialog>
</template>

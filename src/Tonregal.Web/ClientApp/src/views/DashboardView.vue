<!-- SPDX-License-Identifier: AGPL-3.0-or-later -->
<script setup lang="ts">
import { onMounted, ref } from 'vue'
import Card from 'primevue/card'
import Message from 'primevue/message'
import { api, type HealthResponse, type LibraryStats } from '@/api'

const health = ref<HealthResponse | null>(null)
const stats = ref<LibraryStats | null>(null)
const error = ref<string | null>(null)

onMounted(async () => {
  try {
    ;[health.value, stats.value] = await Promise.all([api.health(), api.stats()])
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  }
})

const tiles: { key: keyof LibraryStats; label: string }[] = [
  { key: 'artists', label: 'Künstler' },
  { key: 'albums', label: 'Alben' },
  { key: 'tracks', label: 'Tracks' },
  { key: 'files', label: 'Dateien' },
  { key: 'openReviews', label: 'Offene Reviews' },
  { key: 'openDuplicates', label: 'Offene Dubletten' },
]
</script>

<template>
  <h1 class="text-2xl font-semibold mb-6">Übersicht</h1>
  <Message v-if="error" severity="error" class="mb-4">{{ error }}</Message>
  <Message v-else-if="health && !health.libraryMounted" severity="warn" class="mb-4">
    Mount-Marker nicht gefunden – ist die SSD gemountet?
  </Message>
  <div class="grid grid-cols-2 lg:grid-cols-3 gap-4">
    <Card v-for="tile in tiles" :key="tile.key">
      <template #subtitle>{{ tile.label }}</template>
      <template #content>
        <span class="text-3xl font-semibold tabular-nums" :data-testid="tile.key">{{ stats?.[tile.key] ?? '–' }}</span>
      </template>
    </Card>
  </div>
  <p v-if="health" class="mt-6 text-sm text-surface-500">Version {{ health.version }}</p>
</template>

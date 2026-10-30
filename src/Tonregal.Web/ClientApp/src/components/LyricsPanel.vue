<!-- SPDX-License-Identifier: AGPL-3.0-or-later -->
<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import Button from 'primevue/button'
import ProgressSpinner from 'primevue/progressspinner'
import { api, type SongLyrics } from '@/api'
import { currentLineIndex, lyricsSourceLabel } from '@/lyrics'

const props = defineProps<{ trackId: number; position: number }>()
const emit = defineEmits<{ seek: [seconds: number] }>()

const lyrics = ref<SongLyrics | null>(null)
const loading = ref(false)
const error = ref<string | null>(null)
const lineElements = ref<HTMLElement[]>([])

// Antworten zu einem Stück, das schon nicht mehr läuft, fallen weg.
let requested = 0

async function load(refresh = false) {
  const id = props.trackId
  const token = ++requested
  loading.value = true
  error.value = null
  if (!refresh) lyrics.value = null
  try {
    const result = await (refresh ? api.refreshLyrics(id) : api.lyrics(id))
    if (token === requested) lyrics.value = result
  } catch (e) {
    if (token === requested) error.value = (e as Error).message
  } finally {
    if (token === requested) loading.value = false
  }
}

watch(() => props.trackId, () => load(), { immediate: true })

const active = computed(() => (lyrics.value?.synced ? currentLineIndex(lyrics.value.lines, props.position * 1000) : -1))

watch(active, async (index) => {
  if (index < 0) return
  await nextTick()
  // Mitscrollen, die aktuelle Zeile mittig; jsdom kennt scrollIntoView nicht.
  lineElements.value[index]?.scrollIntoView?.({ block: 'center', behavior: 'smooth' })
})
</script>

<template>
  <div class="flex flex-col gap-4" data-testid="lyrics">
    <div v-if="loading && !lyrics" class="flex justify-center py-8"><ProgressSpinner class="!size-10" /></div>
    <p v-else-if="error" class="text-red-500">{{ error }}</p>
    <template v-else-if="lyrics">
      <p v-if="lyrics.instrumental" class="text-surface-500">Instrumental</p>
      <p v-else-if="!lyrics.lines.length" class="text-surface-500" data-testid="lyrics-none">Kein Songtext gefunden.</p>
      <ol v-else-if="lyrics.synced" class="flex flex-col gap-1 text-lg leading-snug">
        <li v-for="(line, i) in lyrics.lines" :key="i" ref="lineElements">
          <button
            class="w-full text-left rounded px-1 py-0.5 transition-colors hover:bg-surface-100 dark:hover:bg-surface-800"
            :class="i === active ? 'text-primary font-semibold' : 'text-surface-500'"
            :data-active="i === active || undefined"
            data-testid="lyrics-line"
            @click="emit('seek', line.timeMs! / 1000)"
          >
            {{ line.text || '♪' }}
          </button>
        </li>
      </ol>
      <div v-else class="text-lg leading-snug">
        <p v-for="(line, i) in lyrics.lines" :key="i" class="min-h-[1lh]" data-testid="lyrics-line">{{ line.text }}</p>
      </div>
      <div class="flex items-center justify-between gap-2 text-sm text-surface-500">
        <span v-if="lyrics.source !== 'None'">Quelle: {{ lyricsSourceLabel[lyrics.source] }}{{ lyrics.synced ? ', synchronisiert' : '' }}</span>
        <span v-else />
        <Button label="Neu suchen" icon="pi pi-refresh" text size="small" :loading="loading" data-testid="lyrics-refresh" @click="load(true)" />
      </div>
    </template>
  </div>
</template>

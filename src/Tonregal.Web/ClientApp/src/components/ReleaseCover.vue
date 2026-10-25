<!-- SPDX-License-Identifier: AGPL-3.0-or-later -->
<script setup lang="ts">
import { ref, watch } from 'vue'
import { api } from '@/api'

/** Vorschau aus dem Cover Art Archive; ohne Cover ein neutraler Platzhalter. */
const props = defineProps<{ releaseId: string | null }>()
const failed = ref(false)
watch(
  () => props.releaseId,
  () => (failed.value = false),
)
</script>

<template>
  <div class="rounded overflow-hidden bg-surface-200 dark:bg-surface-700 flex items-center justify-center">
    <img v-if="releaseId && !failed" :src="api.releaseCoverUrl(releaseId)" alt="" loading="lazy" class="w-full h-full object-cover" @error="failed = true" />
    <i v-else class="pi pi-image text-surface-400" />
  </div>
</template>

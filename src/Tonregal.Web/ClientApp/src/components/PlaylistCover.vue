<!-- SPDX-License-Identifier: AGPL-3.0-or-later -->
<script setup lang="ts">
import AlbumCover from './AlbumCover.vue'

/** Ein Cover bei einem Album, sonst ein Mosaik aus bis zu vier; leer ein Symbol. */
defineProps<{ albumIds: number[]; alt: string }>()
</script>

<template>
  <AlbumCover v-if="albumIds.length === 1" :album-id="albumIds[0]!" :alt="alt" />
  <div v-else-if="albumIds.length" class="aspect-square w-full overflow-hidden rounded-md grid grid-cols-2 grid-rows-2" :aria-label="alt" role="img">
    <div v-for="(id, i) in albumIds.slice(0, 4)" :key="id" :class="{ 'row-span-2': albumIds.length === 2 || (albumIds.length === 3 && i === 0) }">
      <AlbumCover :album-id="id" alt="" class="!rounded-none !aspect-auto h-full" />
    </div>
  </div>
  <div v-else class="aspect-square w-full rounded-md bg-surface-200 dark:bg-surface-800 flex items-center justify-center">
    <i class="pi pi-list text-3xl text-surface-400" />
  </div>
</template>

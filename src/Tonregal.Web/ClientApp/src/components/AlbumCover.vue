<!-- SPDX-License-Identifier: AGPL-3.0-or-later -->
<script setup lang="ts">
import { ref } from 'vue'
import { api } from '@/api'

defineProps<{ albumId: number; alt: string }>()
const failed = ref(false)
</script>

<template>
  <div class="aspect-square w-full overflow-hidden rounded-md bg-surface-200 dark:bg-surface-800 flex items-center justify-center">
    <img
      v-if="!failed"
      :src="api.coverUrl(albumId)"
      :alt="alt"
      loading="lazy"
      class="size-full object-cover"
      @error="failed = true"
    />
    <i v-else class="pi pi-images text-3xl text-surface-400" />
  </div>
</template>

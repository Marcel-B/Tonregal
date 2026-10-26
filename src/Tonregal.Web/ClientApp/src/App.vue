<!-- SPDX-License-Identifier: AGPL-3.0-or-later -->
<script setup lang="ts">
import { onMounted } from 'vue'
import { useRoute } from 'vue-router'
import Toast from 'primevue/toast'
import PlayerBar from './components/PlayerBar.vue'
import { routes } from './router'
import { useStatusStore } from './stores/status'
import { usePlayerStore } from './stores/player'

const status = useStatusStore()
const route = useRoute()
const player = usePlayerStore()
onMounted(() => status.connect())

/** Unterseiten wie /library/albums/1 markieren ihren Navigationspunkt mit. */
const isActive = (path: string) => (path === '/' ? route.path === '/' : route.path.startsWith(path))
</script>

<template>
  <div class="min-h-screen flex flex-col md:flex-row">
    <nav :class="player.current && 'md:pb-24'" class="md:w-56 shrink-0 border-b md:border-b-0 md:border-r border-surface-200 dark:border-surface-800 p-4 flex md:flex-col gap-1">
      <div class="font-semibold text-lg mb-0 md:mb-4 mr-4 md:mr-0">Tonregal</div>
      <RouterLink
        v-for="item in routes"
        :key="item.name"
        :to="item.path"
        class="flex items-center gap-2 rounded-md px-3 py-2 hover:bg-surface-100 dark:hover:bg-surface-800"
        :class="isActive(item.path) && 'bg-primary-50 text-primary-700 dark:bg-primary-900/40 dark:text-primary-200'"
      >
        <i :class="item.meta.icon" />
        <span class="hidden sm:inline">{{ item.meta.title }}</span>
      </RouterLink>
      <div class="md:mt-auto ml-auto md:ml-0 flex items-center gap-2 text-sm text-surface-500">
        <span class="inline-block size-2 rounded-full" :class="status.connected ? 'bg-green-500' : 'bg-surface-400'" />
        <span class="hidden sm:inline">{{ status.connected ? 'Live' : 'Offline' }}</span>
      </div>
    </nav>
    <main class="flex-1 p-4 md:p-8" :class="player.current && 'pb-28 md:pb-28'">
      <RouterView />
    </main>
    <PlayerBar />
    <Toast />
  </div>
</template>

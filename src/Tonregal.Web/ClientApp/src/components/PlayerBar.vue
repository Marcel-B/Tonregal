<!-- SPDX-License-Identifier: AGPL-3.0-or-later -->
<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import Button from 'primevue/button'
import Drawer from 'primevue/drawer'
import Rating from 'primevue/rating'
import { api, formatDuration } from '@/api'
import { usePlayerStore } from '@/stores/player'
import { useRatingsStore } from '@/stores/ratings'
import LyricsPanel from './LyricsPanel.vue'
import ShuffleIcon from './ShuffleIcon.vue'

const player = usePlayerStore()
const ratings = useRatingsStore()
const audio = ref<HTMLAudioElement | null>(null)
const position = ref(0)
const duration = ref(0)
const queueVisible = ref(false)
const lyricsVisible = ref(false)
const error = ref<string | null>(null)

// Navidrome zählt ein Stück als gehört nach der Hälfte, spätestens nach 4 Minuten (wie Last.fm).
let nowPlayingSent = false
let submitted = false
// Beim Wechsel der Quelle meldet der Browser ein „pause“, das kein Pausenwunsch ist.
let switching = false

function load(autoplay: boolean) {
  const el = audio.value
  const item = player.current
  if (!el) return
  error.value = null
  nowPlayingSent = submitted = false
  position.value = 0
  duration.value = item ? item.durationMs / 1000 : 0
  if (!item) {
    el.removeAttribute('src')
    el.load()
    return
  }
  switching = true
  el.src = api.streamUrl(item.fileId)
  if (autoplay) apply(true)
  updateMediaSession()
}

/** @param restart nach einem Quellwechsel: abspielen, auch wenn das Element noch nicht als pausiert gilt. */
function apply(restart = false) {
  const el = audio.value
  if (!el || !player.current) return
  if (player.playing && (el.paused || restart)) {
    el.play()?.catch((e: DOMException) => {
      // Blockiertes Autoplay (iOS nach dem Neuladen) – erst ein Tipp auf Play startet.
      if (e.name === 'NotAllowedError') player.playing = false
    })
  } else if (!player.playing && !el.paused) el.pause()
}

// sync: play() muss iOS noch im selben Tipp erreichen, sonst gilt es als Autoplay.
watch(() => player.startToken, () => load(player.playing), { flush: 'sync' })
watch(() => player.playing, () => apply(), { flush: 'sync' })
onMounted(() => {
  load(false)
  registerMediaSession()
})
watch(() => player.current?.fileId, (fileId) => fileId !== undefined && ratings.load([fileId]), { immediate: true })

const repeatLabel = { off: 'Wiederholen: aus', all: 'Wiederholen: alle', one: 'Wiederholen: dieses Stück' } as const

function onTimeUpdate() {
  const el = audio.value!
  position.value = el.currentTime
  const item = player.current
  if (!item) return
  if (!nowPlayingSent && el.currentTime > 0) {
    nowPlayingSent = true
    api.scrobble(item.fileId, false).catch(() => {})
  }
  const total = duration.value || item.durationMs / 1000
  if (!submitted && total > 0 && el.currentTime >= Math.min(total / 2, 240)) {
    submitted = true
    api.scrobble(item.fileId, true).catch(() => {})
  }
  updatePositionState()
}

function onLoaded() {
  switching = false
}

function onPause() {
  if (switching || audio.value?.ended) return
  player.playing = false
}

function onDurationChange() {
  const value = audio.value!.duration
  if (Number.isFinite(value) && value > 0) duration.value = value
}

function onError() {
  switching = false
  if (!player.current) return
  error.value = 'Stück lässt sich nicht abspielen'
  player.playing = false
}

function seek(event: Event) {
  seekTo(Number((event.target as HTMLInputElement).value))
}

function seekTo(seconds: number) {
  const el = audio.value
  if (!el) return
  el.currentTime = seconds
  position.value = el.currentTime
}

function previous() {
  // Wie bei den meisten Playern: nach ein paar Sekunden zurück an den Anfang, sonst ein Stück zurück.
  if (audio.value && audio.value.currentTime > 3) audio.value.currentTime = 0
  else player.previous()
}

/** Steuerung im Sperrbildschirm, Kontrollzentrum und über Kopfhörertasten. */
function registerMediaSession() {
  if (!('mediaSession' in navigator)) return
  const handlers: [MediaSessionAction, MediaSessionActionHandler][] = [
    ['play', () => (player.playing = true)],
    ['pause', () => (player.playing = false)],
    ['previoustrack', previous],
    ['nexttrack', () => player.next()],
    ['seekto', (d) => d.seekTime !== undefined && audio.value && (audio.value.currentTime = d.seekTime)],
  ]
  for (const [action, handler] of handlers) {
    try {
      navigator.mediaSession.setActionHandler(action, handler)
    } catch {
      // Aktion wird vom Browser nicht unterstützt
    }
  }
}

function updateMediaSession() {
  const item = player.current
  if (!item || !('mediaSession' in navigator) || typeof MediaMetadata === 'undefined') return
  navigator.mediaSession.metadata = new MediaMetadata({
    title: item.title,
    artist: item.artist,
    album: item.album,
    artwork: [{ src: api.coverUrl(item.albumId), sizes: '512x512', type: 'image/jpeg' }],
  })
}

function updatePositionState() {
  if (!('mediaSession' in navigator) || !duration.value) return
  try {
    navigator.mediaSession.setPositionState({ duration: duration.value, position: Math.min(position.value, duration.value), playbackRate: 1 })
  } catch {
    // ältere Browser
  }
}

const time = (seconds: number) => formatDuration(seconds * 1000)
</script>

<template>
  <audio
    ref="audio"
    preload="auto"
    data-testid="player-audio"
    @timeupdate="onTimeUpdate"
    @durationchange="onDurationChange"
    @loadeddata="onLoaded"
    @play="player.playing = true"
    @pause="onPause"
    @ended="player.finished()"
    @error="onError"
  />
  <div
    v-if="player.current"
    class="fixed inset-x-0 bottom-0 z-40 border-t border-surface-200 dark:border-surface-800 bg-surface-0/95 dark:bg-surface-900/95 backdrop-blur pb-[env(safe-area-inset-bottom)]"
    data-testid="player"
  >
    <input
      type="range"
      min="0"
      :max="duration || 1"
      step="0.1"
      :value="position"
      aria-label="Position"
      class="block w-full h-1 accent-primary cursor-pointer"
      data-testid="player-seek"
      @input="seek"
    />
    <div class="flex items-center gap-3 px-3 py-2">
      <RouterLink :to="{ name: 'album', params: { id: player.current.albumId } }" class="flex items-center gap-3 min-w-0 flex-1">
        <img :src="api.coverUrl(player.current.albumId)" alt="" class="size-11 rounded object-cover bg-surface-200 dark:bg-surface-800 shrink-0" />
        <div class="min-w-0">
          <div class="truncate font-medium" data-testid="player-title">{{ player.current.title }}</div>
          <div class="truncate text-sm text-surface-500">
            <template v-if="error || ratings.error"><span class="text-red-500">{{ error ?? ratings.error }}</span></template>
            <template v-else>{{ player.current.artist }} · {{ player.current.album }}</template>
          </div>
        </div>
      </RouterLink>
      <Rating
        :model-value="ratings.of(player.current.fileId)"
        class="hidden md:flex"
        aria-label="Bewertung"
        data-testid="player-rating"
        @update:model-value="(value: number | null) => ratings.rate(player.current!.fileId, value ?? 0)"
      />
      <span class="hidden sm:inline text-sm text-surface-500 tabular-nums">{{ time(position) }} / {{ time(duration) }}</span>
      <div class="flex items-center">
        <Button
          icon="pi pi-replay"
          text
          rounded
          :severity="player.repeat === 'off' ? 'secondary' : undefined"
          :badge="player.repeat === 'one' ? '1' : undefined"
          :aria-label="repeatLabel[player.repeat]"
          :title="repeatLabel[player.repeat]"
          :aria-pressed="player.repeat !== 'off'"
          data-testid="player-repeat"
          @click="player.cycleRepeat()"
        />
        <Button icon="pi pi-step-backward" text rounded aria-label="Zurück" @click="previous" />
        <Button
          :icon="player.playing ? 'pi pi-pause' : 'pi pi-play'"
          rounded
          :aria-label="player.playing ? 'Pause' : 'Abspielen'"
          data-testid="player-toggle"
          @click="player.toggle()"
        />
        <Button icon="pi pi-step-forward" text rounded aria-label="Weiter" :disabled="!player.hasNext" data-testid="player-next" @click="player.next()" />
        <Button icon="pi pi-align-left" text rounded aria-label="Songtext" data-testid="player-lyrics" @click="lyricsVisible = true" />
        <Button icon="pi pi-list" text rounded aria-label="Warteschlange" data-testid="player-queue" @click="queueVisible = true" />
      </div>
    </div>
  </div>
  <Drawer v-model:visible="lyricsVisible" position="right" class="!w-full sm:!w-[28rem]">
    <template #header>
      <div v-if="player.current" class="min-w-0">
        <div class="truncate font-semibold">{{ player.current.title }}</div>
        <div class="truncate text-sm text-surface-500">{{ player.current.artist }}</div>
      </div>
    </template>
    <LyricsPanel v-if="player.current" :track-id="player.current.trackId" :position="position" @seek="seekTo" />
  </Drawer>
  <Drawer v-model:visible="queueVisible" header="Warteschlange" position="right" class="!w-full sm:!w-96">
    <ol class="flex flex-col">
      <li
        v-for="(item, i) in player.queue"
        :key="`${i}-${item.fileId}`"
        class="flex items-center gap-2 rounded-md px-2 py-1.5 hover:bg-surface-100 dark:hover:bg-surface-800"
        :class="i === player.index && 'text-primary font-semibold'"
        data-testid="queue-item"
      >
        <button class="flex-1 min-w-0 text-left" @click="player.jump(i)">
          <div class="truncate">{{ item.title }}</div>
          <div class="truncate text-sm text-surface-500 font-normal">{{ item.artist }} · {{ formatDuration(item.durationMs) }}</div>
        </button>
        <Button icon="pi pi-times" text rounded size="small" severity="secondary" aria-label="Entfernen" @click="player.remove(i)" />
      </li>
    </ol>
    <template #footer>
      <Button
        label="Rest mischen"
        severity="secondary"
        size="small"
        class="mr-2"
        :disabled="player.index >= player.queue.length - 1"
        data-testid="queue-shuffle"
        @click="player.shuffleUpcoming()"
      >
        <template #icon="{ class: iconClass }"><ShuffleIcon :class="iconClass" /></template>
      </Button>
      <Button label="Leeren" icon="pi pi-trash" severity="secondary" size="small" :disabled="!player.queue.length" @click="(player.clear(), (queueVisible = false))" />
    </template>
  </Drawer>
</template>

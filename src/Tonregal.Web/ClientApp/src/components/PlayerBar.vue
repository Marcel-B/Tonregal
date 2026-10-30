<!-- SPDX-License-Identifier: AGPL-3.0-or-later -->
<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import Button from 'primevue/button'
import Drawer from 'primevue/drawer'
import Rating from 'primevue/rating'
import { api, formatDuration } from '@/api'
import { usePlayerStore } from '@/stores/player'
import { useRatingsStore } from '@/stores/ratings'
import NowPlaying from './NowPlaying.vue'
import ShuffleIcon from './ShuffleIcon.vue'

const player = usePlayerStore()
const ratings = useRatingsStore()
const audio = ref<HTMLAudioElement | null>(null)
const position = ref(0)
const duration = ref(0)
const queueVisible = ref(false)
const error = ref<string | null>(null)

// Navidrome zählt ein Stück als gehört nach der Hälfte, spätestens nach 4 Minuten (wie Last.fm).
let nowPlayingSent = false
let submitted = false
// Beim Wechsel der Quelle meldet der Browser ein „pause“, das kein Pausenwunsch ist.
let switching = false
// Nach dem Neuladen geht es an der gemerkten Stelle weiter, sobald das Element die Länge kennt.
let resumeAt = 0
let rememberedAt = 0

function load(autoplay: boolean) {
  const el = audio.value
  const item = player.current
  if (!el) return
  error.value = null
  nowPlayingSent = submitted = false
  resumeAt = 0
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
  resumeAt = player.savedPosition()
  position.value = resumeAt
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
  if (Math.abs(el.currentTime - rememberedAt) >= 5) remember()
}

function remember() {
  const el = audio.value
  if (!el || !player.current) return
  rememberedAt = el.currentTime
  player.rememberPosition(el.currentTime)
}

function onMetadata() {
  if (!resumeAt || !audio.value) return
  audio.value.currentTime = resumeAt
  position.value = resumeAt
  resumeAt = 0
}

function onLoaded() {
  switching = false
}

function onPause() {
  if (switching || audio.value?.ended) return
  player.playing = false
  remember()
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
  resumeAt = 0
  el.currentTime = seconds
  position.value = el.currentTime
  remember()
}

/** Cover und Titel öffnen den großen Player, wie ein Tipp auf die kleine Leiste in den Musik-Apps. */
function expand() {
  player.expanded = true
}

// ---- Warteschlange umsortieren --------------------------------------------------------------------------------

/** Am Griff gezogen (Maus, Finger, Stift über Pointer Events, die auch Safari auf dem iPhone kann) oder per Pfeiltaste. */
const dragFrom = ref(-1)
const dragTo = ref(-1)
const rows = ref<HTMLElement[]>([])

function dragStart(event: PointerEvent, at: number) {
  dragFrom.value = dragTo.value = at
  ;(event.currentTarget as HTMLElement).setPointerCapture?.(event.pointerId)
}

function dragMove(event: PointerEvent) {
  if (dragFrom.value < 0) return
  const at = rows.value.findIndex((row) => {
    const box = row.getBoundingClientRect()
    return event.clientY >= box.top && event.clientY < box.bottom
  })
  if (at >= 0) dragTo.value = at
  else if (rows.value.length && event.clientY >= rows.value[rows.value.length - 1]!.getBoundingClientRect().bottom) {
    dragTo.value = rows.value.length - 1
  } else if (rows.value.length && event.clientY < rows.value[0]!.getBoundingClientRect().top) dragTo.value = 0
}

function dragEnd() {
  if (dragFrom.value >= 0 && dragTo.value >= 0) player.move(dragFrom.value, dragTo.value)
  dragFrom.value = dragTo.value = -1
}

function moveByKey(event: KeyboardEvent, at: number) {
  const to = event.key === 'ArrowUp' ? at - 1 : event.key === 'ArrowDown' ? at + 1 : -1
  if (to < 0 || to >= player.queue.length) return
  event.preventDefault()
  player.move(at, to)
  // Der Fokus folgt dem Eintrag, damit sich mehrfach drücken lässt.
  requestAnimationFrame(() => rows.value[to]?.querySelector<HTMLElement>('[data-handle]')?.focus())
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
    ['seekto', (d) => d.seekTime !== undefined && seekTo(d.seekTime)],
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
    @loadedmetadata="onMetadata"
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
      <button type="button" class="shrink-0 cursor-pointer" aria-label="Player öffnen" data-testid="player-expand" @click="expand">
        <img :src="api.coverUrl(player.current.albumId)" alt="" class="size-11 rounded object-cover bg-surface-200 dark:bg-surface-800" />
      </button>
      <div class="min-w-0 flex-1">
        <button type="button" class="block w-full truncate text-left cursor-pointer hover:underline" title="Player öffnen" @click="expand">
          <span class="font-medium" data-testid="player-title">{{ player.current.title }}</span>
          <span class="text-sm text-surface-500"> · {{ player.current.artist }}</span>
        </button>
        <!-- Bewertet wird, während es läuft, wenn sich ein Stück am besten beurteilen lässt; auch auf dem Telefon. -->
        <div class="mt-0.5 flex items-center gap-3 text-sm">
          <span v-if="error || ratings.error" class="truncate text-red-500">{{ error ?? ratings.error }}</span>
          <Rating
            v-else
            :model-value="ratings.of(player.current.fileId)"
            class="mini-rating"
            aria-label="Bewertung"
            data-testid="player-rating"
            @update:model-value="(value: number | null) => ratings.rate(player.current!.fileId, value ?? 0)"
          />
          <span class="hidden sm:inline text-surface-500 tabular-nums">{{ time(position) }} / {{ time(duration) }}</span>
        </div>
      </div>
      <div class="flex items-center">
        <Button
          icon="pi pi-replay"
          text
          rounded
          class="!hidden sm:!inline-flex"
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
        <Button icon="pi pi-list" text rounded class="!hidden sm:!inline-flex" aria-label="Warteschlange" data-testid="player-queue" @click="queueVisible = true" />
      </div>
    </div>
  </div>
  <Transition name="sheet">
    <NowPlaying
      v-if="player.expanded && player.current"
      :position="position"
      :duration="duration"
      :error="error"
      @seek="seekTo"
      @previous="previous"
      @queue="queueVisible = true"
    />
  </Transition>
  <Drawer v-model:visible="queueVisible" header="Warteschlange" position="right" class="!w-full sm:!w-96">
    <ol class="flex flex-col" @pointermove="dragMove" @pointerup="dragEnd" @pointercancel="dragEnd">
      <li
        v-for="(item, i) in player.queue"
        :key="`${i}-${item.fileId}`"
        ref="rows"
        class="flex items-center gap-1 rounded-md px-1 py-1.5 hover:bg-surface-100 dark:hover:bg-surface-800"
        :class="[
          i === player.index && 'text-primary font-semibold',
          // Schon Gespieltes blass, wie in der App.
          i < player.index && i !== dragFrom && 'opacity-50',
          i === dragFrom && 'bg-surface-100 dark:bg-surface-800 shadow',
          dragFrom >= 0 && i === dragTo && i !== dragFrom && (dragTo > dragFrom ? 'border-b-2 border-primary' : 'border-t-2 border-primary'),
        ]"
        data-testid="queue-item"
      >
        <span
          data-handle
          tabindex="0"
          role="button"
          class="drag-handle shrink-0 cursor-grab px-2 py-1 text-surface-400"
          :aria-label="`${item.title} verschieben (Pfeiltasten)`"
          title="Ziehen zum Verschieben"
          data-testid="queue-handle"
          @pointerdown.prevent="dragStart($event, i)"
          @keydown="moveByKey($event, i)"
        >
          <i class="pi pi-bars" />
        </span>
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

<style scoped>
/* Nur der Griff nimmt den Finger, damit die Liste daneben weiter scrollt. */
.drag-handle {
  touch-action: none;
}

.mini-rating {
  --p-rating-icon-size: 0.9rem;
  gap: 0.15rem;
}

/* Der große Player kommt aus der kleinen Leiste und geht wieder in sie zurück. */
.sheet-enter-active,
.sheet-leave-active {
  transition:
    transform 0.25s ease,
    opacity 0.25s ease;
}

.sheet-enter-from,
.sheet-leave-to {
  transform: translateY(100%);
  opacity: 0.6;
}

@media (prefers-reduced-motion: reduce) {
  .sheet-enter-active,
  .sheet-leave-active {
    transition: none;
  }
}
</style>

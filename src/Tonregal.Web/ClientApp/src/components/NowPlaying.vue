<!-- SPDX-License-Identifier: AGPL-3.0-or-later -->
<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import Button from 'primevue/button'
import Rating from 'primevue/rating'
import Slider from 'primevue/slider'
import { api, formatDuration } from '@/api'
import { coverColour, fallbackColour } from '@/coverColour'
import { usePlayerStore } from '@/stores/player'
import { useRatingsStore } from '@/stores/ratings'
import LyricsPanel from './LyricsPanel.vue'
import ShuffleIcon from './ShuffleIcon.vue'

/**
 * Der Player über dem ganzen Bildschirm, nach dem Vorbild von Tonwerk-AI und dem großen Player der iPhone-App: das
 * Cover groß auf einer unscharfen Kopie seiner selbst, Fortschritt, Knöpfe, Bewertung und auf Wunsch der Songtext.
 * Er zeigt nur, was die PlayerBar spielt; das &lt;audio&gt;-Element bleibt dort, Öffnen und Schließen unterbricht nichts.
 */
const props = defineProps<{ position: number; duration: number; error: string | null }>()
const emit = defineEmits<{ seek: [seconds: number]; previous: []; queue: [] }>()

const player = usePlayerStore()
const ratings = useRatingsStore()
const route = useRoute()

const lyricsKey = 'tonregal.showsLyrics'
const showsLyrics = ref(readLyricsSwitch())

function readLyricsSwitch(): boolean {
  try {
    return localStorage.getItem(lyricsKey) === 'true'
  } catch {
    return false
  }
}

function toggleLyrics() {
  showsLyrics.value = !showsLyrics.value
  try {
    localStorage.setItem(lyricsKey, String(showsLyrics.value))
  } catch {
    // privates Fenster o. ä.
  }
}

/** Die Farbe des Covers für Schimmer, Abschnittsfarbe und gesetzte Schalter; bis es geladen ist, die der Seite. */
const colour = ref(fallbackColour)

function coverLoaded(event: Event) {
  try {
    colour.value = coverColour(event.target as HTMLImageElement)
  } catch {
    // Ein Cover, das der Canvas nicht lesen darf, behält die bisherige Farbe.
  }
}

const cover = computed(() => (player.current ? api.coverUrl(player.current.albumId) : undefined))
const coverMissing = ref(false)
watch(cover, () => (coverMissing.value = false))

// ---- Fortschritt ------------------------------------------------------------------------------------------------

/** Beim Ziehen zeigt der Regler, wo der Finger ist, und das Stück springt erst beim Loslassen dorthin. */
const scrubbing = ref<number | null>(null)
const shownTime = computed(() => scrubbing.value ?? props.position)
const progress = ref<HTMLElement | null>(null)

function scrub(value: number | number[]) {
  scrubbing.value = Array.isArray(value) ? (value[0] ?? 0) : value
  // Ein Tipp auf die Leiste oder eine Pfeiltaste springt sofort, nur ein Ziehen wartet aufs Loslassen.
  if (!progress.value?.querySelector('[data-p-sliding="true"]')) scrubbed()
}

function scrubbed() {
  if (scrubbing.value === null) return
  emit('seek', scrubbing.value)
  scrubbing.value = null
}

const clock = (seconds: number) => formatDuration(Math.max(0, seconds) * 1000)

const repeatLabel = { off: 'Wiederholen: aus', all: 'Wiederholen: alle', one: 'Wiederholen: dieses Stück' } as const

// ---- Öffnen und Schließen ---------------------------------------------------------------------------------------

/** Die Zurück-Geste des Telefons schließt den Player, statt die Seite zu verlassen; das Öffnen legte dafür einen Schritt an. */
function shut() {
  if ((history.state as { nowPlaying?: boolean } | null)?.nowPlaying) history.back()
  else player.expanded = false
}

const onPopState = () => (player.expanded = false)

function onKey(event: KeyboardEvent) {
  if (event.key === 'Escape') shut()
}

// Ein Link im Player (Album) führt auf eine andere Seite; dann zu.
watch(
  () => route?.fullPath,
  () => (player.expanded = false),
)

/** Am oberen Rand oder am Cover nach unten gezogen, geht der Player zu wie ein Blatt auf dem Telefon. */
const pull = ref(0)
let pullFrom: number | null = null

function pullStart(event: TouchEvent) {
  pullFrom = event.touches.length === 1 ? event.touches[0]!.clientY : null
}

function pullMove(event: TouchEvent) {
  if (pullFrom !== null) pull.value = Math.max(0, event.touches[0]!.clientY - pullFrom)
}

function pullEnd() {
  if (pull.value > 120) shut()
  pull.value = 0
  pullFrom = null
}

let scrolledTo = 0

onMounted(() => {
  history.pushState({ ...(history.state as object | null), nowPlaying: true }, '')
  window.addEventListener('popstate', onPopState)
  window.addEventListener('keydown', onKey)
  // Kein overflow: hidden auf der Seite: auf iOS blieb die feste Leiste danach auf halber Höhe hängen. Die Seite darf
  // hinter dem deckenden Player scrollen, beim Schließen steht sie wieder, wo sie war.
  scrolledTo = window.scrollY
})

onBeforeUnmount(() => {
  window.removeEventListener('popstate', onPopState)
  window.removeEventListener('keydown', onKey)
  window.scrollTo?.(0, scrolledTo)
})
</script>

<template>
  <div
    v-if="player.current"
    class="now-playing"
    :class="{ lyrics: showsLyrics, paused: !player.playing }"
    role="dialog"
    aria-modal="true"
    aria-label="Läuft gerade"
    data-testid="now-playing"
    :style="{ '--cover-colour': colour, transform: pull ? `translateY(${pull}px)` : undefined }"
  >
    <div class="backdrop" aria-hidden="true">
      <img v-if="cover && !coverMissing" :src="cover" alt="" />
      <div class="glow" />
    </div>

    <div class="top" @touchstart.passive="pullStart" @touchmove.passive="pullMove" @touchend="pullEnd">
      <Button icon="pi pi-chevron-down" text rounded aria-label="Player schließen" data-testid="now-playing-close" @click="shut" />
      <RouterLink
        :to="{ name: 'album', params: { id: player.current.albumId } }"
        class="min-w-0 flex-1 truncate text-center text-sm opacity-80 hover:underline"
        title="Zum Album"
      >
        {{ player.current.album }}
      </RouterLink>
      <Button icon="pi pi-list" text rounded aria-label="Warteschlange" data-testid="now-playing-queue" @click="emit('queue')" />
    </div>

    <div class="stage">
      <div class="cover-area" @touchstart.passive="pullStart" @touchmove.passive="pullMove" @touchend="pullEnd">
        <div class="cover-box">
          <img v-if="cover && !coverMissing" :src="cover" alt="" class="cover" @load="coverLoaded" @error="coverMissing = true" />
          <div v-else class="cover cover-empty"><i class="pi pi-headphones" aria-hidden="true" /></div>
        </div>
      </div>
      <div v-if="showsLyrics" class="lyrics-panel">
        <LyricsPanel :track-id="player.current.trackId" :position="position" @seek="emit('seek', $event)" />
      </div>
    </div>

    <div class="controls">
      <div class="flex items-center gap-3">
        <img v-if="showsLyrics && cover && !coverMissing" :src="cover" alt="" class="thumb" />
        <div class="min-w-0 flex-1">
          <div class="truncate text-xl font-bold" data-testid="now-playing-title">{{ player.current.title }}</div>
          <div class="truncate opacity-70">{{ player.current.artist }}</div>
        </div>
      </div>
      <Rating
        :model-value="ratings.of(player.current.fileId)"
        class="mt-2"
        aria-label="Bewertung"
        data-testid="now-playing-rating"
        @update:model-value="(value: number | null) => ratings.rate(player.current!.fileId, value ?? 0)"
      />
      <p v-if="error || ratings.error" class="mt-1 text-sm text-red-300">{{ error ?? ratings.error }}</p>

      <div ref="progress" class="mt-4">
        <Slider
          :model-value="Math.min(shownTime, duration || 1)"
          :min="0"
          :max="duration || 1"
          :step="0.1"
          :disabled="!duration"
          aria-label="Position"
          data-testid="now-playing-seek"
          @update:model-value="scrub"
          @slideend="scrubbed"
        />
        <div class="mt-2 flex justify-between text-xs tabular-nums opacity-70">
          <span>{{ clock(shownTime) }}</span>
          <span>{{ duration ? `−${clock(duration - shownTime)}` : '' }}</span>
        </div>
      </div>

      <div class="mt-1 flex items-center justify-between">
        <Button
          text
          rounded
          size="large"
          aria-label="Rest mischen"
          title="Rest mischen"
          :disabled="player.queue.length - player.index - 1 < 2"
          data-testid="now-playing-shuffle"
          @click="player.shuffleUpcoming()"
        >
          <template #icon="{ class: iconClass }"><ShuffleIcon :class="iconClass" /></template>
        </Button>
        <Button icon="pi pi-step-backward" text rounded size="large" aria-label="Zurück" @click="emit('previous')" />
        <Button
          :icon="player.playing ? 'pi pi-pause' : 'pi pi-play'"
          rounded
          class="play !size-16"
          :aria-label="player.playing ? 'Pause' : 'Abspielen'"
          data-testid="now-playing-toggle"
          @click="player.toggle()"
        />
        <Button icon="pi pi-step-forward" text rounded size="large" aria-label="Weiter" :disabled="!player.hasNext" @click="player.next()" />
        <Button
          icon="pi pi-replay"
          text
          rounded
          size="large"
          :class="{ on: player.repeat !== 'off' }"
          :badge="player.repeat === 'one' ? '1' : undefined"
          :aria-label="repeatLabel[player.repeat]"
          :title="repeatLabel[player.repeat]"
          :aria-pressed="player.repeat !== 'off'"
          data-testid="now-playing-repeat"
          @click="player.cycleRepeat()"
        />
      </div>

      <div class="switches">
        <Button
          icon="pi pi-align-left"
          label="Songtext"
          text
          size="small"
          :class="{ on: showsLyrics }"
          :aria-pressed="showsLyrics"
          data-testid="now-playing-lyrics"
          @click="toggleLyrics"
        />
        <Button icon="pi pi-list" label="Warteschlange" text size="small" @click="emit('queue')" />
      </div>
    </div>
  </div>
</template>

<style scoped>
/* Dunkel, egal welches Thema die Seite hat, wie die Player der Musik-Apps: die Farbe kommt vom Cover, und weiße
   Schrift liest sich auf jedem Cover, sobald es unscharf und abgedunkelt ist. PrimeVues Farben über seine Tokens. */
.now-playing {
  --p-button-text-primary-color: #fff;
  --p-button-text-primary-hover-background: rgb(255 255 255 / 0.1);
  --p-button-text-primary-active-background: rgb(255 255 255 / 0.18);
  --p-button-text-contrast-color: #fff;
  --p-button-text-contrast-hover-background: rgb(255 255 255 / 0.1);
  --p-rating-icon-color: rgb(255 255 255 / 0.45);
  --p-rating-icon-hover-color: #fff;
  --p-rating-icon-active-color: #fff;
  --p-slider-track-background: rgb(255 255 255 / 0.25);
  --p-slider-range-background: #fff;
  --p-slider-handle-background: #fff;
  --p-slider-handle-content-background: #fff;
  --p-slider-handle-hover-background: #fff;

  position: fixed;
  inset: 0;
  z-index: 50;
  display: flex;
  flex-direction: column;
  overflow: hidden;
  padding: env(safe-area-inset-top) max(1rem, env(safe-area-inset-right)) max(1rem, env(safe-area-inset-bottom))
    max(1rem, env(safe-area-inset-left));
  color: #fff;
  background: #0b0d12;
  color-scheme: dark;
  overscroll-behavior: contain;
  transition: transform 0.15s ease-out;
}

.backdrop {
  position: absolute;
  inset: 0;
  z-index: -1;
  overflow: hidden;
  pointer-events: none;
}

/* Einmal gezeichnet und danach nur noch zusammengesetzt: ein stehendes, unscharfes Bild kostet je Frame nichts. */
.backdrop img {
  position: absolute;
  inset: -20%;
  width: 140%;
  height: 140%;
  object-fit: cover;
  filter: blur(60px) saturate(1.4) brightness(0.45);
}

.glow {
  position: absolute;
  top: 85%;
  left: 30%;
  width: 90vmax;
  height: 90vmax;
  border-radius: 50%;
  transform: translate(-50%, -50%);
  background: radial-gradient(circle, color-mix(in srgb, var(--cover-colour) 35%, transparent), transparent 65%);
}

.top {
  display: flex;
  flex: none;
  align-items: center;
  gap: 0.5rem;
  width: 100%;
  max-width: 64rem;
  margin: 0 auto;
  padding-top: 0.5rem;
}

.stage {
  display: flex;
  flex: 1;
  justify-content: center;
  width: 100%;
  max-width: 64rem;
  min-height: 0;
  margin: 0 auto;
}

.cover-area {
  display: flex;
  flex: 1;
  align-items: center;
  justify-content: center;
  min-width: 0;
  padding: 1rem 0;
}

/* So groß, wie Platz ist, quadratisch; pausiert etwas kleiner wie in der Musik-App. */
.cover-box {
  width: min(100%, 28rem, 50vh);
  aspect-ratio: 1;
  transition: transform 0.4s cubic-bezier(0.34, 1.56, 0.64, 1);
}

.paused .cover-box {
  transform: scale(0.92);
}

.cover {
  width: 100%;
  height: 100%;
  border-radius: 0.75rem;
  object-fit: cover;
  box-shadow: 0 20px 60px rgb(0 0 0 / 0.5);
}

.cover-empty {
  display: flex;
  align-items: center;
  justify-content: center;
  font-size: 4rem;
  background: rgb(255 255 255 / 0.08);
}

.lyrics-panel {
  flex: 1;
  min-width: 0;
  overflow-y: auto;
  padding: 1rem 0 3rem;
  overscroll-behavior: contain;
  mask-image: linear-gradient(transparent, #000 1.5rem, #000 calc(100% - 3rem), transparent);
}

.thumb {
  width: 3rem;
  height: 3rem;
  flex: none;
  border-radius: 0.375rem;
  object-fit: cover;
}

.controls {
  flex: none;
  width: 100%;
  max-width: 28rem;
  margin: 0 auto;
}

/* Größe über die !-Utility, PrimeVues .p-button-icon-only steht sonst später im Dokument und gewinnt. */
.play {
  --p-button-primary-background: #fff;
  --p-button-primary-border-color: #fff;
  --p-button-primary-color: #0b0d12;
  --p-button-primary-hover-background: rgb(255 255 255 / 0.85);
  --p-button-primary-hover-border-color: transparent;
  --p-button-primary-hover-color: #0b0d12;
  --p-button-primary-active-background: rgb(255 255 255 / 0.75);
  --p-button-primary-active-color: #0b0d12;
}

.play :deep(.p-button-icon) {
  font-size: 1.5rem;
}

.switches {
  display: flex;
  justify-content: space-between;
  margin-top: 0.75rem;
}

.switches :deep(.p-button) {
  opacity: 0.55;
}

/* Über das Token, damit die Farbe auch beim Überfahren und Drücken bleibt. */
.switches :deep(.p-button.on),
.controls :deep(.p-button.on) {
  --p-button-text-primary-color: var(--cover-colour);
  opacity: 1;
}

/* Auf dem Telefon nimmt der Songtext den Platz des Covers ein; das Cover ist dann das kleine neben dem Titel. */
.lyrics .cover-area {
  display: none;
}

/* Mit Platz daneben bleibt das Cover, und der Text läuft neben ihm. */
@media (min-width: 900px) and (min-height: 500px) {
  .lyrics .cover-area {
    display: flex;
  }

  .lyrics .lyrics-panel {
    padding-left: 2rem;
  }

  .lyrics .thumb {
    display: none;
  }
}

/* Quer gehaltenes Telefon: Cover neben den Knöpfen, damit keins gequetscht wird. */
@media (orientation: landscape) and (max-height: 500px) {
  .now-playing {
    display: grid;
    grid-template-rows: auto 1fr;
    grid-template-columns: 1fr 1fr;
    column-gap: 1.5rem;
  }

  .top {
    grid-column: 1 / -1;
  }

  .controls {
    align-self: center;
  }
}

@media (prefers-reduced-motion: reduce) {
  .now-playing,
  .cover-box {
    transition: none;
  }
}
</style>

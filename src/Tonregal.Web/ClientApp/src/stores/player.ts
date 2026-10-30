// SPDX-License-Identifier: AGPL-3.0-or-later
import { defineStore } from 'pinia'
import { computed, ref, watch } from 'vue'
import type { AlbumDetail } from '@/api'

export interface QueueItem {
  fileId: number
  trackId: number
  title: string
  artist: string
  album: string
  albumId: number
  durationMs: number
}

const storageKey = 'tonregal.player'
const positionKey = 'tonregal.player.position'

/** Wie in der Musik-App: aus, die ganze Warteschlange, das laufende Stück. */
export type RepeatMode = 'off' | 'all' | 'one'
const repeatModes: RepeatMode[] = ['off', 'all', 'one']

/** Mischt eine Kopie (Fisher-Yates). */
export function shuffled<T>(items: readonly T[]): T[] {
  const copy = [...items]
  for (let i = copy.length - 1; i > 0; i--) {
    const j = Math.floor(Math.random() * (i + 1))
    ;[copy[i], copy[j]] = [copy[j]!, copy[i]!]
  }
  return copy
}

/** Tracks eines Albums in Abspielreihenfolge; Tracks ohne Datei fallen weg, je Track die bevorzugte Datei (FLAC zuerst). */
export function albumQueue(album: AlbumDetail): QueueItem[] {
  return album.tracks
    .filter((t) => t.files.length > 0)
    .map((t) => ({
      fileId: t.files[0]!.id,
      trackId: t.id,
      title: t.title,
      artist: t.artists.join(', ') || album.albumArtist.name,
      album: album.title,
      albumId: album.id,
      durationMs: t.durationMs,
    }))
}

/**
 * Queue und Abspielwunsch. Das &lt;audio&gt;-Element lebt in der PlayerBar und folgt diesem Store;
 * startToken steigt bei jedem „von vorn starten“, damit auch dasselbe Stück neu beginnt.
 */
export const usePlayerStore = defineStore('player', () => {
  const queue = ref<QueueItem[]>([])
  const index = ref(-1)
  const playing = ref(false)
  const startToken = ref(0)
  const repeat = ref<RepeatMode>('off')
  /** Der große Player über dem ganzen Bildschirm ist offen. */
  const expanded = ref(false)
  const current = computed(() => queue.value[index.value] ?? null)

  try {
    const saved = JSON.parse(localStorage.getItem(storageKey) ?? 'null')
    if (Array.isArray(saved?.queue)) {
      queue.value = saved.queue
      index.value = Math.min(saved.index ?? 0, saved.queue.length - 1)
    }
    if (repeatModes.includes(saved?.repeat)) repeat.value = saved.repeat
  } catch {
    // ohne Speicher eben mit leerer Queue
  }
  watch([queue, index, repeat], () => {
    try {
      localStorage.setItem(storageKey, JSON.stringify({ queue: queue.value, index: index.value, repeat: repeat.value }))
    } catch {
      // privates Fenster o. ä.
    }
  }, { deep: true })

  // Reihenfolge zählt: erst die neue Quelle laden, dann abspielen – so ruft die PlayerBar play() genau einmal.
  function start(at: number) {
    index.value = at
    startToken.value++
    playing.value = at >= 0
  }

  function play(items: QueueItem[], at = 0) {
    queue.value = [...items]
    start(items.length ? Math.max(0, Math.min(at, items.length - 1)) : -1)
  }

  function playAlbum(album: AlbumDetail, trackId?: number) {
    const items = albumQueue(album)
    play(items, Math.max(0, items.findIndex((i) => i.trackId === trackId)))
  }

  function playShuffled(items: QueueItem[]) {
    play(shuffled(items))
  }

  /** Mischt, was nach dem laufenden Stück kommt. */
  function shuffleUpcoming() {
    const at = index.value + 1
    queue.value.splice(at, queue.value.length - at, ...shuffled(queue.value.slice(at)))
  }

  function cycleRepeat() {
    repeat.value = repeatModes[(repeatModes.indexOf(repeat.value) + 1) % repeatModes.length]!
  }

  const hasNext = computed(() => index.value < queue.value.length - 1 || (repeat.value === 'all' && queue.value.length > 0))

  /** Hinten anhängen; bei leerer Queue gleich abspielen. */
  function enqueue(items: QueueItem[]) {
    const wasEmpty = current.value === null
    queue.value.push(...items)
    if (wasEmpty && items.length) start(queue.value.length - items.length)
  }

  /** Direkt nach dem laufenden Stück einreihen. */
  function playNext(items: QueueItem[]) {
    if (current.value === null) return enqueue(items)
    queue.value.splice(index.value + 1, 0, ...items)
  }

  /** Weiter per Knopf; „ein Stück wiederholen“ gilt hier nicht, sonst käme man nie weiter. */
  function next() {
    if (index.value < queue.value.length - 1) start(index.value + 1)
    else if (repeat.value === 'all' && queue.value.length) start(0)
    else playing.value = false
  }

  /** Das Stück ist zu Ende gespielt. */
  function finished() {
    if (repeat.value === 'one') start(index.value)
    else next()
  }

  function previous() {
    start(Math.max(0, index.value - 1))
  }

  function jump(at: number) {
    start(at)
  }

  function remove(at: number) {
    queue.value.splice(at, 1)
    if (at < index.value) index.value--
    else if (at === index.value) {
      if (index.value >= queue.value.length) {
        index.value = queue.value.length - 1
        playing.value = false
      }
      startToken.value++
    }
  }

  /**
   * Verschiebt einen Eintrag; `to` ist die Position, die er danach hat. Dasselbe Stück kann mehrfach in der Queue
   * stehen, daher wandert der Index mit, statt das laufende Stück zu suchen.
   */
  function move(from: number, to: number) {
    const items = queue.value
    if (from === to || from < 0 || to < 0 || from >= items.length || to >= items.length) return
    const [moved] = items.splice(from, 1)
    items.splice(to, 0, moved!)
    if (index.value === from) index.value = to
    else if (from < index.value && to >= index.value) index.value--
    else if (from > index.value && to <= index.value) index.value++
  }

  /**
   * Merkt sich, wo das laufende Stück steht, damit es nach dem Neuladen dort weitergeht wie in der App. Eigener
   * Schlüssel, weil die Stelle sich ständig ändert und die Queue dafür nicht jedes Mal neu geschrieben werden soll.
   */
  function rememberPosition(seconds: number) {
    if (!current.value) return
    try {
      localStorage.setItem(positionKey, JSON.stringify({ fileId: current.value.fileId, index: index.value, seconds }))
    } catch {
      // privates Fenster o. ä.
    }
  }

  /** Die gemerkte Stelle, wenn sie zum laufenden Eintrag gehört, sonst 0. */
  function savedPosition(): number {
    try {
      const saved = JSON.parse(localStorage.getItem(positionKey) ?? 'null')
      if (saved && current.value && saved.fileId === current.value.fileId && saved.index === index.value && saved.seconds > 0) {
        return Number(saved.seconds)
      }
    } catch {
      // nichts gemerkt
    }
    return 0
  }

  function clear() {
    queue.value = []
    index.value = -1
    playing.value = false
    expanded.value = false
  }

  function toggle() {
    if (current.value) playing.value = !playing.value
  }

  return {
    queue,
    index,
    playing,
    startToken,
    repeat,
    expanded,
    current,
    hasNext,
    play,
    playAlbum,
    playShuffled,
    shuffleUpcoming,
    cycleRepeat,
    enqueue,
    playNext,
    next,
    finished,
    previous,
    jump,
    remove,
    move,
    rememberPosition,
    savedPosition,
    clear,
    toggle,
  }
})

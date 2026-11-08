// SPDX-License-Identifier: AGPL-3.0-or-later
import { computed, ref, useTemplateRef } from 'vue'

/**
 * Eine Liste am Griff umsortieren, mit Maus, Finger oder Stift über Pointer Events (die auch Safari auf dem iPhone kann)
 * oder per Pfeiltaste. Die Zeilen tragen `ref="rows"` (im `v-for`), der Griff `data-handle`; `move(from, to)` setzt die Änderung um,
 * `to` ist die Position danach. Die Warteschlange und die Playlists teilen sich das.
 */
export function useReorder(count: () => number, move: (from: number, to: number) => void) {
  const dragFrom = ref(-1)
  const dragTo = ref(-1)
  const rowRefs = useTemplateRef<HTMLElement[]>('rows')
  const rows = computed(() => rowRefs.value ?? [])

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
    const [from, to] = [dragFrom.value, dragTo.value]
    dragFrom.value = dragTo.value = -1
    if (from >= 0 && to >= 0 && from !== to) move(from, to)
  }

  function moveByKey(event: KeyboardEvent, at: number) {
    const to = event.key === 'ArrowUp' ? at - 1 : event.key === 'ArrowDown' ? at + 1 : -1
    if (to < 0 || to >= count()) return
    event.preventDefault()
    move(at, to)
    // Der Fokus folgt dem Eintrag, damit sich mehrfach drücken lässt.
    requestAnimationFrame(() => rows.value[to]?.querySelector<HTMLElement>('[data-handle]')?.focus())
  }

  return { dragFrom, dragTo, dragStart, dragMove, dragEnd, moveByKey }
}

/** Kopie mit dem Eintrag von `from` an Position `to`. */
export function moved<T>(items: readonly T[], from: number, to: number): T[] {
  const copy = [...items]
  const [item] = copy.splice(from, 1)
  copy.splice(to, 0, item!)
  return copy
}

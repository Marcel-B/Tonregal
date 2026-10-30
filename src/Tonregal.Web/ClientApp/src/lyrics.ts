// SPDX-License-Identifier: AGPL-3.0-or-later
import type { LyricLine, LyricsSource } from '@/api'

/** Index der Zeile, die bei `ms` gesungen wird (die letzte, die schon begonnen hat); -1 davor oder ohne Zeiten. */
export function currentLineIndex(lines: LyricLine[], ms: number): number {
  let low = 0
  let high = lines.length - 1
  let found = -1
  while (low <= high) {
    const mid = (low + high) >> 1
    const time = lines[mid]!.timeMs
    if (time === null) return -1
    if (time <= ms) {
      found = mid
      low = mid + 1
    } else high = mid - 1
  }
  return found
}

export const lyricsSourceLabel: Record<LyricsSource, string> = {
  None: '',
  Sidecar: '.lrc-Datei',
  Tag: 'in der Datei eingebettet',
  Lrclib: 'lrclib.net',
}

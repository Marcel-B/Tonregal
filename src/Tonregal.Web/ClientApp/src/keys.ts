// SPDX-License-Identifier: AGPL-3.0-or-later
/** Tonarten wie die Analyse sie schreibt (analyze.py), Dur ohne, Moll mit „m“. */
const NAMES = ['C', 'C#', 'D', 'Eb', 'E', 'F', 'F#', 'G', 'Ab', 'A', 'Bb', 'B']
const ALIASES: Record<string, number> = { Db: 1, 'D#': 3, Gb: 6, 'G#': 8, 'A#': 10 }

export const allKeys = [...NAMES, ...NAMES.map((n) => `${n}m`)]

function parse(key: string): { pitch: number; minor: boolean } | null {
  const match = /^([A-G][#b]?)(m|min|moll)?$/i.exec(key.trim())
  if (!match) return null
  const root = match[1]!.charAt(0).toUpperCase() + match[1]!.slice(1)
  const pitch = NAMES.indexOf(root) >= 0 ? NAMES.indexOf(root) : ALIASES[root]
  return pitch === undefined ? null : { pitch, minor: match[2] !== undefined }
}

const name = (pitch: number, minor: boolean) => `${NAMES[((pitch % 12) + 12) % 12]}${minor ? 'm' : ''}`

/**
 * Harmonisch passende Tonarten nach dem Quintenzirkel (Camelot-Nachbarn): die Tonart selbst, eine Quinte
 * hoch und runter und die Paralleltonart. Unbekannte Schreibweisen bleiben, wie sie sind.
 */
export function compatibleKeys(key: string): string[] {
  const parsed = parse(key)
  if (!parsed) return [key]
  const { pitch, minor } = parsed
  return [name(pitch, minor), name(pitch + 7, minor), name(pitch - 7, minor), name(minor ? pitch + 3 : pitch - 3, !minor)]
}

// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from 'vitest'
import { currentLineIndex } from './lyrics'

const lines = [
  { timeMs: 1000, text: 'eins' },
  { timeMs: 4000, text: 'zwei' },
  { timeMs: 4000, text: 'zwei b' },
  { timeMs: 9000, text: '' },
]

describe('currentLineIndex', () => {
  it('findet die zuletzt begonnene Zeile', () => {
    expect(currentLineIndex(lines, 0)).toBe(-1)
    expect(currentLineIndex(lines, 1000)).toBe(0)
    expect(currentLineIndex(lines, 3999)).toBe(0)
    expect(currentLineIndex(lines, 5000)).toBe(2)
    expect(currentLineIndex(lines, 60_000)).toBe(3)
  })

  it('ohne Zeiten keine Zeile', () => {
    expect(currentLineIndex([{ timeMs: null, text: 'a' }], 5000)).toBe(-1)
    expect(currentLineIndex([], 5000)).toBe(-1)
  })
})

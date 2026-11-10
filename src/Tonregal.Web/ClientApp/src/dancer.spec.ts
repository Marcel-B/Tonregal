// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from 'vitest'
import { nextMove } from './dancer'

describe('nextMove', () => {
  it('nimmt nie dieselbe Bewegung zweimal hintereinander', () => {
    const names = ['Tritt', 'Schlag', 'Kniebeuge']
    for (let i = 0; i < 50; i++) expect(nextMove(names, 'Schlag')).not.toBe('Schlag')
    expect(nextMove(names, 'Tritt', () => 0)).toBe('Schlag')
  })

  it('wiederholt eine einzige Bewegung und kommt ohne aus', () => {
    expect(nextMove(['Tritt'], 'Tritt')).toBe('Tritt')
    expect(nextMove([], null)).toBeNull()
  })
})

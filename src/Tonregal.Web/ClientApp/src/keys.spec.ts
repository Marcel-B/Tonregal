// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from 'vitest'
import { allKeys, compatibleKeys } from './keys'

describe('compatibleKeys', () => {
  it('nennt Quinte hoch, Quinte runter und die Paralleltonart', () => {
    expect(compatibleKeys('Am')).toEqual(['Am', 'Em', 'Dm', 'C'])
    expect(compatibleKeys('C')).toEqual(['C', 'G', 'F', 'Am'])
    expect(compatibleKeys('F#m')).toEqual(['F#m', 'C#m', 'Bm', 'A'])
  })

  it('versteht Erniedrigungen und lässt Fremdes stehen', () => {
    expect(compatibleKeys('Gb')).toEqual(['F#', 'C#', 'B', 'Ebm'])
    expect(compatibleKeys('8A')).toEqual(['8A'])
  })

  it('kennt alle 24 Tonarten', () => {
    expect(allKeys).toHaveLength(24)
  })
})

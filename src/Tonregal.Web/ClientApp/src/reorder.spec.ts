// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from 'vitest'
import { moved } from './reorder'

describe('moved', () => {
  it('setzt einen Eintrag an seine neue Position und lässt das Original stehen', () => {
    const items = ['a', 'b', 'c', 'd']
    expect(moved(items, 0, 2)).toEqual(['b', 'c', 'a', 'd'])
    expect(moved(items, 3, 0)).toEqual(['d', 'a', 'b', 'c'])
    expect(items).toEqual(['a', 'b', 'c', 'd'])
  })
})

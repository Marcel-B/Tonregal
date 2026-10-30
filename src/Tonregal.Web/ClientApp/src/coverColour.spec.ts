// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from 'vitest'
import { dominantHue, fallbackColour } from './coverColour'

const pixels = (...rgb: [number, number, number][]) => rgb.flatMap(([r, g, b]) => [r, g, b, 255])

describe('dominantHue', () => {
  it('nimmt den kräftigsten Farbton, auch wenn trübe Pixel überwiegen', () => {
    const grey = Array.from({ length: 20 }, () => [120, 110, 115] as [number, number, number])
    const red = Array.from({ length: 5 }, () => [230, 20, 20] as [number, number, number])
    const colour = dominantHue(pixels(...grey, ...red))
    const [r, g, b] = [1, 3, 5].map((i) => parseInt(colour.slice(i, i + 2), 16))
    expect(r).toBeGreaterThan(g! + 80)
    expect(r).toBeGreaterThan(b! + 80)
  })

  it('fällt bei einem grauen Cover auf die Primärfarbe zurück', () => {
    expect(dominantHue(pixels([40, 40, 40], [200, 200, 200]))).toBe(fallbackColour)
  })
})

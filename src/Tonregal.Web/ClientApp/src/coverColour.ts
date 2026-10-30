// SPDX-License-Identifier: AGPL-3.0-or-later

/** Ohne Cover oder bei einem grauen Cover: Auras Smaragd, die Primärfarbe der Seite. */
export const fallbackColour = '#34d399'

/**
 * Der kräftigste Farbton eines Covers als helle Akzentfarbe, wie Tonwerk-AI sie für seinen großen Player nimmt: die
 * Pixel nach Farbton in 10°-Fächer, gewichtet mit der Buntheit im Quadrat, damit wenige kräftige Pixel mehr zählen als
 * viele trübe.
 */
export function dominantHue(pixels: ArrayLike<number>): string {
  const bins = new Array<number>(36).fill(0)
  const sums = new Array<number>(36).fill(0)
  let total = 0
  for (let i = 0; i + 2 < pixels.length; i += 4) {
    const r = pixels[i]! / 255
    const g = pixels[i + 1]! / 255
    const b = pixels[i + 2]! / 255
    const max = Math.max(r, g, b)
    const chroma = max - Math.min(r, g, b)
    if (chroma < 0.08) continue
    const hue = (max === r ? ((g - b) / chroma + 6) % 6 : max === g ? (b - r) / chroma + 2 : (r - g) / chroma + 4) * 60
    const weight = chroma * chroma
    const bin = Math.floor(hue / 10) % 36
    bins[bin]! += weight
    sums[bin]! += weight * hue
    total += weight
  }
  if (total < 0.5) return fallbackColour
  const best = bins.indexOf(Math.max(...bins))
  return hslToHex(sums[best]! / bins[best]!, 0.75, 0.65)
}

/** Liest ein geladenes Cover verkleinert aus; wirft, wenn der Canvas es nicht lesen darf. */
export function coverColour(image: CanvasImageSource): string {
  const size = 24
  const canvas = document.createElement('canvas')
  canvas.width = canvas.height = size
  const context = canvas.getContext('2d', { willReadFrequently: true })
  if (!context) return fallbackColour
  context.drawImage(image, 0, 0, size, size)
  return dominantHue(context.getImageData(0, 0, size, size).data)
}

function hslToHex(hue: number, saturation: number, lightness: number): string {
  const a = saturation * Math.min(lightness, 1 - lightness)
  const channel = (n: number) => {
    const k = (n + hue / 30) % 12
    const value = lightness - a * Math.max(-1, Math.min(k - 3, 9 - k, 1))
    return Math.round(value * 255)
      .toString(16)
      .padStart(2, '0')
  }
  return `#${channel(0)}${channel(8)}${channel(4)}`
}

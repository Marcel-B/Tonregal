// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from 'vitest'
import { plan, uploadFolder } from './upload'

const file = (name: string) => new File(['x'], name)

describe('upload', () => {
  it('legt lose Dateien gemeinsam in einen Ordner und lässt Ordner-Pfade, wie sie sind', () => {
    const result = plan(
      [
        { file: file('01.mp3'), path: '01.mp3' },
        { file: file('02.FLAC'), path: 'Album/02.FLAC' },
        { file: file('cover.jpg'), path: 'Album/cover.jpg' },
        { file: file('.DS_Store'), path: 'Album/.DS_Store' },
        { file: file('x.mp3'), path: 'Album/.versteckt/x.mp3' },
      ],
      'Upload 2026-10-25 23-50',
    )

    expect(result.items.map((i) => i.path)).toEqual(['Upload 2026-10-25 23-50/01.mp3', 'Album/02.FLAC'])
    expect(result.skipped).toBe(3)
  })

  it('benennt den Upload-Ordner nach Datum und Uhrzeit', () => {
    expect(uploadFolder(new Date(2026, 9, 5, 7, 3))).toBe('Upload 2026-10-05 07-03')
  })
})

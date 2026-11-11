// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from 'vitest'
import type { AlbumDetail } from '@/api'
import { changes, formFor, resultText, splitArtists, validate } from './tagEdit'

const file = { id: 1, format: 'Mp3' as const, bitrate: 320, sampleRate: 44100, sizeBytes: 1, path: 'a.mp3' }
const album: AlbumDetail = {
  id: 7,
  title: 'Testalbum',
  year: 2024,
  kind: 'Album',
  discCount: 1,
  albumArtist: { id: 1, name: 'Testband' },
  tracks: [
    { id: 1, disc: 1, number: 1, title: 'Erster Song', artists: ['Testband'], durationMs: 1, bpm: null, initialKey: null, genre: 'Rock', files: [file] },
    { id: 2, disc: 1, number: 2, title: 'Zweiter Song', artists: ['Testband', 'Gast'], durationMs: 1, bpm: null, initialKey: null, genre: 'Pop', files: [file] },
  ],
  canFetchCover: false,
  releaseCount: 1,
}

describe('Tag-Editor', () => {
  it('trennt Interpreten am Semikolon, nicht am Komma', () => {
    expect(splitArtists('Crosby, Stills & Nash; Gast ;; Gast')).toEqual(['Crosby, Stills & Nash', 'Gast'])
  })

  it('schickt beim Album nur Geändertes und lässt verschiedene Genres stehen', () => {
    const original = formFor(album, null)
    expect(original.genre).toBe('')
    expect(changes({ ...original, album: ' Neues Album ', year: '2025' }, original, false)).toEqual({ album: 'Neues Album', year: 2025 })
    expect(changes({ ...original, genre: 'Jazz', title: 'ignoriert' }, original, false)).toEqual({ genre: 'Jazz' })
  })

  it('entfernt Jahr und Genre eines Tracks mit leeren Feldern', () => {
    const original = formFor(album, album.tracks[0])
    expect(original).toMatchObject({ title: 'Erster Song', artists: 'Testband', number: '1', disc: '1', genre: 'Rock', year: '2024' })
    expect(changes({ ...original, year: '', genre: '' }, original, true)).toEqual({ year: 0, genre: '' })
    expect(changes({ ...original, artists: 'Testband; Gast', number: '7' }, original, true)).toEqual({ artists: ['Testband', 'Gast'], number: 7 })
  })

  it('prüft die Eingaben', () => {
    const track = formFor(album, album.tracks[1])
    expect(validate(track, true)).toBeNull()
    expect(validate({ ...track, title: ' ' }, true)).toContain('Titel')
    expect(validate({ ...track, artists: ' ; ' }, true)).toContain('Interpreten')
    expect(validate({ ...track, year: '20x4' }, true)).toContain('Jahr')
    expect(validate({ ...track, disc: '0' }, true)).toContain('Disc')
    expect(validate({ ...formFor(album, null), album: '' }, false)).toContain('Albumtitel')
  })

  it('beschreibt das Ergebnis', () => {
    expect(resultText(0, 0)).toContain('Nichts')
    expect(resultText(1, 0)).toBe('1 Datei geändert.')
    expect(resultText(3, 3)).toBe('3 Dateien geändert, 3 nach der Ordnerregel umbenannt.')
  })
})

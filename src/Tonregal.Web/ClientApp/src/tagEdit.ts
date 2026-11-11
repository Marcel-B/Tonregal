// SPDX-License-Identifier: AGPL-3.0-or-later
import type { AlbumDetail, TagEdit, TrackDto } from '@/api'

/** Die Felder des Tag-Editors als Text, so wie sie im Formular stehen. */
export interface TagForm {
  title: string
  artists: string
  albumArtist: string
  album: string
  year: string
  genre: string
  number: string
  disc: string
}

/** Trennt mehrere Interpreten; ein Komma kommt in Namen vor („Crosby, Stills & Nash“). */
export const ArtistSeparator = ';'

export function splitArtists(text: string): string[] {
  return text
    .split(ArtistSeparator)
    .map((a) => a.trim())
    .filter((a, i, all) => a.length > 0 && all.indexOf(a) === i)
}

/** Ein gemeinsames Genre aller Tracks, sonst leer; `mixed` sagt, ob sie verschiedene haben. */
export function albumGenre(album: AlbumDetail): { genre: string; mixed: boolean } {
  const genres = [...new Set(album.tracks.map((t) => t.genre ?? ''))]
  return genres.length === 1 ? { genre: genres[0], mixed: false } : { genre: '', mixed: genres.length > 1 }
}

export function formFor(album: AlbumDetail, track: TrackDto | null): TagForm {
  return {
    title: track?.title ?? '',
    artists: track ? track.artists.join(`${ArtistSeparator} `) : '',
    albumArtist: album.albumArtist.name,
    album: album.title,
    year: album.year ? String(album.year) : '',
    genre: track ? (track.genre ?? '') : albumGenre(album).genre,
    number: track ? String(track.number || '') : '',
    disc: track ? String(track.disc) : '',
  }
}

/** Ein Fehlertext oder `null`, wenn sich das Formular so speichern lässt. */
export function validate(form: TagForm, single: boolean): string | null {
  if (single && !form.title.trim()) return 'Der Titel darf nicht leer sein.'
  if (single && splitArtists(form.artists).length === 0) return 'Ein Track braucht mindestens einen Interpreten.'
  if (!form.albumArtist.trim()) return 'Der Album-Interpret darf nicht leer sein.'
  if (!form.album.trim()) return 'Der Albumtitel darf nicht leer sein.'
  if (form.year.trim() && !/^\d{1,4}$/.test(form.year.trim())) return 'Das Jahr ist eine Zahl bis 9999.'
  if (single && form.number.trim() && !/^\d{1,3}$/.test(form.number.trim())) return 'Die Nummer ist eine Zahl bis 999.'
  if (single && (!/^\d{1,2}$/.test(form.disc.trim()) || Number(form.disc) < 1)) return 'Die Disc ist eine Zahl von 1 bis 99.'
  return null
}

/**
 * Nur was sich gegenüber dem Stand geändert hat, damit eine Album-Änderung nicht nebenbei die Genres aller Tracks
 * gleichzieht. Ein leeres Genre beim Album zählt nur, wenn es vorher ein gemeinsames gab.
 */
export function changes(form: TagForm, original: TagForm, single: boolean): TagEdit {
  const edit: TagEdit = {}
  const text = (key: keyof TagForm) => (form[key].trim() !== original[key].trim() ? form[key].trim() : undefined)
  const number = (key: keyof TagForm) => (form[key].trim() !== original[key].trim() ? Number(form[key].trim() || 0) : undefined)
  if (single) {
    edit.title = text('title')
    if (splitArtists(form.artists).join('\u0000') !== splitArtists(original.artists).join('\u0000')) edit.artists = splitArtists(form.artists)
    edit.number = number('number')
    edit.disc = number('disc')
  }
  edit.albumArtist = text('albumArtist')
  edit.album = text('album')
  edit.year = number('year')
  if (form.genre.trim() !== original.genre.trim()) edit.genre = form.genre.trim()
  return Object.fromEntries(Object.entries(edit).filter(([, v]) => v !== undefined)) as TagEdit
}

export function resultText(changed: number, moved: number): string {
  if (changed === 0) return 'Nichts geändert, die Dateien stimmen schon.'
  const files = changed === 1 ? '1 Datei' : `${changed} Dateien`
  return moved > 0 ? `${files} geändert, ${moved} nach der Ordnerregel umbenannt.` : `${files} geändert.`
}

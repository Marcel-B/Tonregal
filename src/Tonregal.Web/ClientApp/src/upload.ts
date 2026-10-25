// SPDX-License-Identifier: AGPL-3.0-or-later
import { isAudioFile } from '@/api'

/** Eine Datei mit ihrem Pfad relativ zur Inbox. */
export interface UploadItem {
  file: File
  path: string
}

/** Ordnername für lose Dateien, z. B. „Upload 2026-10-25 23-50“. */
export function uploadFolder(date = new Date()) {
  const pad = (n: number) => String(n).padStart(2, '0')
  return `Upload ${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(date.getHours())}-${pad(date.getMinutes())}`
}

/**
 * Lässt nur Audiodateien durch. Lose Dateien kommen gemeinsam in `folder`, damit
 * sie als ein Album-Kandidat gelten; Pfade aus einem Ordner bleiben unverändert.
 */
export function plan(items: UploadItem[], folder: string): { items: UploadItem[]; skipped: number } {
  const audio = items.filter((i) => isAudioFile(i.file.name) && !i.path.split('/').some((s) => s.startsWith('.')))
  return {
    items: audio.map((i) => ({ file: i.file, path: i.path.includes('/') ? i.path : `${folder}/${i.path}` })),
    skipped: items.length - audio.length,
  }
}

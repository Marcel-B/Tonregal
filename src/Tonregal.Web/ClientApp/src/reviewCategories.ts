// SPDX-License-Identifier: AGPL-3.0-or-later
import type { ImportJob } from '@/api'

/** Warum eine Datei geprüft werden muss, ohne Dauer, Score und Namen: so lassen sich Fälle bündeln. */
export interface ReviewCategory {
  key: string
  label: string
  /** Mit Vorschlag: wird mit dem vorgeschlagenen Treffer übernommen. */
  withSuggestion: ImportJob[]
  /** Ohne Vorschlag: nur mit Titel, Interpret und Album aus den Tags. */
  withoutSuggestion: ImportJob[]
}

// Reihenfolge zählt: Die Tag-Suche hängt ihren Teil an den ursprünglichen Grund an.
const rules: [key: string, label: string, test: RegExp][] = [
  ['tagsOnly', 'Vorschlag nur über die Tags gefunden', /Vorschlag nur über die Tags gefunden/],
  ['tagsDuration', 'Dauer passt zu keinem Treffer über die Tags', /passt zu keinem Treffer über die Tags/],
  ['duration', 'Dauer passt zu keinem Treffer', /^Dauer .* passt zu keinem Treffer$/],
  ['unknown', 'AcoustID kennt die Aufnahme nicht', /^AcoustID kennt diese Aufnahme nicht/],
  ['score', 'Score zu niedrig', /^Score /],
  ['mbid', 'MusicBrainz-ID in den Tags passt nicht', /^MusicBrainz-ID in den Tags/],
  ['tags', 'Tags passen zu keinem Treffer', /^Tags passen zu keinem Treffer/],
  ['artist', 'Interpret passt zu keinem Treffer', /^Interpret passt zu keinem Treffer/],
  ['ambiguous', 'Fingerprint passt zu mehreren Songs', /^Fingerprint passt auch zu/],
  ['unreadable', 'Datei nicht lesbar', /^Datei nicht lesbar/],
]

export function reviewCategory(reason: string | null): { key: string; label: string } {
  const text = reason?.trim() ?? ''
  for (const [key, label, test] of rules) if (test.test(text)) return { key, label }
  return { key: 'other', label: 'Sonstiges' }
}

/** Ein Vorschlag zählt nur, wenn er auf einem Release steht; sonst gibt es nichts zu übernehmen. */
export const hasSuggestion = (job: ImportJob) => !!job.selected?.releaseId

/** Die zu prüfenden Dateien nach Grund gebündelt, die größten Gruppen zuerst. */
export function reviewCategories(jobs: ImportJob[]): ReviewCategory[] {
  const groups = new Map<string, ReviewCategory>()
  for (const job of jobs) {
    if (job.state !== 'NeedsReview') continue
    const { key, label } = reviewCategory(job.reviewReason)
    let group = groups.get(key)
    if (!group) groups.set(key, (group = { key, label, withSuggestion: [], withoutSuggestion: [] }))
    ;(hasSuggestion(job) ? group.withSuggestion : group.withoutSuggestion).push(job)
  }
  const size = (g: ReviewCategory) => g.withSuggestion.length + g.withoutSuggestion.length
  return [...groups.values()].sort((a, b) => size(b) - size(a) || a.label.localeCompare(b.label))
}

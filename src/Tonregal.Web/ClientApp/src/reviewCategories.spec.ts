// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from 'vitest'
import type { ImportJob } from '@/api'
import { reviewCategories, reviewCategory } from './reviewCategories'

const job = (id: number, reviewReason: string | null, releaseId: string | null = 'r1'): ImportJob =>
  ({
    id,
    state: 'NeedsReview',
    reviewReason,
    selected: { recordingId: 'x', releaseId } as ImportJob['selected'],
  }) as ImportJob

describe('reviewCategory', () => {
  it('bündelt Gründe ohne Dauer, Score und Namen', () => {
    expect(reviewCategory('Dauer 3:01 passt zu keinem Treffer').key).toBe('duration')
    expect(reviewCategory('Dauer 12:45 passt zu keinem Treffer').key).toBe('duration')
    expect(reviewCategory('Score 61 % unter 85 %').key).toBe('score')
    expect(reviewCategory('Tags passen zu keinem Treffer: „Intro“').key).toBe('tags')
    expect(reviewCategory('Fingerprint passt auch zu „X“ von Y').key).toBe('ambiguous')
    expect(reviewCategory('AcoustID kennt diese Aufnahme nicht').key).toBe('unknown')
    expect(reviewCategory(null).key).toBe('other')
  })

  it('erkennt den Zusatz der Tag-Suche vor dem ursprünglichen Grund', () => {
    expect(reviewCategory('AcoustID kennt diese Aufnahme nicht; Vorschlag nur über die Tags gefunden').key).toBe('tagsOnly')
    expect(reviewCategory('AcoustID kennt diese Aufnahme nicht; Dauer 3:01 passt zu keinem Treffer über die Tags').key).toBe('tagsDuration')
  })
})

describe('reviewCategories', () => {
  it('trennt mit und ohne Vorschlag und stellt große Gruppen nach vorn', () => {
    const groups = reviewCategories([
      job(1, 'Score 61 % unter 85 %'),
      job(2, 'Dauer 3:01 passt zu keinem Treffer'),
      job(3, 'Dauer 4:10 passt zu keinem Treffer'),
      job(4, 'Dauer 5:00 passt zu keinem Treffer', null),
      { ...job(5, 'Dauer 5:00 passt zu keinem Treffer'), state: 'Matched' },
    ])
    expect(groups.map((g) => g.key)).toEqual(['duration', 'score'])
    expect(groups[0]!.withSuggestion.map((j) => j.id)).toEqual([2, 3])
    expect(groups[0]!.withoutSuggestion.map((j) => j.id)).toEqual([4])
  })
})

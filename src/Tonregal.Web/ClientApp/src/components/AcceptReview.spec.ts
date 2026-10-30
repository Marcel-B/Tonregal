// SPDX-License-Identifier: AGPL-3.0-or-later
import { afterEach, describe, expect, it, vi } from 'vitest'
import { enableAutoUnmount, flushPromises, mount } from '@vue/test-utils'
import PrimeVue from 'primevue/config'
import AcceptReview from './AcceptReview.vue'
import type { AcceptReviewResponse, ImportJob } from '@/api'

const job = (id: number, reviewReason: string, releaseId: string | null = 'rel1'): ImportJob => ({
  id, path: `Alt/${id}.mp3`, group: 'Alt', state: 'NeedsReview', format: 'Mp3', durationMs: 181_000,
  tagTitle: null, tagArtist: null, tagAlbum: null, matchScore: 0.9, reviewReason, error: null,
  selected: releaseId ? ({ recordingId: `r${id}`, releaseId } as ImportJob['selected']) : null,
  candidateCount: 1, startedAt: '2026-10-30T18:00:00Z',
})

const jobs = [
  job(1, 'Dauer 3:01 passt zu keinem Treffer'),
  job(2, 'Dauer 4:12 passt zu keinem Treffer'),
  job(3, 'Score 61 % unter 85 %'),
  job(4, 'AcoustID kennt diese Aufnahme nicht', null),
]

function mountButton() {
  return mount(AcceptReview, { props: { jobs }, global: { plugins: [PrimeVue], stubs: { teleport: true } }, attachTo: document.body })
}

enableAutoUnmount(afterEach)

describe('AcceptReview', () => {
  it('zeigt die Gründe und übernimmt nach Bestätigung alle', async () => {
    const result: AcceptReviewResponse = {
      accepted: 3, unmatched: 1, skipped: 0,
      status: { running: true, currentPath: null, processed: 0, waiting: 0, imported: 0, importsPending: 4, lastRunAt: null, lastError: null, importFailed: 0 },
    }
    const fetch = vi.fn(async () => new Response(JSON.stringify(result)))
    vi.stubGlobal('fetch', fetch)

    const wrapper = mountButton()
    expect(wrapper.find('button[data-testid="accept-review"]').text()).toBe('Alle zu prüfenden übernehmen (4)')
    await wrapper.find('button[data-testid="accept-review"]').trigger('click')

    expect(wrapper.find('[data-testid="accept-category-duration"]').text()).toContain('2 mit Vorschlag')
    expect(wrapper.find('[data-testid="accept-category-unknown"]').text()).toContain('0 mit Vorschlag · 1 ohne')
    expect(wrapper.find('[data-testid="accept-summary"]').text()).toBe('4 von 4 Datei(en) werden übernommen, davon 1 mit ihren Tags.')
    // Nichts passiert ohne Bestätigung.
    expect(fetch).not.toHaveBeenCalled()

    await wrapper.find('button[data-testid="accept-confirm"]').trigger('click')
    await flushPromises()

    const [url, init] = fetch.mock.calls[0] as unknown as [string, RequestInit]
    expect(url).toBe('/api/inbox/accept')
    expect(JSON.parse(init.body as string)).toEqual({ jobIds: [1, 2, 3, 4], withoutSuggestion: true })
    expect(wrapper.find('[data-testid="accept-result"]').text()).toContain('4 Datei(en) werden übernommen')
    expect(wrapper.emitted('done')).toHaveLength(1)
  })

  it('lässt Gründe und Dateien ohne Vorschlag weg', async () => {
    const fetch = vi.fn(async () => new Response(JSON.stringify({ accepted: 1, unmatched: 0, skipped: 0, status: {} })))
    vi.stubGlobal('fetch', fetch)

    const wrapper = mountButton()
    await wrapper.find('button[data-testid="accept-review"]').trigger('click')
    await wrapper.find('#accept-duration').setValue(false)
    await wrapper.find('#accept-without-suggestion').setValue(false)

    expect(wrapper.find('[data-testid="accept-summary"]').text()).toBe('1 von 4 Datei(en) werden übernommen.')
    await wrapper.find('button[data-testid="accept-confirm"]').trigger('click')
    await flushPromises()

    const [, init] = fetch.mock.calls[0] as unknown as [string, RequestInit]
    expect(JSON.parse(init.body as string)).toEqual({ jobIds: [3], withoutSuggestion: false })
  })
})

// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import PrimeVue from 'primevue/config'
import InboxView from './InboxView.vue'
import type { ImportJob, InboxResponse, RecordingCandidate } from '@/api'

const candidate: RecordingCandidate = {
  recordingId: 'r1', acoustId: 'a1', title: 'A Victory of Love', artists: ['Alphaville'], durationMs: 256_000,
  releaseId: 'rel1', releaseGroupId: 'rg1', releaseTitle: 'Forever Young', releaseArtist: 'Alphaville', releaseType: 'Album',
  releaseStatus: 'Official', year: 1984, disc: 1, trackNumber: 1, trackCount: 10,
  releaseDate: '1984-09-27', country: 'DE', mediumFormat: 'CD', score: 0.98,
}

const job = (id: number, path: string, state: ImportJob['state'], extra: Partial<ImportJob> = {}): ImportJob => ({
  id, path, group: path.includes('/') ? path.slice(0, path.lastIndexOf('/')) : '', state, format: 'Mp3', durationMs: 256_000,
  tagTitle: null, tagArtist: null, tagAlbum: null, matchScore: null, reviewReason: null, error: null,
  selected: null, candidateCount: 0, startedAt: '2026-10-25T18:00:00Z', ...extra,
})

const response: InboxResponse = {
  status: { running: false, currentPath: null, processed: 2, waiting: 1, imported: 0, importsPending: 0, lastRunAt: '2026-10-25T18:00:00Z', lastError: null },
  jobs: [
    job(1, 'Forever Young/01.mp3', 'Matched', { tagTitle: 'A Victory Of Love', tagArtist: 'Alphaville', matchScore: 0.98, selected: candidate, candidateCount: 5 }),
    job(2, 'Forever Young/02.mp3', 'NeedsReview', { matchScore: 0.72, reviewReason: 'Score 72 % unter 90 %', selected: candidate, candidateCount: 1 }),
    job(3, 'lose.flac', 'Fingerprinted', { error: 'AcoustID-API-Key fehlt' }),
  ],
}

function mountView() {
  setActivePinia(createPinia())
  return mount(InboxView, { global: { plugins: [PrimeVue] } })
}

describe('InboxView', () => {
  it('zeigt Jobs nach Ordner mit Status, Treffer und Begründung', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify(response))))

    const wrapper = mountView()
    await flushPromises()

    expect(wrapper.findAll('[data-testid="job-state"]').map((t) => t.text())).toEqual(['Erkannt', 'Prüfen', 'In Arbeit'])
    expect(wrapper.text()).toContain('Forever Young')
    expect(wrapper.text()).toContain('Ohne Ordner')
    expect(wrapper.find('[data-testid="job-match"]').text()).toContain('A Victory of Love')
    expect(wrapper.text()).toContain('1984-09-27 · DE · CD')
    expect(wrapper.find('[data-testid="job-reason"]').text()).toBe('Score 72 % unter 90 %')
    expect(wrapper.text()).toContain('AcoustID-API-Key fehlt')
    expect(wrapper.find('[data-testid="inbox-progress"]').text()).toBe('1 Datei(en) werden noch kopiert')
  })

  it('filtert nach Status', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify(response))))

    const wrapper = mountView()
    await flushPromises()
    const review = wrapper.findAll('button').find((b) => b.text().startsWith('Prüfen'))
    await review!.trigger('click')

    expect(review!.text()).toBe('Prüfen (1)')
    expect(wrapper.findAll('[data-testid="job-state"]').map((t) => t.text())).toEqual(['Prüfen'])
  })

  it('erklärt die leere Inbox', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ ...response, jobs: [] }))))

    const wrapper = mountView()
    await flushPromises()

    expect(wrapper.text()).toContain('Die Inbox ist leer')
  })

  it('übernimmt erkannte Dateien eines Ordners', async () => {
    const fetch = vi.fn(async (url: string, init?: RequestInit) =>
      url === '/api/inbox/import' && init?.method === 'POST'
        ? new Response(JSON.stringify({ ...response.status, running: true, importsPending: 1 }), { status: 202 })
        : new Response(JSON.stringify(response)),
    )
    vi.stubGlobal('fetch', fetch)

    const wrapper = mountView()
    await flushPromises()
    expect(wrapper.find('[data-testid="import-all"]').text()).toBe('Alle erkannten übernehmen (1)')
    await wrapper.find('[data-testid="import-group"]').trigger('click')
    await flushPromises()

    const call = fetch.mock.calls.find(([url]) => url === '/api/inbox/import')
    expect(JSON.parse(call![1]!.body as string)).toEqual({ jobIds: [1] })
  })

  it('verwirft einen Job', async () => {
    const fetch = vi.fn(async (url: string) =>
      url.endsWith('/reject') ? new Response(null, { status: 204 }) : new Response(JSON.stringify(response)),
    )
    vi.stubGlobal('fetch', fetch)

    const wrapper = mountView()
    await flushPromises()
    await wrapper.findAll('[data-testid="reject-job"]')[2]!.trigger('click')
    await flushPromises()

    expect(fetch).toHaveBeenCalledWith('/api/inbox/3/reject', expect.objectContaining({ method: 'POST' }))
  })

  it('kopiert den Fehlerbericht und prüft alle Problemfälle neu', async () => {
    const fetch = vi.fn(async (url: string, init?: RequestInit) =>
      url === '/api/inbox/report'
        ? new Response('Tonregal Inbox – 2 Datei(en) mit Problemen')
        : init?.method === 'POST'
          ? new Response(JSON.stringify({ count: 2 }))
          : new Response(JSON.stringify(response)),
    )
    vi.stubGlobal('fetch', fetch)

    const wrapper = mountView()
    await flushPromises()
    expect(wrapper.find('[data-testid="report"]').text()).toBe('Fehler kopieren (2)')

    await wrapper.find('[data-testid="report"]').trigger('click')
    await flushPromises()
    expect((document.querySelector('[data-testid="report-text"]') as HTMLTextAreaElement).value).toContain('2 Datei(en) mit Problemen')

    await wrapper.find('[data-testid="retry-all"]').trigger('click')
    await flushPromises()
    expect(fetch.mock.calls.map((c) => [c[0], c[1]?.method])).toContainEqual(['/api/inbox/retry-all', 'POST'])
    wrapper.unmount()
  })
})

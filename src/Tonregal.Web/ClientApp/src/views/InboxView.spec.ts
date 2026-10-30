// SPDX-License-Identifier: AGPL-3.0-or-later
import { afterEach, describe, expect, it, vi } from 'vitest'
import { enableAutoUnmount, flushPromises, mount } from '@vue/test-utils'
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

function mountView(props: { source?: 'Inbox' | 'Legacy' } = {}) {
  setActivePinia(createPinia())
  return mount(InboxView, { props, global: { plugins: [PrimeVue] }, attachTo: document.body })
}

// Die Ansicht hört auf Tasten am window; ohne Unmount reagierten alte Instanzen mit.
enableAutoUnmount(afterEach)

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

  it('übernimmt alle Dateien ohne Vorschlag eines Ordners', async () => {
    const unknown = (id: number) =>
      job(id, `Meret/0${id}.mp3`, 'NeedsReview', { reviewReason: 'AcoustID kennt diese Aufnahme nicht' })
    const jobs = [unknown(4), unknown(5), response.jobs[1]]
    const fetch = vi.fn(async (_url: string, _init?: RequestInit) => new Response(JSON.stringify({ ...response, jobs })))
    vi.stubGlobal('fetch', fetch)

    const wrapper = mountView()
    await flushPromises()
    const button = wrapper.find('[data-testid="import-group-unmatched"]')
    expect(button.text()).toBe('Ohne Treffer übernehmen (2)')
    await button.trigger('click')
    await flushPromises()

    expect(fetch.mock.calls.filter(([, init]) => init?.method === 'POST').map(([url]) => url)).toEqual([
      '/api/inbox/4/import-unmatched',
      '/api/inbox/5/import-unmatched',
    ])
  })

  it('übernimmt einen Vorschlag aus dem Review', async () => {
    const fetch = vi.fn(async (url: string, init?: RequestInit) =>
      url === '/api/inbox/2/select'
        ? new Response(JSON.stringify(response.jobs[1]))
        : url === '/api/inbox/import' && init?.method === 'POST'
          ? new Response(JSON.stringify({ ...response.status, running: true, importsPending: 1 }), { status: 202 })
          : new Response(JSON.stringify(response)),
    )
    vi.stubGlobal('fetch', fetch)

    const wrapper = mountView()
    await flushPromises()
    await wrapper.find('[data-testid="accept-job"]').trigger('click')
    await flushPromises()

    const urls = fetch.mock.calls.filter(([, init]) => init?.method === 'POST').map(([url, init]) => [url, JSON.parse(init!.body as string)])
    expect(urls).toEqual([
      ['/api/inbox/2/select', { recordingId: 'r1', releaseId: 'rel1' }],
      ['/api/inbox/import', { jobIds: [2] }],
    ])
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
      url === '/api/inbox/report?source=Inbox'
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
    expect(fetch.mock.calls.map((c) => [c[0], c[1]?.method])).toContainEqual(['/api/inbox/retry-all?source=Inbox', 'POST'])
  })

  it('arbeitet die Prüfliste per Tastatur ab', async () => {
    const fetch = vi.fn(async (url: string, _init?: RequestInit) =>
      url.endsWith('/select') || url.endsWith('/reject')
        ? new Response(JSON.stringify(response.jobs[1]))
        : url === '/api/inbox/import'
          ? new Response(JSON.stringify(response.status), { status: 202 })
          : new Response(JSON.stringify(response)),
    )
    vi.stubGlobal('fetch', fetch)

    mountView()
    await flushPromises()
    const press = async (key: string) => {
      window.dispatchEvent(new KeyboardEvent('keydown', { key }))
      await flushPromises()
    }
    await press('j')
    await press('j')
    await press('Enter')
    await press('k')
    await press('x')

    const posts = fetch.mock.calls.filter(([, init]) => init?.method === 'POST').map(([url]) => url)
    expect(posts).toEqual(['/api/inbox/2/select', '/api/inbox/import', '/api/inbox/1/reject'])
  })

  it('zeigt den Stand des Altbestands und startet den Massenlauf', async () => {
    const summary = { path: '/Volumes/T7/Musik/Altbestand', exists: true, total: 120, pending: 80, matched: 0, needsReview: 7, imported: 30, alreadyInLibrary: 3, rejected: 0 }
    const fetch = vi.fn(async (url: string) =>
      url === '/api/inbox/legacy'
        ? new Response(JSON.stringify(summary))
        : url === '/api/inbox/legacy/scan'
          ? new Response(JSON.stringify(response.status), { status: 202 })
          : new Response(JSON.stringify({ ...response, jobs: [] })),
    )
    vi.stubGlobal('fetch', fetch)

    const wrapper = mountView({ source: 'Legacy' })
    await flushPromises()
    expect(wrapper.find('h1').text()).toBe('Altbestand')
    expect(wrapper.find('[data-testid="legacy-summary"]').text()).toContain('120 Dateien eingelesen: 30 übernommen, 3 schon da, 7 zu prüfen, 80 offen')
    await wrapper.find('button[data-testid="start-legacy"]').trigger('click')
    await flushPromises()

    expect(fetch.mock.calls.map(([url]) => url)).toContain('/api/inbox?source=Legacy')
    expect(fetch).toHaveBeenCalledWith('/api/inbox/legacy/scan', expect.objectContaining({ method: 'POST' }))
  })
  it('lädt nach dem eigenen Laden nicht im Kreis neu', async () => {
    const fetch = vi.fn(async (_url: string) => new Response(JSON.stringify(response)))
    vi.stubGlobal('fetch', fetch)

    mountView()
    await flushPromises()
    await new Promise((resolve) => setTimeout(resolve, 500))
    await flushPromises()

    expect(fetch.mock.calls.filter(([url]) => url === '/api/inbox?source=Inbox')).toHaveLength(1)
  })

  it('zeigt lange Listen seitenweise und blättert mit der Tastatur weiter', async () => {
    localStorage.clear()
    // Der Paginator fragt nach der Bildschirmbreite, jsdom kennt matchMedia nicht.
    vi.stubGlobal('matchMedia', () => ({ matches: false, addEventListener() {}, removeEventListener() {} }))
    const many = Array.from({ length: 120 }, (_, i) => job(i + 1, `Altbestand/${String(i + 1).padStart(3, '0')}.mp3`, 'NeedsReview'))
    vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ ...response, jobs: many }))))

    const wrapper = mountView()
    await flushPromises()
    expect(wrapper.findAll('[data-testid="job-state"]')).toHaveLength(50)
    expect(wrapper.text()).toContain('1–50 von 120')

    for (let i = 0; i < 51; i++) window.dispatchEvent(new KeyboardEvent('keydown', { key: 'j' }))
    await flushPromises()

    expect(wrapper.text()).toContain('51–100 von 120')
    expect(wrapper.find('.focused-row').text()).toContain('051.mp3')
  })
})

// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, RouterLinkStub } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import PrimeVue from 'primevue/config'
import DuplicatesView from './DuplicatesView.vue'
import type { DuplicateFile, DuplicateGroup } from '@/api'

const file = (id: number, extra: Partial<DuplicateFile> = {}): DuplicateFile => ({
  id, path: `Evanescence/2003 - Fallen/0${id} - Haunted.mp3`, format: 'Mp3', bitrate: 192, sampleRate: 44100, sizeBytes: 5_000_000,
  durationMs: 186_000, title: 'Haunted', artists: ['Evanescence'], albumId: 3, album: 'Fallen', year: 2003,
  hasMusicBrainzId: true, source: 'Inbox', addedAt: '2026-10-25T20:00:00Z', ...extra,
})

const groups: DuplicateGroup[] = [
  { id: 1, kind: 'Fingerprint', suggestedFileId: 2, createdAt: '2026-10-25T20:00:00Z', files: [file(1), file(2, { format: 'Flac', bitrate: 900 })] },
]

function mountView() {
  setActivePinia(createPinia())
  return mount(DuplicatesView, { global: { plugins: [PrimeVue], stubs: { RouterLink: RouterLinkStub } } })
}

describe('DuplicatesView', () => {
  it('zeigt Gruppen mit Vorschlag und schickt die Auswahl', async () => {
    const fetch = vi.fn(async (_url: string, init?: RequestInit) =>
      init?.method === 'POST' ? new Response(null, { status: 204 }) : new Response(JSON.stringify(fetch.mock.calls.length > 1 ? [] : groups)),
    )
    vi.stubGlobal('fetch', fetch)
    vi.stubGlobal('Audio', class { pause() {} })

    const wrapper = mountView()
    await flushPromises()

    expect(wrapper.findAll('[data-testid="duplicate-group"]')).toHaveLength(1)
    expect(wrapper.text()).toContain('Gleiche Aufnahme')
    expect(wrapper.text()).toContain('FLAC · 44.1 kHz')
    expect(wrapper.text()).toContain('MP3 · 192 kbps')
    expect(wrapper.find('[data-testid="resolve"]').text()).toContain('1 in den Papierkorb')

    await wrapper.find('[data-testid="resolve"]').trigger('click')
    await flushPromises()

    const post = fetch.mock.calls.find((c) => c[1]?.method === 'POST')!
    expect(post[0]).toBe('/api/duplicates/1/resolve')
    expect(JSON.parse(post[1]!.body as string)).toEqual({ keepFileIds: [2] })
    expect(wrapper.text()).toContain('Keine offenen Dubletten')
  })

  it('alle behalten schickt alle Dateien', async () => {
    const fetch = vi.fn(async (_url: string, init?: RequestInit) =>
      init?.method === 'POST' ? new Response(null, { status: 204 }) : new Response(JSON.stringify(groups)),
    )
    vi.stubGlobal('fetch', fetch)
    vi.stubGlobal('Audio', class { pause() {} })

    const wrapper = mountView()
    await flushPromises()
    await wrapper.find('[data-testid="keep-all"]').trigger('click')
    await flushPromises()

    const post = fetch.mock.calls.find((c) => c[1]?.method === 'POST')!
    expect(JSON.parse(post[1]!.body as string)).toEqual({ keepFileIds: [1, 2] })
  })
})

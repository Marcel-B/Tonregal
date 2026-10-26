// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, RouterLinkStub } from '@vue/test-utils'
import PrimeVue from 'primevue/config'
import AlbumView from './AlbumView.vue'
import type { AlbumDetail } from '@/api'

const album: AlbumDetail = {
  id: 7,
  title: 'Testalbum',
  year: 2024,
  kind: 'Album',
  discCount: 1,
  albumArtist: { id: 1, name: 'Testband' },
  tracks: [
    { id: 1, disc: 1, number: 1, title: 'Erster Song', artists: ['Testband'], durationMs: 125_000, bpm: 120, initialKey: 'Am', genre: 'Rock',
      files: [{ id: 1, format: 'Mp3', bitrate: 320, sampleRate: 44100, sizeBytes: 1, path: 'a.mp3' }] },
    { id: 2, disc: 1, number: 2, title: 'Zweiter Song', artists: ['Testband', 'Gast'], durationMs: 61_000, bpm: null, initialKey: null, genre: null,
      files: [{ id: 2, format: 'Flac', bitrate: 900, sampleRate: 44100, sizeBytes: 1, path: 'b.flac' }] },
  ],
  canFetchCover: false,
  releaseCount: 1,
}

describe('AlbumView', () => {
  it('zeigt Tracks, Gäste, Dauer und Formate', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify(album))))

    const wrapper = mount(AlbumView, { props: { id: 7 }, global: { plugins: [PrimeVue], stubs: { RouterLink: RouterLinkStub } } })
    await flushPromises()

    expect(wrapper.findAll('[data-testid="track-title"]').map((t) => t.text())).toEqual(['Erster Song', 'Zweiter Song'])
    expect(wrapper.text()).toContain('Gast')
    expect(wrapper.text()).toContain('3:06')
    expect(wrapper.text()).toContain('FLAC')
    expect(wrapper.find('[data-testid="fetch-cover"]').exists()).toBe(false)
  })

  it('holt ein fehlendes Cover nach', async () => {
    const fetch = vi.fn(async (_url: string, init?: RequestInit) =>
      init?.method === 'POST' ? new Response(null, { status: 204 }) : new Response(JSON.stringify({ ...album, canFetchCover: fetch.mock.calls.length < 2 })),
    )
    vi.stubGlobal('fetch', fetch)

    const wrapper = mount(AlbumView, { props: { id: 7 }, global: { plugins: [PrimeVue], stubs: { RouterLink: RouterLinkStub } } })
    await flushPromises()
    await wrapper.find('[data-testid="fetch-cover"] button, button[data-testid="fetch-cover"]').trigger('click')
    await flushPromises()

    expect(fetch.mock.calls.map((c) => [c[0], c[1]?.method])).toContainEqual(['/api/library/albums/7/cover', 'POST'])
    expect(wrapper.find('[data-testid="fetch-cover"]').exists()).toBe(false)
  })

  it('bringt alle Tracks auf ein Release', async () => {
    const fetch = vi.fn(async (_url: string, init?: RequestInit) =>
      init?.method === 'POST'
        ? new Response(JSON.stringify({ releaseId: 'r', changed: 3, skipped: 1 }))
        : new Response(JSON.stringify({ ...album, releaseCount: fetch.mock.calls.length < 2 ? 2 : 1 })),
    )
    vi.stubGlobal('fetch', fetch)

    const wrapper = mount(AlbumView, { props: { id: 7 }, global: { plugins: [PrimeVue], stubs: { RouterLink: RouterLinkStub } } })
    await flushPromises()
    await wrapper.find('button[data-testid="align-release"]').trigger('click')
    await flushPromises()

    expect(fetch.mock.calls.map((c) => [c[0], c[1]?.method])).toContainEqual(['/api/library/albums/7/align-release', 'POST'])
    expect(wrapper.find('[data-testid="align-release"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="align-message"]').text()).toBe('3 Tracks angepasst, 1 stehen nicht auf diesem Release.')
  })
})

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
  })
})

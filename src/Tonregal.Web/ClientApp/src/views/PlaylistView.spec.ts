// SPDX-License-Identifier: AGPL-3.0-or-later
import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, RouterLinkStub } from '@vue/test-utils'
import PrimeVue from 'primevue/config'
import { createPinia, setActivePinia } from 'pinia'
import { usePlayerStore } from '@/stores/player'
import PlaylistView from './PlaylistView.vue'
import type { PlaylistDetail, PlaylistItem } from '@/api'

vi.mock('vue-router', () => ({ useRouter: () => ({ push: vi.fn() }) }))

const item = (trackId: number): PlaylistItem => ({
  fileId: trackId * 10,
  trackId,
  title: `Song ${trackId}`,
  artist: 'Band',
  album: 'Platte',
  albumId: 3,
  durationMs: 180_000,
})

const detail = (...ids: number[]): PlaylistDetail => ({
  playlist: { id: 7, name: 'Sonntag', comment: null, trackCount: ids.length, durationMs: ids.length * 180_000, updatedAt: '2026-11-08T00:00:00Z', albumIds: [3] },
  items: ids.map(item),
})

function stub(answer: (url: string, init?: RequestInit) => Response) {
  const fetch = vi.fn(async (url: string, init?: RequestInit) => answer(url, init))
  vi.stubGlobal('fetch', fetch)
  return fetch
}

const mountView = () => {
  const pinia = createPinia()
  setActivePinia(pinia)
  return mount(PlaylistView, { props: { id: 7 }, global: { plugins: [PrimeVue, pinia], stubs: { RouterLink: RouterLinkStub } } })
}

describe('PlaylistView', () => {
  afterEach(() => vi.unstubAllGlobals())

  it('zeigt die Stücke und spielt ab einem Stück', async () => {
    stub(() => new Response(JSON.stringify(detail(1, 2, 3))))
    const wrapper = mountView()
    await flushPromises()

    expect(wrapper.find('[data-testid="playlist-title"]').text()).toBe('Sonntag')
    expect(wrapper.findAll('[data-testid="playlist-item"]').map((r) => r.text())).toEqual([
      expect.stringContaining('Song 1'),
      expect.stringContaining('Song 2'),
      expect.stringContaining('Song 3'),
    ])
    await wrapper.findAll('[data-testid="play-from"]')[1]!.trigger('click')
    const player = usePlayerStore()
    expect(player.queue.map((i) => i.trackId)).toEqual([1, 2, 3])
    expect(player.current?.trackId).toBe(2)
  })

  it('verschiebt per Pfeiltaste und schickt die neue Reihenfolge', async () => {
    const fetch = stub((_, init) =>
      new Response(JSON.stringify(init?.method === 'PUT' ? detail(...(JSON.parse(String(init.body)).trackIds as number[])) : detail(1, 2, 3))),
    )
    const wrapper = mountView()
    await flushPromises()

    await wrapper.findAll('[data-testid="playlist-handle"]')[0]!.trigger('keydown', { key: 'ArrowDown' })
    await flushPromises()

    const put = fetch.mock.calls.find(([, init]) => init?.method === 'PUT')!
    expect(put[0]).toBe('/api/playlists/7/tracks')
    expect(JSON.parse(String(put[1]!.body))).toEqual({ trackIds: [2, 1, 3] })
    expect(wrapper.findAll('[data-testid="playlist-item"]')[0]!.text()).toContain('Song 2')
  })

  it('nimmt die Reihenfolge zurück, wenn der Server ablehnt', async () => {
    stub((_, init) =>
      init?.method === 'PUT'
        ? new Response(JSON.stringify({ detail: 'Kaputt' }), { status: 500 })
        : new Response(JSON.stringify(detail(1, 2))),
    )
    const wrapper = mountView()
    await flushPromises()

    await wrapper.findAll('[data-testid="playlist-handle"]')[1]!.trigger('keydown', { key: 'ArrowUp' })
    await flushPromises()

    expect(wrapper.findAll('[data-testid="playlist-item"]')[0]!.text()).toContain('Song 1')
    expect(wrapper.text()).toContain('Kaputt')
  })

  it('entfernt ein Stück', async () => {
    let removed = false
    const fetch = stub((_, init) => {
      if (init?.method === 'DELETE') {
        removed = true
        return new Response(null, { status: 204 })
      }
      return new Response(JSON.stringify(removed ? detail(2) : detail(1, 2)))
    })
    const wrapper = mountView()
    await flushPromises()

    await wrapper.findAll('[data-testid="remove"]')[0]!.trigger('click')
    await flushPromises()

    expect(fetch.mock.calls.some(([url, init]) => url === '/api/playlists/7/tracks/1' && init?.method === 'DELETE')).toBe(true)
    expect(wrapper.findAll('[data-testid="playlist-item"]')).toHaveLength(1)
  })
})

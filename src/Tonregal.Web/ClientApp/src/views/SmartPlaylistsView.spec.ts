// SPDX-License-Identifier: AGPL-3.0-or-later
import { beforeAll, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import PrimeVue from 'primevue/config'
import { createPinia, setActivePinia } from 'pinia'
import { usePlayerStore } from '@/stores/player'
import SmartPlaylistsView from './SmartPlaylistsView.vue'
import type { SmartPlaylist } from '@/api'

const mountView = () => {
  const pinia = createPinia()
  setActivePinia(pinia)
  return mount(SmartPlaylistsView, { global: { plugins: [PrimeVue, pinia] } })
}
const button = (wrapper: ReturnType<typeof mountView>, label: string) => wrapper.findAll('button').find((b) => b.text().includes(label))!

const neu: SmartPlaylist = {
  id: 1,
  name: 'Neu (30 Tage)',
  comment: null,
  bpmMin: null,
  bpmMax: null,
  keys: [],
  genre: null,
  addedWithinDays: 30,
  neverPlayed: false,
  minRating: null,
  sort: 'Newest',
  limit: null,
  updatedAt: '2026-10-27T00:00:00Z',
  path: 'Playlists/tonregal-1.nsp',
  matching: 12,
  nsp: '{}',
}

function stub(extra: (url: string, init?: RequestInit) => Response | undefined = () => undefined) {
  const fetch = vi.fn(async (url: string, init?: RequestInit) => {
    const answer = extra(url, init)
    if (answer) return answer
    if (url === '/api/smart-playlists/keys') return new Response(JSON.stringify([{ key: 'Am', tracks: 3 }]))
    return new Response(JSON.stringify([neu]))
  })
  vi.stubGlobal('fetch', fetch)
  return fetch
}

describe('SmartPlaylistsView', () => {
  // Select und MultiSelect fragen nach der Bildschirmbreite, jsdom kennt matchMedia nicht.
  beforeAll(() => {
    window.matchMedia ??= (query: string) =>
      ({ matches: false, media: query, addEventListener() {}, removeEventListener() {} }) as unknown as MediaQueryList
  })

  it('zeigt Regeln und Anzahl', async () => {
    stub()
    const wrapper = mountView()
    await flushPromises()
    const card = wrapper.find('[data-testid="playlist-1"]')
    expect(card.text()).toContain('neu seit 30 Tagen')
    expect(card.text()).toContain('Neueste zuerst')
    expect(card.find('[data-testid="matching"]').text()).toBe('12 Stücke')
  })

  it('legt eine Playlist an und schickt die Regeln', async () => {
    const created: SmartPlaylist = { ...neu, id: 3, name: 'Ruhig', addedWithinDays: null, bpmMax: 90, sort: 'Random', matching: 4 }
    const fetch = stub((_, init) => (init?.method === 'POST' ? new Response(JSON.stringify(created), { status: 201 }) : undefined))
    const wrapper = mountView()
    await flushPromises()

    await button(wrapper, 'Neue Smart Playlist').trigger('click')
    await wrapper.find('[data-testid="name"]').setValue('Ruhig')
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    const [, init] = fetch.mock.calls.find(([, i]) => i?.method === 'POST')!
    expect(JSON.parse(init!.body as string)).toMatchObject({ name: 'Ruhig', keys: [], sort: 'Random', neverPlayed: false })
    expect(wrapper.find('[data-testid="editor"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="playlist-3"]').text()).toContain('Ruhig')
  })

  it('zeigt die Fehlermeldung des Servers', async () => {
    stub((_, init) =>
      init?.method === 'POST'
        ? new Response(JSON.stringify({ errors: { rules: ['Mindestens eine Regel setzen'] } }), { status: 400 })
        : undefined,
    )
    const wrapper = mountView()
    await flushPromises()
    await button(wrapper, 'Neue Smart Playlist').trigger('click')
    await wrapper.find('[data-testid="name"]').setValue('Leer')
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(wrapper.text()).toContain('Mindestens eine Regel setzen')
  })

  it('löscht erst nach Rückfrage', async () => {
    const fetch = stub((_, init) => (init?.method === 'DELETE' ? new Response(null, { status: 204 }) : undefined))
    const wrapper = mountView()
    await flushPromises()
    await button(wrapper, 'Löschen').trigger('click')
    expect(fetch.mock.calls.some(([, i]) => i?.method === 'DELETE')).toBe(false)
    await button(wrapper, 'Ja, löschen').trigger('click')
    await flushPromises()
    expect(fetch).toHaveBeenCalledWith('/api/smart-playlists/1', expect.objectContaining({ method: 'DELETE' }))
    expect(wrapper.find('[data-testid="playlist-1"]').exists()).toBe(false)
  })

  it('spielt die aktuelle Auswertung im Player', async () => {
    const item = { fileId: 7, trackId: 3, title: 'Nachtfahrt', artist: 'Tonwerk', album: 'Nacht', albumId: 2, durationMs: 1000 }
    stub((url) =>
      url === '/api/smart-playlists/1/tracks' ? new Response(JSON.stringify({ fromNavidrome: false, items: [item] })) : undefined,
    )
    const wrapper = mountView()
    await flushPromises()

    await wrapper.find('[data-testid="play"]').trigger('click')
    await flushPromises()

    const player = usePlayerStore()
    expect(player.queue).toEqual([item])
    expect(player.playing).toBe(true)
    expect(wrapper.find('[data-testid="notice"]').text()).toContain('Tonregal hat selbst ausgewählt')
  })
})

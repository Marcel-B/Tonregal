// SPDX-License-Identifier: AGPL-3.0-or-later
import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import PrimeVue from 'primevue/config'
import { createPinia, setActivePinia } from 'pinia'
import AddToPlaylist from './AddToPlaylist.vue'
import { usePlaylistsStore } from '@/stores/playlists'
import type { PlaylistSummary } from '@/api'

const summary = (trackCount: number): PlaylistSummary => ({
  id: 4,
  name: 'Zug',
  comment: null,
  trackCount,
  durationMs: 0,
  updatedAt: '2026-11-08T00:00:00Z',
  albumIds: [],
})

describe('AddToPlaylist', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
    document.body.innerHTML = ''
  })

  it('lädt die Playlists beim Öffnen und fügt die Stücke ohne Doppelte hinzu', async () => {
    const fetch = vi.fn(async (_: string, init?: RequestInit) =>
      init?.method === 'POST'
        ? new Response(JSON.stringify({ added: 1, skipped: 1, playlist: summary(5) }))
        : new Response(JSON.stringify([summary(3)])),
    )
    vi.stubGlobal('fetch', fetch)
    setActivePinia(createPinia())
    const wrapper = mount(AddToPlaylist, { props: { trackIds: [8, 9, 8] }, global: { plugins: [PrimeVue] }, attachTo: document.body })

    await wrapper.find('[data-testid="add-to-playlist"]').trigger('click')
    await flushPromises()
    const entry = [...document.body.querySelectorAll<HTMLElement>('[role="menuitem"]')].find((e) => e.textContent?.includes('Zug'))!
    entry.querySelector<HTMLElement>('a, div')!.click()
    await flushPromises()

    const post = fetch.mock.calls.find(([, init]) => init?.method === 'POST')!
    expect(post[0]).toBe('/api/playlists/4/tracks')
    expect(JSON.parse(String(post[1]!.body))).toEqual({ trackIds: [8, 9] })
    expect(usePlaylistsStore().list[0]!.trackCount).toBe(5)
    wrapper.unmount()
  })
})

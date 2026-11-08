// SPDX-License-Identifier: AGPL-3.0-or-later
import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, RouterLinkStub } from '@vue/test-utils'
import PrimeVue from 'primevue/config'
import { createPinia, setActivePinia } from 'pinia'
import PlaylistsView from './PlaylistsView.vue'
import type { PlaylistSummary } from '@/api'

const push = vi.fn()
vi.mock('vue-router', () => ({ useRouter: () => ({ push }) }))

const summary = (id: number, name: string, trackCount: number): PlaylistSummary => ({
  id,
  name,
  comment: null,
  trackCount,
  durationMs: trackCount * 240_000,
  updatedAt: '2026-11-08T00:00:00Z',
  albumIds: trackCount ? [1] : [],
})

const mountView = () => {
  const pinia = createPinia()
  setActivePinia(pinia)
  return mount(PlaylistsView, { global: { plugins: [PrimeVue, pinia], stubs: { RouterLink: RouterLinkStub } } })
}

describe('PlaylistsView', () => {
  afterEach(() => vi.unstubAllGlobals())

  it('listet die Playlists mit Anzahl und Länge', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify([summary(2, 'Zug', 20), summary(1, 'abends', 1)]))))
    const wrapper = mountView()
    await flushPromises()

    expect(wrapper.findAll('[data-testid^="playlist-"]').map((c) => c.attributes('data-testid'))).toEqual(['playlist-1', 'playlist-2'])
    expect(wrapper.find('[data-testid="playlist-2"] [data-testid="summary"]').text()).toBe('20 Stücke · 1 Std. 20 Min.')
    expect(wrapper.find('[data-testid="playlist-1"] [data-testid="summary"]').text()).toBe('1 Stück · 4:00')
  })

  it('legt eine Playlist an und öffnet sie', async () => {
    const fetch = vi.fn(async (_: string, init?: RequestInit) =>
      init?.method === 'POST' ? new Response(JSON.stringify(summary(5, 'Neu', 0)), { status: 201 }) : new Response('[]'),
    )
    vi.stubGlobal('fetch', fetch)
    const wrapper = mountView()
    await flushPromises()
    expect(wrapper.find('[data-testid="empty"]').exists()).toBe(true)

    await wrapper.find('[data-testid="new-playlist"]').trigger('click')
    await wrapper.find('[data-testid="playlist-name"]').setValue('Neu')
    await wrapper.find('[data-testid="create-form"]').trigger('submit')
    await flushPromises()

    const post = fetch.mock.calls.find(([, init]) => init?.method === 'POST')!
    expect(JSON.parse(String(post[1]!.body))).toEqual({ name: 'Neu', comment: null })
    expect(push).toHaveBeenCalledWith({ name: 'playlist', params: { id: 5 } })
  })
})

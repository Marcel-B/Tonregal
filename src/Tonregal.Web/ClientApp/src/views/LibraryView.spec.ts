// SPDX-License-Identifier: AGPL-3.0-or-later
import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, RouterLinkStub } from '@vue/test-utils'
import { createPinia } from 'pinia'
import PrimeVue from 'primevue/config'
import ToastService from 'primevue/toastservice'
import LibraryView from './LibraryView.vue'

describe('LibraryView', () => {
  afterEach(() => {
    vi.useRealTimers()
    sessionStorage.clear()
  })

  it('gruppiert Künstler nach Sortiername und filtert', async () => {
    const responses: Record<string, unknown> = {
      '/api/library/artists': [
        { id: 1, name: 'The Beatles', sortName: 'Beatles, The', albumCount: 2 },
        { id: 2, name: 'Daft Punk', sortName: 'Daft Punk', albumCount: 1 },
      ],
      '/api/library/scan': { running: false, progress: null, lastRunAt: null, lastResult: null, lastError: null },
    }
    vi.stubGlobal('fetch', vi.fn(async (url: string) => new Response(JSON.stringify(responses[url]))))

    const wrapper = mount(LibraryView, {
      global: { plugins: [PrimeVue, ToastService, createPinia()], stubs: { RouterLink: RouterLinkStub } },
    })
    await flushPromises()

    expect(wrapper.findAll('h2').map((h) => h.text())).toEqual(['B', 'D'])
    expect(wrapper.text()).toContain('2 Alben')

    await wrapper.find('input').setValue('daft')
    expect(wrapper.findAll('[data-testid="artist"]').map((a) => a.text())).toEqual(['Daft Punk1 Album'])
  })

  it('sucht Titel und Alben auf dem Server und spielt einen Treffer', async () => {
    vi.useFakeTimers()
    const track = (trackId: number, title: string) => ({ fileId: trackId * 10, trackId, title, artist: 'Daft Punk', album: 'Discovery', albumId: 7, durationMs: 225000 })
    const fetch = vi.fn(async (url: string) => {
      if (url.startsWith('/api/library/search'))
        return new Response(
          JSON.stringify({
            query: 'one',
            artists: [],
            albums: [{ id: 7, title: 'Discovery', year: 2001, kind: 'Album', trackCount: 14, albumArtist: { id: 2, name: 'Daft Punk' } }],
            tracks: [track(1, 'One More Time'), track(2, 'Something About Us')],
            trackCount: 2,
          }),
        )
      if (url === '/api/library/artists') return new Response(JSON.stringify([{ id: 2, name: 'Daft Punk', sortName: 'Daft Punk', albumCount: 1 }]))
      return new Response(JSON.stringify({ running: false, progress: null, lastRunAt: null, lastResult: null, lastError: null }))
    })
    vi.stubGlobal('fetch', fetch)
    const pinia = createPinia()
    const wrapper = mount(LibraryView, { global: { plugins: [PrimeVue, ToastService, pinia], stubs: { RouterLink: RouterLinkStub } } })
    await flushPromises()

    await wrapper.find('[data-testid="search"]').setValue('one')
    expect(fetch).not.toHaveBeenCalledWith('/api/library/search?q=one', expect.anything())
    await vi.advanceTimersByTimeAsync(300)
    await flushPromises()

    expect(fetch).toHaveBeenCalledWith('/api/library/search?q=one', expect.anything())
    expect(wrapper.findAll('[data-testid="track"]').map((t) => t.text())).toEqual([
      expect.stringContaining('One More Time'),
      expect.stringContaining('Something About Us'),
    ])
    expect(wrapper.find('[data-testid="album-results"]').text()).toContain('Discovery')
    expect(sessionStorage.getItem('tonregal.librarySearch')).toBe('one')

    await wrapper.findAll('[data-testid="track"] button')[0].trigger('click')
    const { usePlayerStore } = await import('@/stores/player')
    const player = usePlayerStore(pinia)
    expect(player.queue.map((q) => q.title)).toEqual(['One More Time', 'Something About Us'])
    expect(player.current?.title).toBe('One More Time')
  })

  it('meldet, wenn nichts passt', async () => {
    vi.useFakeTimers()
    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: string) =>
        new Response(
          JSON.stringify(
            url.startsWith('/api/library/search')
              ? { query: 'xyz', artists: [], albums: [], tracks: [], trackCount: 0 }
              : url === '/api/library/artists'
                ? []
                : { running: false, progress: null, lastRunAt: null, lastResult: null, lastError: null },
          ),
        ),
      ),
    )
    const wrapper = mount(LibraryView, { global: { plugins: [PrimeVue, ToastService, createPinia()], stubs: { RouterLink: RouterLinkStub } } })
    await flushPromises()
    await wrapper.find('[data-testid="search"]').setValue('xyz')
    await vi.advanceTimersByTimeAsync(300)
    await flushPromises()
    expect(wrapper.find('[data-testid="no-results"]').text()).toContain('xyz')
  })
})

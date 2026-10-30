// SPDX-License-Identifier: AGPL-3.0-or-later
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { mount, RouterLinkStub } from '@vue/test-utils'
import PrimeVue from 'primevue/config'
import { createPinia, setActivePinia } from 'pinia'
import PlayerBar from './PlayerBar.vue'
import { usePlayerStore, type QueueItem } from '@/stores/player'

const item = (fileId: number): QueueItem => ({ fileId, trackId: fileId, title: `Song ${fileId}`, artist: 'Band', album: 'Platte', albumId: 3, durationMs: 200_000 })

describe('PlayerBar', () => {
  let play: ReturnType<typeof vi.fn>
  let pause: ReturnType<typeof vi.fn>
  let fetch: ReturnType<typeof vi.fn>

  beforeEach(() => {
    localStorage.clear()
    setActivePinia(createPinia())
    // jsdom spielt nichts ab; play/pause nur mitschreiben und die Events selbst auslösen.
    play = vi.fn(function (this: HTMLMediaElement) {
      Object.defineProperty(this, 'paused', { value: false, configurable: true })
      return Promise.resolve()
    })
    pause = vi.fn(function (this: HTMLMediaElement) {
      Object.defineProperty(this, 'paused', { value: true, configurable: true })
    })
    vi.spyOn(HTMLMediaElement.prototype, 'play').mockImplementation(play as never)
    vi.spyOn(HTMLMediaElement.prototype, 'pause').mockImplementation(pause as never)
    vi.spyOn(HTMLMediaElement.prototype, 'load').mockImplementation(() => {})
    fetch = vi.fn(async () => new Response(null, { status: 204 }))
    vi.stubGlobal('fetch', fetch)
  })

  afterEach(() => vi.restoreAllMocks())

  const mountBar = () => mount(PlayerBar, { global: { plugins: [PrimeVue], stubs: { RouterLink: RouterLinkStub, Drawer: true } } })

  it('spielt die Queue über den Stream-Endpunkt und schaltet am Ende weiter', async () => {
    const wrapper = mountBar()
    const player = usePlayerStore()
    expect(wrapper.find('[data-testid="player"]').exists()).toBe(false)

    player.play([item(1), item(2)])
    await wrapper.vm.$nextTick()
    const audio = wrapper.get('audio').element as HTMLAudioElement
    expect(audio.src).toMatch(/\/api\/player\/files\/1\/stream$/)
    expect(play).toHaveBeenCalledTimes(1)
    expect(wrapper.get('[data-testid="player-title"]').text()).toBe('Song 1')

    await wrapper.get('audio').trigger('ended')
    expect(audio.src).toMatch(/\/api\/player\/files\/2\/stream$/)
    expect(play).toHaveBeenCalledTimes(2)
  })

  it('meldet „läuft“ sofort und „gehört“ nach der Hälfte an Navidrome', async () => {
    const wrapper = mountBar()
    usePlayerStore().play([item(7)])
    await wrapper.vm.$nextTick()
    const audio = wrapper.get('audio')

    Object.defineProperty(audio.element, 'currentTime', { value: 5, configurable: true, writable: true })
    await audio.trigger('timeupdate')
    Object.defineProperty(audio.element, 'currentTime', { value: 101, configurable: true, writable: true })
    await audio.trigger('timeupdate')
    await audio.trigger('timeupdate')

    expect(fetch.mock.calls.map((c) => c[0]).filter((url) => url.includes('/scrobble'))).toEqual([
      '/api/player/files/7/scrobble?submission=false',
      '/api/player/files/7/scrobble?submission=true',
    ])
  })

  it('zeigt die Bewertung aus Navidrome und bewertet', async () => {
    fetch.mockImplementation(async (url: string) =>
      url.startsWith('/api/player/ratings') ? Response.json({ '7': 3 }) : new Response(null, { status: 204 }),
    )
    const wrapper = mountBar()
    usePlayerStore().play([item(7)])
    await vi.waitFor(() => expect(wrapper.findAll('[data-testid="player-rating"] .p-rating-option-active')).toHaveLength(3))

    await wrapper.findAll('[data-testid="player-rating"] .p-rating-option')[4]!.trigger('click')

    const put = fetch.mock.calls.find((c) => c[1]?.method === 'PUT')!
    expect(put[0]).toBe('/api/player/files/7/rating')
    expect(JSON.parse(put[1].body)).toEqual({ rating: 5 })
  })

  it('wiederholt ein Stück, wenn es zu Ende ist', async () => {
    const wrapper = mountBar()
    const player = usePlayerStore()
    player.play([item(1), item(2)])
    await wrapper.vm.$nextTick()
    const repeat = wrapper.get('[data-testid="player-repeat"]')
    await repeat.trigger('click')
    await repeat.trigger('click')
    expect(repeat.attributes('aria-label')).toBe('Wiederholen: dieses Stück')

    await wrapper.get('audio').trigger('ended')
    expect((wrapper.get('audio').element as HTMLAudioElement).src).toMatch(/\/api\/player\/files\/1\/stream$/)
    expect(play).toHaveBeenCalledTimes(2)
  })

  it('pausiert per Knopf, ignoriert aber das pause beim Quellwechsel', async () => {
    const wrapper = mountBar()
    const player = usePlayerStore()
    player.play([item(1), item(2)])
    await wrapper.vm.$nextTick()

    await wrapper.get('audio').trigger('pause')
    expect(player.playing).toBe(true)

    await wrapper.get('audio').trigger('loadeddata')
    await wrapper.get('[data-testid="player-toggle"]').trigger('click')
    expect(pause).toHaveBeenCalled()
    expect(player.playing).toBe(false)
  })

  it('öffnet den großen Player über Cover oder Titel', async () => {
    const wrapper = mountBar()
    const player = usePlayerStore()
    player.play([item(1), item(2)])
    await wrapper.vm.$nextTick()
    expect(wrapper.find('[data-testid="now-playing"]').exists()).toBe(false)

    await wrapper.get('[data-testid="player-expand"]').trigger('click')
    expect(player.expanded).toBe(true)
    expect(wrapper.get('[data-testid="now-playing-title"]').text()).toBe('Song 1')

    await wrapper.get('[data-testid="now-playing-toggle"]').trigger('click')
    expect(player.playing).toBe(false)
    await wrapper.get('[data-testid="now-playing-repeat"]').trigger('click')
    expect(player.repeat).toBe('all')

    await wrapper.get('[data-testid="now-playing-close"]').trigger('click')
    await vi.waitFor(() => expect(player.expanded).toBe(false))
  })

  it('macht nach dem Neuladen an der gemerkten Stelle weiter', async () => {
    usePlayerStore().play([item(1)])
    usePlayerStore().rememberPosition(80)
    await Promise.resolve()
    setActivePinia(createPinia())

    const wrapper = mountBar()
    const audio = wrapper.get('audio')
    Object.defineProperty(audio.element, 'currentTime', { value: 0, configurable: true, writable: true })
    expect(usePlayerStore().playing).toBe(false)
    await audio.trigger('loadedmetadata')
    expect((audio.element as HTMLAudioElement).currentTime).toBe(80)
  })

  it('verschiebt Einträge der Warteschlange per Pfeiltaste', async () => {
    const wrapper = mount(PlayerBar, { global: { plugins: [PrimeVue], stubs: { RouterLink: RouterLinkStub, Drawer: { template: '<div><slot /></div>' } } } })
    const player = usePlayerStore()
    player.play([item(1), item(2), item(3)])
    await wrapper.vm.$nextTick()

    await wrapper.findAll('[data-testid="queue-handle"]')[0]!.trigger('keydown', { key: 'ArrowDown' })
    expect(player.queue.map((i) => i.fileId)).toEqual([2, 1, 3])
    expect(player.current?.fileId).toBe(1)
  })
})


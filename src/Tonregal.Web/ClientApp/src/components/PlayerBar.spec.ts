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

    expect(fetch.mock.calls.map((c) => c[0])).toEqual(['/api/player/files/7/scrobble?submission=false', '/api/player/files/7/scrobble?submission=true'])
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
})

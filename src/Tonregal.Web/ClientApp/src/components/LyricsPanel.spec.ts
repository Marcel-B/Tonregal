// SPDX-License-Identifier: AGPL-3.0-or-later
import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import PrimeVue from 'primevue/config'
import LyricsPanel from './LyricsPanel.vue'
import type { SongLyrics } from '@/api'

const synced: SongLyrics = {
  source: 'Lrclib',
  synced: true,
  instrumental: false,
  lines: [
    { timeMs: 1000, text: 'Erste Zeile' },
    { timeMs: 5000, text: 'Zweite Zeile' },
    { timeMs: 9000, text: '' },
  ],
}

function stubFetch(...answers: SongLyrics[]) {
  const fetch = vi.fn(async () => new Response(JSON.stringify(answers.shift()), { status: 200, headers: { 'Content-Type': 'application/json' } }))
  vi.stubGlobal('fetch', fetch)
  return fetch
}

const mountPanel = (position = 0, trackId = 4) => mount(LyricsPanel, { props: { trackId, position }, global: { plugins: [PrimeVue] } })

describe('LyricsPanel', () => {
  afterEach(() => vi.unstubAllGlobals())

  it('hebt die laufende Zeile hervor und springt beim Tippen', async () => {
    const fetch = stubFetch(synced)
    const wrapper = mountPanel(6)
    await flushPromises()

    expect(fetch).toHaveBeenCalledWith('/api/player/tracks/4/lyrics', expect.anything())
    const lines = wrapper.findAll('[data-testid="lyrics-line"]')
    expect(lines.map((l) => l.text())).toEqual(['Erste Zeile', 'Zweite Zeile', '♪'])
    expect(wrapper.get('[data-active]').text()).toBe('Zweite Zeile')
    expect(wrapper.text()).toContain('Quelle: lrclib.net, synchronisiert')

    await wrapper.setProps({ position: 9.5 })
    expect(wrapper.get('[data-active]').text()).toBe('♪')

    await lines[0]!.trigger('click')
    expect(wrapper.emitted('seek')).toEqual([[1]])
  })

  it('zeigt reinen Text, „nichts gefunden“ und sucht neu', async () => {
    const fetch = stubFetch(
      { source: 'None', synced: false, instrumental: false, lines: [] },
      { source: 'Tag', synced: false, instrumental: false, lines: [{ timeMs: null, text: 'Nur Text' }] },
    )
    const wrapper = mountPanel()
    await flushPromises()
    expect(wrapper.find('[data-testid="lyrics-none"]').exists()).toBe(true)

    await wrapper.get('[data-testid="lyrics-refresh"]').trigger('click')
    await flushPromises()
    expect(fetch).toHaveBeenLastCalledWith('/api/player/tracks/4/lyrics/refresh', expect.objectContaining({ method: 'POST' }))
    expect(wrapper.get('[data-testid="lyrics-line"]').text()).toBe('Nur Text')
    expect(wrapper.find('[data-active]').exists()).toBe(false)
  })

  it('lädt beim nächsten Stück neu', async () => {
    const fetch = stubFetch(synced, { ...synced, lines: [{ timeMs: 0, text: 'Anderes Lied' }] })
    const wrapper = mountPanel()
    await flushPromises()
    await wrapper.setProps({ trackId: 5 })
    await flushPromises()

    expect(fetch).toHaveBeenLastCalledWith('/api/player/tracks/5/lyrics', expect.anything())
    expect(wrapper.get('[data-testid="lyrics-line"]').text()).toBe('Anderes Lied')
  })
})

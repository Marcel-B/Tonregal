// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import PrimeVue from 'primevue/config'
import AnalysisView from './AnalysisView.vue'
import type { AnalysisResponse, AnalysisStatus } from '@/api'

const status: AnalysisStatus = {
  running: false, requested: false, currentAlbum: null, counts: { files: 200, analyzed: 50, pending: 150, withoutResult: 2 },
  waiting: 'Wartet aufs Nachtfenster 01:00–06:00', lastRunAt: null, lastError: null,
  recentErrors: ['Garbage/Unbekanntes Album/Titel 3.mp3: kaputte Frames'],
}
const response: AnalysisResponse = { status, settings: { runAtNight: true, nightStart: '01:00', nightEnd: '06:00', busyCheck: true } }

function mountView() {
  setActivePinia(createPinia())
  return mount(AnalysisView, { global: { plugins: [PrimeVue] } })
}

describe('AnalysisView', () => {
  it('zeigt Fortschritt, Zeitplan und Problemfälle', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify(response))))

    const wrapper = mountView()
    await flushPromises()

    expect(wrapper.find('[data-testid="analysis-counts"]').text()).toBe('50 von 200 Dateien analysiert, 2 ohne Ergebnis')
    expect(wrapper.text()).toContain('25 %')
    expect(wrapper.text()).toContain('jede Nacht von 01:00 bis 06:00, solange kein KI-Job läuft')
    expect(wrapper.find('[data-testid="analysis-state"]').text()).toBe('Wartet aufs Nachtfenster 01:00–06:00')
    expect(wrapper.find('[data-testid="analysis-recent-errors"]').text()).toContain('kaputte Frames')
  })

  it('startet und hält die Analyse an', async () => {
    const fetch = vi.fn(async (url: string, _init?: RequestInit) => {
      if (url === '/api/analysis/start') return new Response(JSON.stringify({ ...status, requested: true }), { status: 202 })
      if (url === '/api/analysis/stop') return new Response(JSON.stringify(status))
      return new Response(JSON.stringify(response))
    })
    vi.stubGlobal('fetch', fetch)

    const wrapper = mountView()
    await flushPromises()
    await wrapper.find('[data-testid="start-analysis"]').trigger('click')
    await flushPromises()
    await wrapper.find('[data-testid="stop-analysis"]').trigger('click')
    await flushPromises()

    expect(fetch.mock.calls.map(([url]) => url)).toEqual(['/api/analysis', '/api/analysis/start', '/api/analysis/stop'])
    expect(wrapper.find('[data-testid="start-analysis"]').exists()).toBe(true)
  })
})

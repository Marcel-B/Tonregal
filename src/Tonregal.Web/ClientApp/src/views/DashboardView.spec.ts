// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import PrimeVue from 'primevue/config'
import DashboardView from './DashboardView.vue'

describe('DashboardView', () => {
  it('zeigt die Bibliothekszahlen aus der API', async () => {
    const responses: Record<string, unknown> = {
      '/api/health': { status: 'ok', libraryMounted: true, version: '1.0.0' },
      '/api/stats': { artists: 3, albums: 5, tracks: 42, files: 44, openReviews: 1, openDuplicates: 0 },
    }
    vi.stubGlobal('fetch', vi.fn(async (url: string) => new Response(JSON.stringify(responses[url]))))

    const wrapper = mount(DashboardView, { global: { plugins: [PrimeVue] } })
    await flushPromises()

    expect(wrapper.get('[data-testid="tracks"]').text()).toBe('42')
    expect(wrapper.get('[data-testid="albums"]').text()).toBe('5')
    expect(wrapper.text()).not.toContain('Mount-Marker')
  })
})

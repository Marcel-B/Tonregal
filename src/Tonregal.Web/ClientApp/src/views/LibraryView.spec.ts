// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, RouterLinkStub } from '@vue/test-utils'
import { createPinia } from 'pinia'
import PrimeVue from 'primevue/config'
import ToastService from 'primevue/toastservice'
import LibraryView from './LibraryView.vue'

describe('LibraryView', () => {
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
})

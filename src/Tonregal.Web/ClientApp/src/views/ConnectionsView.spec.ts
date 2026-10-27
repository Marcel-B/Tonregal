// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import PrimeVue from 'primevue/config'
import ConnectionsView from './ConnectionsView.vue'
import type { IngestKeyInfo } from '@/api'

const mountView = () => mount(ConnectionsView, { global: { plugins: [PrimeVue] } })
const button = (wrapper: ReturnType<typeof mountView>, label: string) =>
  wrapper.findAll('button').find((b) => b.text().includes(label) || b.attributes('aria-label') === label)!

describe('ConnectionsView', () => {
  it('erzeugt einen Key und zeigt ihn mit den Zeilen für tonregal.env', async () => {
    const fresh: IngestKeyInfo = { source: 'tonwerk-ai', key: 'abc123', fromConfig: false, createdAt: '2026-10-27T20:00:00Z' }
    const fetch = vi.fn(async (url: string, _init?: RequestInit) =>
      url === '/api/settings/ingest/tonwerk-ai/key'
        ? new Response(JSON.stringify(fresh))
        : new Response(JSON.stringify([{ source: 'tonwerk-ai', key: null, fromConfig: false, createdAt: null }])),
    )
    vi.stubGlobal('fetch', fetch)

    const wrapper = mountView()
    await flushPromises()
    expect(wrapper.text()).toContain('Noch kein Key')

    await button(wrapper, 'Key erzeugen').trigger('click')
    await flushPromises()

    expect(fetch).toHaveBeenCalledWith('/api/settings/ingest/tonwerk-ai/key', expect.objectContaining({ method: 'POST' }))
    expect(wrapper.find('[data-testid="ingest-key"]').text()).toBe('abc123')
    expect(wrapper.find('[data-testid="ingest-env"]').text()).toBe(`TONREGAL_URL=${window.location.origin}\nTONREGAL_API_KEY=abc123`)
  })

  it('verdeckt einen vorhandenen Key und fragt vor dem Ersetzen nach', async () => {
    const fetch = vi.fn(async (url: string, _init?: RequestInit) =>
      url.endsWith('/key')
        ? new Response(JSON.stringify({ source: 'tonwerk-ai', key: 'neu', fromConfig: false, createdAt: null }))
        : new Response(JSON.stringify([{ source: 'tonwerk-ai', key: 'alt', fromConfig: false, createdAt: null }])),
    )
    vi.stubGlobal('fetch', fetch)

    const wrapper = mountView()
    await flushPromises()
    expect(wrapper.find('[data-testid="ingest-key"]').text()).toBe('•••')

    await button(wrapper, 'Anzeigen').trigger('click')
    expect(wrapper.find('[data-testid="ingest-key"]').text()).toBe('alt')

    await button(wrapper, 'Neu erzeugen').trigger('click')
    expect(fetch).toHaveBeenCalledTimes(1)
    await button(wrapper, 'Ja, neu erzeugen').trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-testid="ingest-key"]').text()).toBe('neu')
  })

  it('bietet einen Key aus appsettings nicht zum Ersetzen an', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify([{ source: 'tonwerk-ai', key: 'fest', fromConfig: true, createdAt: null }]))))

    const wrapper = mountView()
    await flushPromises()
    expect(wrapper.text()).toContain('aus appsettings')
    expect(wrapper.findAll('button').some((b) => b.text().includes('Neu erzeugen'))).toBe(false)
  })
})

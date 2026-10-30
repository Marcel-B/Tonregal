// SPDX-License-Identifier: AGPL-3.0-or-later
import { afterEach, describe, expect, it, vi } from 'vitest'
import { enableAutoUnmount, flushPromises, mount } from '@vue/test-utils'
import PrimeVue from 'primevue/config'
import LegacyCleanup from './LegacyCleanup.vue'
import type { LegacyCleanupPreview, LegacyCleanupResult } from '@/api'

const preview: LegacyCleanupPreview = {
  trashFolder: '.trash/2026-10-30/Altbestand',
  files: [
    { jobId: 1, path: 'Alphaville/Forever Young/01.mp3', sizeBytes: 4_000_000, alreadyInLibrary: false, libraryPath: 'Alphaville/1984 - Forever Young/01 - A Victory of Love.mp3' },
    { jobId: 2, path: 'Alphaville/Forever Young/02.mp3', sizeBytes: 5_000_000, alreadyInLibrary: true, libraryPath: 'Alphaville/1984 - Forever Young/02 - Summer in Berlin.mp3' },
  ],
  skipped: [{ jobId: 3, path: 'Alphaville/Forever Young/03.mp3', reason: 'Keine passende Datei in der Library gefunden' }],
  count: 2,
  sizeBytes: 9_000_000,
}

function mountButton() {
  return mount(LegacyCleanup, { global: { plugins: [PrimeVue], stubs: { teleport: true } }, attachTo: document.body })
}

enableAutoUnmount(afterEach)

describe('LegacyCleanup', () => {
  it('zeigt die Vorschau und verschiebt nach Bestätigung genau deren Dateien', async () => {
    const result: LegacyCleanupResult = { moved: 2, sizeBytes: 9_000_000, trashFolder: preview.trashFolder, skipped: [] }
    const fetch = vi.fn(async (_url: string, init?: RequestInit) =>
      new Response(JSON.stringify(init?.method === 'POST' ? result : preview)),
    )
    vi.stubGlobal('fetch', fetch)

    const wrapper = mountButton()
    await wrapper.find('button[data-testid="legacy-cleanup"]').trigger('click')
    await flushPromises()

    expect(fetch).toHaveBeenCalledWith('/api/inbox/legacy/cleanup', expect.objectContaining({ method: 'GET' }))
    const summary = wrapper.find('[data-testid="cleanup-summary"]').text()
    expect(summary).toContain('2 Datei(en)')
    expect(summary).toContain('9 MB')
    expect(summary).toContain('davon 1 schon vorher byte-gleich')
    expect(summary).toContain('Library/.trash/2026-10-30/Altbestand')
    expect(wrapper.find('[data-testid="cleanup-files"]').text()).toContain('01 - A Victory of Love.mp3')
    expect(wrapper.find('[data-testid="cleanup-skipped"]').text()).toContain('1 übernommene Datei(en) bleiben')
    // Nichts passiert ohne Bestätigung.
    expect(fetch).toHaveBeenCalledTimes(1)

    await wrapper.find('button[data-testid="cleanup-confirm"]').trigger('click')
    await flushPromises()

    const post = fetch.mock.calls.find(([, init]) => init?.method === 'POST')!
    expect(post[0]).toBe('/api/inbox/legacy/cleanup')
    expect(JSON.parse(post[1]!.body as string)).toEqual({ jobIds: [1, 2] })
    expect(wrapper.find('[data-testid="cleanup-result"]').text()).toContain('2 Datei(en) (9 MB) liegen jetzt in')
    expect(wrapper.emitted('done')).toHaveLength(1)
  })

  it('sagt, wenn es nichts aufzuräumen gibt, und bietet nichts zum Verschieben an', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ ...preview, files: [], skipped: [], count: 0, sizeBytes: 0 }))))

    const wrapper = mountButton()
    await wrapper.find('button[data-testid="legacy-cleanup"]').trigger('click')
    await flushPromises()

    expect(wrapper.find('[data-testid="cleanup-empty"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="cleanup-confirm"]').exists()).toBe(false)
  })
})

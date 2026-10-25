// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import PrimeVue from 'primevue/config'
import MatchDialog from './MatchDialog.vue'
import type { FileDto, MatchResponse, TrackDto } from '@/api'

const file: FileDto = { id: 5, format: 'Flac', bitrate: 900, sampleRate: 44100, sizeBytes: 1, path: 'Testband/a.flac' }
const track: TrackDto = { id: 1, disc: 1, number: 2, title: 'Zweiter Song', artists: ['Testband'], durationMs: 2000, bpm: null, initialKey: null, genre: null, files: [file] }

function mountDialog() {
  return mount(MatchDialog, {
    props: { visible: true, track, file },
    global: { plugins: [PrimeVue], stubs: { teleport: true } },
    attachTo: document.body,
  })
}

describe('MatchDialog', () => {
  it('listet Kandidaten mit Score und Release', async () => {
    const response: MatchResponse = {
      fileId: 5,
      durationSeconds: 2,
      candidates: [{
        recordingId: 'r1', acoustId: 'a1', title: 'Zweiter Song', artists: ['Testband', 'Gast'], durationMs: 2040,
        releaseId: 'rel1', releaseGroupId: 'rg1', releaseTitle: 'Testalbum', releaseArtist: 'Testband', releaseType: 'Album',
        releaseStatus: 'Official', year: 2024, disc: 1, trackNumber: 2, trackCount: 3, score: 0.97,
      }],
    }
    const fetch = vi.fn(async () => new Response(JSON.stringify(response)))
    vi.stubGlobal('fetch', fetch)

    const wrapper = mountDialog()
    await flushPromises()

    expect(fetch).toHaveBeenCalledWith('/api/library/files/5/candidates', expect.anything())
    expect(document.body.textContent).toContain('97 %')
    expect(document.body.textContent).toContain('Testalbum')
    expect(document.body.textContent).toContain('2/3')
    wrapper.unmount()
  })

  it('zeigt die Fehlermeldung der API', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ detail: 'AcoustID-API-Key fehlt' }), { status: 503 })))

    const wrapper = mountDialog()
    await flushPromises()

    expect(document.body.textContent).toContain('AcoustID-API-Key fehlt')
    wrapper.unmount()
  })
})

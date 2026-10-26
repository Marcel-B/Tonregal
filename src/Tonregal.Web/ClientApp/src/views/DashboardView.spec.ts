// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import PrimeVue from 'primevue/config'
import DashboardView from './DashboardView.vue'
import type { BackupResponse, BackupRun } from '@/api'

const run = (id: number, status: BackupRun['status'], extra: Partial<BackupRun> = {}): BackupRun => ({
  id, startedAt: '2026-10-26T02:00:00Z', finishedAt: '2026-10-26T02:42:00Z', status, filesSynced: 12, bytesSynced: 120_000_000, log: null, ...extra,
})

function mountView(backup: BackupResponse) {
  const responses: Record<string, unknown> = {
    '/api/health': { status: 'ok', libraryMounted: true, version: '1.0.0' },
    '/api/stats': { artists: 3, albums: 5, tracks: 42, files: 44, openReviews: 1, openDuplicates: 0, libraryBytes: 1_234_000_000, analyzedFiles: 11 },
    '/api/backup': backup,
  }
  const fetch = vi.fn(async (url: string, init?: RequestInit) =>
    init?.method === 'POST' ? new Response(JSON.stringify({ ...backup.status, running: true }), { status: 202 }) : new Response(JSON.stringify(responses[url])),
  )
  vi.stubGlobal('fetch', fetch)
  setActivePinia(createPinia())
  return { fetch, wrapper: mount(DashboardView, { global: { plugins: [PrimeVue] } }) }
}

const ok: BackupResponse = {
  status: { running: false, step: null, lastRun: run(2, 'Succeeded'), lastSuccessAt: '2026-10-26T02:42:00Z', consecutiveFailures: 0, nextRunAt: '2026-10-27T02:00:00Z' },
  runs: [run(2, 'Succeeded'), run(1, 'Failed', { log: 'rclone-Remote fehlt' })],
}

describe('DashboardView', () => {
  it('zeigt Bibliothekszahlen, Größe, Analyse-Anteil und Backup-Historie', async () => {
    const { wrapper } = mountView(ok)
    await flushPromises()

    expect(wrapper.get('[data-testid="tracks"]').text()).toBe('42')
    expect(wrapper.get('[data-testid="libraryBytes"]').text()).toBe('1,2 GB')
    expect(wrapper.get('[data-testid="analyzed"]').text()).toBe('25 %')
    expect(wrapper.get('[data-testid="backup-summary"]').text()).toContain('Letztes erfolgreiches Backup')
    expect(wrapper.findAll('[data-testid="backup-run"]').map((r) => r.text())).toEqual([
      expect.stringContaining('OK'),
      expect.stringContaining('Fehler'),
    ])
    expect(wrapper.find('[data-testid="backup-failing"]').exists()).toBe(false)
    expect(wrapper.text()).not.toContain('Mount-Marker')
  })

  it('markiert zwei Fehlschläge in Folge rot und startet ein Backup', async () => {
    const log = 'Library: 3 Datei(en) übertragen\nFailed to copy: Put "https://cloud.example/remote.php/dav/files/a/b.flac"\nFailed to sync: 507 Insufficient Storage'
    const failed = run(3, 'Failed', { log })
    const { fetch, wrapper } = mountView({ status: { ...ok.status, lastRun: failed, consecutiveFailures: 2 }, runs: [failed] })
    await flushPromises()

    const banner = wrapper.get('[data-testid="backup-failing"]').text()
    expect(banner).toContain('507 Insufficient Storage')
    expect(banner).not.toContain('remote.php')
    expect(wrapper.find('[data-testid="backup-log"]').exists()).toBe(false)
    await wrapper.get('[data-testid="backup-run"]').trigger('click')
    expect(wrapper.get('[data-testid="backup-log"]').text()).toContain('remote.php')
    await wrapper.get('[data-testid="run-backup"]').trigger('click')
    await flushPromises()
    expect(fetch).toHaveBeenCalledWith('/api/backup/run', expect.objectContaining({ method: 'POST' }))
    expect(wrapper.find('[data-testid="backup-failing"]').exists()).toBe(false)
    await wrapper.get('[data-testid="cancel-backup"]').trigger('click')
    await flushPromises()
    expect(fetch).toHaveBeenCalledWith('/api/backup/cancel', expect.objectContaining({ method: 'POST' }))
  })
})

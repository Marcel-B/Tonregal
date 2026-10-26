// SPDX-License-Identifier: AGPL-3.0-or-later
import { beforeEach, describe, expect, it } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { usePlayerStore, type QueueItem } from './player'

const item = (fileId: number): QueueItem => ({ fileId, trackId: fileId, title: `Song ${fileId}`, artist: 'A', album: 'B', albumId: 1, durationMs: 1000 })

describe('player store', () => {
  beforeEach(() => {
    localStorage.clear()
    setActivePinia(createPinia())
  })

  it('schaltet weiter und hält am Ende an', () => {
    const player = usePlayerStore()
    player.play([item(1), item(2)])
    player.next()
    expect(player.playing).toBe(true)
    expect(player.current?.fileId).toBe(2)
    player.next()
    expect(player.playing).toBe(false)
    expect(player.current?.fileId).toBe(2)
  })

  it('reiht hinter dem laufenden Stück ein und startet bei leerer Queue', () => {
    const player = usePlayerStore()
    player.playNext([item(9)])
    expect(player.current?.fileId).toBe(9)
    player.enqueue([item(1), item(2)])
    player.playNext([item(5)])
    expect(player.queue.map((i) => i.fileId)).toEqual([9, 5, 1, 2])
  })

  it('entfernt Stücke und hält den Index', () => {
    const player = usePlayerStore()
    player.play([item(1), item(2), item(3)], 1)
    player.remove(0)
    expect(player.current?.fileId).toBe(2)
    player.remove(0)
    expect(player.current?.fileId).toBe(3)
    player.remove(0)
    expect(player.current).toBeNull()
    expect(player.playing).toBe(false)
  })

  it('merkt sich die Queue, spielt nach dem Neuladen aber nicht von selbst', async () => {
    usePlayerStore().play([item(1), item(2)], 1)
    await Promise.resolve()

    setActivePinia(createPinia())
    const reloaded = usePlayerStore()
    expect(reloaded.current?.fileId).toBe(2)
    expect(reloaded.playing).toBe(false)
  })
})

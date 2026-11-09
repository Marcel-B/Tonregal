// SPDX-License-Identifier: AGPL-3.0-or-later
import { defineStore } from 'pinia'
import { ref } from 'vue'
import { api, type PlaylistAddResult, type PlaylistSummary } from '@/api'

/**
 * Die eigenen Playlists für die Liste und das Menü „Zur Playlist“. Jede Änderung legt die Antwort des Servers hier ab,
 * damit Anzahl und Reihenfolge überall gleich sind.
 */
export const usePlaylistsStore = defineStore('playlists', () => {
  const list = ref<PlaylistSummary[]>([])
  const loaded = ref(false)

  const sorted = (items: PlaylistSummary[]) => [...items].sort((a, b) => a.name.localeCompare(b.name, 'de', { sensitivity: 'base' }))

  function put(playlist: PlaylistSummary) {
    list.value = sorted([...list.value.filter((p) => p.id !== playlist.id), playlist])
  }

  async function load() {
    list.value = sorted(await api.playlists())
    loaded.value = true
  }

  async function create(name: string, comment: string | null = null) {
    const created = await api.createPlaylist(name, comment)
    put(created)
    return created
  }

  async function add(id: number, trackIds: number[]): Promise<PlaylistAddResult> {
    const result = await api.addToPlaylist(id, trackIds)
    put(result.playlist)
    return result
  }

  function forget(id: number) {
    list.value = list.value.filter((p) => p.id !== id)
  }

  return { list, loaded, load, create, add, put, forget }
})

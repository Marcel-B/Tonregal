// SPDX-License-Identifier: AGPL-3.0-or-later
import { defineStore } from 'pinia'
import { ref } from 'vue'
import { api } from '@/api'

/**
 * Sterne je Datei, wie Navidrome sie hält. Player-Leiste und Albumansicht teilen sich den Stand, damit eine Bewertung
 * an beiden Stellen gleich erscheint. Unbekannt und unbewertet sind beide 0.
 */
export const useRatingsStore = defineStore('ratings', () => {
  const byFile = ref<Record<number, number>>({})
  const error = ref<string | null>(null)

  async function load(fileIds: number[]) {
    try {
      const loaded = await api.ratings(fileIds)
      const next = { ...byFile.value }
      for (const id of fileIds) next[id] = loaded[String(id)] ?? 0
      byFile.value = next
    } catch {
      // Ohne Bewertungen weiterspielen
    }
  }

  /** Sofort anzeigen, bei einer Absage von Navidrome zurücknehmen. */
  async function rate(fileId: number, rating: number) {
    const before = byFile.value[fileId] ?? 0
    byFile.value = { ...byFile.value, [fileId]: rating }
    error.value = null
    try {
      await api.rate(fileId, rating)
    } catch (e) {
      byFile.value = { ...byFile.value, [fileId]: before }
      error.value = e instanceof Error ? e.message : String(e)
    }
  }

  const of = (fileId: number) => byFile.value[fileId] ?? 0

  return { byFile, error, load, rate, of }
})

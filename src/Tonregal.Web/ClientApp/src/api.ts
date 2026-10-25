// SPDX-License-Identifier: AGPL-3.0-or-later
export interface HealthResponse {
  status: string
  libraryMounted: boolean
  version: string
}

export interface LibraryStats {
  artists: number
  albums: number
  tracks: number
  files: number
  openReviews: number
  openDuplicates: number
}

async function get<T>(path: string): Promise<T> {
  const response = await fetch(`/api${path}`, { headers: { Accept: 'application/json' } })
  if (!response.ok) throw new Error(`${path}: HTTP ${response.status}`)
  return (await response.json()) as T
}

export const api = {
  health: () => get<HealthResponse>('/health'),
  stats: () => get<LibraryStats>('/stats'),
}

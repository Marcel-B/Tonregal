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

export type AlbumKind = 'Album' | 'Ep' | 'Single' | 'Compilation'
export type AudioFormat = 'Unknown' | 'Mp3' | 'Flac' | 'Aac' | 'Alac' | 'Ogg' | 'Opus' | 'Wav'

export interface ArtistSummary {
  id: number
  name: string
  sortName: string
  albumCount: number
}

export interface AlbumSummary {
  id: number
  title: string
  year: number | null
  kind: AlbumKind
  trackCount: number
}

export interface ArtistDetail {
  id: number
  name: string
  albums: AlbumSummary[]
}

export interface FileDto {
  id: number
  format: AudioFormat
  bitrate: number
  sampleRate: number
  sizeBytes: number
  path: string
}

export interface TrackDto {
  id: number
  disc: number
  number: number
  title: string
  artists: string[]
  durationMs: number
  bpm: number | null
  initialKey: string | null
  genre: string | null
  files: FileDto[]
}

export interface AlbumDetail {
  id: number
  title: string
  year: number | null
  kind: AlbumKind
  discCount: number
  albumArtist: { id: number; name: string }
  tracks: TrackDto[]
}

export interface ScanProgress {
  processed: number
  total: number
  currentPath: string | null
}

export interface ScanResult {
  added: number
  updated: number
  removed: number
  unchanged: number
  errors: { path: string; message: string }[]
  duration: string
}

export interface ScanStatus {
  running: boolean
  progress: ScanProgress | null
  lastRunAt: string | null
  lastResult: ScanResult | null
  lastError: string | null
}

export interface RecordingCandidate {
  recordingId: string
  acoustId: string | null
  title: string
  artists: string[]
  durationMs: number | null
  releaseId: string | null
  releaseGroupId: string | null
  releaseTitle: string | null
  releaseArtist: string | null
  releaseType: string | null
  releaseStatus: string | null
  year: number | null
  disc: number | null
  trackNumber: number | null
  trackCount: number | null
  releaseDate: string | null
  country: string | null
  mediumFormat: string | null
  score: number
}

export interface MatchResponse {
  fileId: number
  durationSeconds: number
  candidates: RecordingCandidate[]
}

export interface ToolStatus {
  fpcalc: string | null
  acoustIdConfigured: boolean
}

async function request<T>(method: string, path: string): Promise<T> {
  const response = await fetch(`/api${path}`, { method, headers: { Accept: 'application/json' } })
  if (!response.ok) {
    // ProblemDetails der API tragen die eigentliche Fehlermeldung in "detail".
    const problem = await response.json().catch(() => null)
    throw new Error(problem?.detail ?? `${path}: HTTP ${response.status}`)
  }
  return (await response.json()) as T
}

const get = <T>(path: string) => request<T>('GET', path)

export const api = {
  health: () => get<HealthResponse>('/health'),
  stats: () => get<LibraryStats>('/stats'),
  artists: () => get<ArtistSummary[]>('/library/artists'),
  artist: (id: number) => get<ArtistDetail>(`/library/artists/${id}`),
  album: (id: number) => get<AlbumDetail>(`/library/albums/${id}`),
  coverUrl: (albumId: number) => `/api/library/albums/${albumId}/cover`,
  scanStatus: () => get<ScanStatus>('/library/scan'),
  startScan: () => request<ScanStatus>('POST', '/library/scan'),
  tools: () => get<ToolStatus>('/tools'),
  candidates: (fileId: number) => get<MatchResponse>(`/library/files/${fileId}/candidates`),
}

export function formatDuration(ms: number): string {
  const seconds = Math.round(ms / 1000)
  return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`
}

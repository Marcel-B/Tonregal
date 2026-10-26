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
  canFetchCover: boolean
  /** Verschiedene MusicBrainz-Releases in den Tags; ab 2 teilt Navidrome das Album auf. */
  releaseCount: number
}

export interface ReleaseAlignResult {
  releaseId: string | null
  changed: number
  skipped: number
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

export type ImportState = 'New' | 'Fingerprinted' | 'Matched' | 'NeedsReview' | 'Imported' | 'Rejected'

export interface ImportJob {
  id: number
  path: string
  group: string
  state: ImportState
  format: AudioFormat
  durationMs: number
  tagTitle: string | null
  tagArtist: string | null
  tagAlbum: string | null
  matchScore: number | null
  reviewReason: string | null
  error: string | null
  selected: RecordingCandidate | null
  candidateCount: number
  startedAt: string
}

/** Woher eine Datei kommt: Inbox (wird verschoben) oder Altbestand (wird nur kopiert). */
export type ImportSource = 'Inbox' | 'Legacy'

/** Massenlauf über den Altbestand; je Datei zählt ihr jüngster Durchlauf. */
export interface LegacySummary {
  path: string
  exists: boolean
  total: number
  pending: number
  matched: number
  needsReview: number
  imported: number
  alreadyInLibrary: number
  rejected: number
}

export interface InboxStatus {
  running: boolean
  currentPath: string | null
  processed: number
  waiting: number
  imported: number
  importsPending: number
  lastRunAt: string | null
  lastError: string | null
}

export interface InboxResponse {
  status: InboxStatus
  jobs: ImportJob[]
}

export interface ImportJobCandidates {
  jobId: number
  selectedRecordingId: string | null
  selectedReleaseId: string | null
  candidates: RecordingCandidate[]
}

export type DuplicateKind = 'ExactHash' | 'Fingerprint' | 'Metadata'
export type FileSource = 'Inbox' | 'Purchase' | 'Legacy' | 'Api'

export interface DuplicateFile {
  id: number
  path: string
  format: AudioFormat
  bitrate: number
  sampleRate: number
  sizeBytes: number
  durationMs: number
  title: string
  artists: string[]
  albumId: number
  album: string
  year: number | null
  hasMusicBrainzId: boolean
  source: FileSource
  addedAt: string
}

export interface DuplicateGroup {
  id: number
  kind: DuplicateKind
  suggestedFileId: number | null
  createdAt: string
  files: DuplicateFile[]
}

export interface ToolStatus {
  fpcalc: string | null
  acoustIdConfigured: boolean
}

async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json' }
  const form = body instanceof FormData
  if (body !== undefined && !form) headers['Content-Type'] = 'application/json'
  const response = await fetch(`/api${path}`, { method, headers, body: body === undefined ? undefined : form ? body : JSON.stringify(body) })
  if (!response.ok) {
    // ProblemDetails der API tragen die eigentliche Fehlermeldung in "detail".
    const problem = await response.json().catch(() => null)
    throw new Error(problem?.detail ?? `${path}: HTTP ${response.status}`)
  }
  return (response.status === 204 ? undefined : await response.json()) as T
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
  inbox: (source: ImportSource = 'Inbox') => get<InboxResponse>(`/inbox?source=${source}`),
  scanInbox: () => request<InboxStatus>('POST', '/inbox/scan'),
  importCandidates: (jobId: number) => get<ImportJobCandidates>(`/inbox/${jobId}/candidates`),
  retryImport: (jobId: number) => request<ImportJob>('POST', `/inbox/${jobId}/retry`),
  selectCandidate: (jobId: number, recordingId: string, releaseId: string) =>
    request<ImportJob>('POST', `/inbox/${jobId}/select`, { recordingId, releaseId }),
  retryAllImports: (source: ImportSource = 'Inbox') => request<{ count: number }>('POST', `/inbox/retry-all?source=${source}`),
  inboxReport: async (source: ImportSource = 'Inbox') => {
    const response = await fetch(`/api/inbox/report?source=${source}`)
    if (!response.ok) throw new Error(`/inbox/report: HTTP ${response.status}`)
    return response.text()
  },
  rejectImport: (jobId: number) => request<void>('POST', `/inbox/${jobId}/reject`),
  importJobs: (jobIds: number[]) => request<InboxStatus>('POST', '/inbox/import', { jobIds }),
  importUnmatched: (jobId: number) => request<ImportJob>('POST', `/inbox/${jobId}/import-unmatched`),
  legacy: () => get<LegacySummary>('/inbox/legacy'),
  scanLegacy: () => request<InboxStatus>('POST', '/inbox/legacy/scan'),
  releaseCoverUrl: (releaseId: string) => `https://coverartarchive.org/release/${releaseId}/front-250`,
  fetchCover: (albumId: number) => request<void>('POST', `/library/albums/${albumId}/cover`),
  alignRelease: (albumId: number) => request<ReleaseAlignResult>('POST', `/library/albums/${albumId}/align-release`),
  audioUrl: (fileId: number) => `/api/library/files/${fileId}/audio`,
  duplicates: () => get<DuplicateGroup[]>('/duplicates'),
  scanDuplicates: () => request<ScanStatus>('POST', '/duplicates/scan'),
  resolveDuplicate: (groupId: number, keepFileIds: number[]) => request<void>('POST', `/duplicates/${groupId}/resolve`, { keepFileIds }),
  /** Eine Datei in die Inbox; path ist relativ zur Inbox und bestimmt den Album-Ordner. */
  upload: (file: File, path: string) => {
    const form = new FormData()
    form.append('file', file, file.name)
    form.append('path', path)
    return request<{ path: string }>('POST', '/inbox/upload', form)
  },
}

/** Endungen, die Tonregal liest; alles andere wird beim Hochladen übersprungen. */
export const audioExtensions = ['.mp3', '.flac', '.m4a', '.aac', '.ogg', '.opus', '.wav']

export const isAudioFile = (name: string) => audioExtensions.some((ext) => name.toLowerCase().endsWith(ext))

export function formatDuration(ms: number): string {
  const seconds = Math.round(ms / 1000)
  return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`
}

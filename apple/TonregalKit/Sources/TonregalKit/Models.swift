// SPDX-License-Identifier: AGPL-3.0-or-later
import Foundation

// Spiegel der Records in Tonregal.Web/Api (LibraryApi.cs, TonregalApi.cs). Enums kommen als PascalCase-Strings
// (JsonStringEnumConverter) und bleiben hier Strings, damit ein neuer Wert die App nicht bricht.

public struct Health: Codable, Sendable, Equatable {
    public let status: String
    public let libraryMounted: Bool
    public let version: String
}

public struct ArtistSummary: Codable, Sendable, Hashable, Identifiable {
    public let id: Int
    public let name: String
    public let sortName: String
    public let albumCount: Int
}

public struct ArtistRef: Codable, Sendable, Hashable {
    public let id: Int
    public let name: String
}

public struct AlbumSummary: Codable, Sendable, Hashable, Identifiable {
    public let id: Int
    public let title: String
    public let year: Int?
    public let kind: String
    public let trackCount: Int
}

public struct ArtistDetail: Codable, Sendable, Hashable, Identifiable {
    public let id: Int
    public let name: String
    public let albums: [AlbumSummary]
}

/// Ein Eintrag aus `GET /api/library/albums`, zuletzt übernommene zuerst.
public struct AlbumListItem: Codable, Sendable, Hashable, Identifiable {
    public let id: Int
    public let title: String
    public let year: Int?
    public let kind: String
    public let trackCount: Int
    public let albumArtist: ArtistRef
}

public struct AudioFile: Codable, Sendable, Hashable, Identifiable {
    public let id: Int
    public let format: String
    public let bitrate: Int
    public let sampleRate: Int
    public let sizeBytes: Int64
    public let path: String
}

public struct Track: Codable, Sendable, Hashable, Identifiable {
    public let id: Int
    public let disc: Int
    public let number: Int
    public let title: String
    public let artists: [String]
    public let durationMs: Int
    public let bpm: Double?
    public let initialKey: String?
    public let genre: String?
    /// Bevorzugte Datei zuerst (FLAC, dann höchste Bitrate), wie der Server sie sortiert.
    public let files: [AudioFile]
}

public struct AlbumDetail: Codable, Sendable, Hashable, Identifiable {
    public let id: Int
    public let title: String
    public let year: Int?
    public let kind: String
    public let discCount: Int
    public let albumArtist: ArtistRef
    public let tracks: [Track]
}

/// So antwortet `GET /api/library/search`; `trackCount` zählt alle passenden Titel, `tracks` hält höchstens 100.
public struct LibrarySearchResult: Codable, Sendable, Hashable {
    public let query: String
    public let artists: [ArtistSummary]
    public let albums: [AlbumListItem]
    public let tracks: [QueueItem]
    public let trackCount: Int

    public var isEmpty: Bool { artists.isEmpty && albums.isEmpty && tracks.isEmpty }
}

public enum Duration {
    /// `3:07` oder `1:02:03`, wie `formatDuration` im Web-Client.
    public static func format(seconds: Double) -> String {
        guard seconds.isFinite, seconds > 0 else { return "0:00" }
        let total = Int(seconds.rounded(.down))
        let (h, m, s) = (total / 3600, total / 60 % 60, total % 60)
        return h > 0 ? String(format: "%d:%02d:%02d", h, m, s) : String(format: "%d:%02d", m, s)
    }
}

/// Eine eigene Playlist mit festen Stücken, wie `PlaylistSummary` in Tonregal.Application (IPlaylists.cs).
public struct PlaylistSummary: Codable, Sendable, Hashable, Identifiable {
    public let id: Int
    public let name: String
    public let comment: String?
    public let trackCount: Int
    public let durationMs: Int64
    public let updatedAt: String
    /// Bis zu vier verschiedene Alben der ersten Stücke, fürs Cover-Mosaik.
    public let albumIds: [Int]
}

/// `GET /api/playlists/{id}`: die Stücke in ihrer Reihenfolge, je Track die Datei, die auch der Web-Player nimmt.
public struct PlaylistDetail: Codable, Sendable, Hashable {
    public let playlist: PlaylistSummary
    public let items: [QueueItem]

    /// Die App zeigt eine neue Reihenfolge sofort und ersetzt sie dann durch die Antwort des Servers.
    public init(playlist: PlaylistSummary, items: [QueueItem]) {
        self.playlist = playlist
        self.items = items
    }
}

/// Antwort auf „hinzufügen“: `skipped` standen schon darin.
public struct PlaylistAddResult: Codable, Sendable, Hashable {
    public let added: Int
    public let skipped: Int
    public let playlist: PlaylistSummary
}

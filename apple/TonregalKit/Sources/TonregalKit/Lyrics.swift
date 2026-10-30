// SPDX-License-Identifier: AGPL-3.0-or-later
import Foundation

/// Spiegel von `SongLyrics` (`GET /api/player/tracks/{id}/lyrics`). Der Server zerlegt LRC schon in Zeilen.
public struct SongLyrics: Codable, Sendable, Equatable {
    /// `None`, `Sidecar` (.lrc neben der Datei), `Tag` (eingebettet) oder `Lrclib`.
    public let source: String
    public let synced: Bool
    public let instrumental: Bool
    public let lines: [LyricLine]

    public init(source: String, synced: Bool, instrumental: Bool, lines: [LyricLine]) {
        self.source = source
        self.synced = synced
        self.instrumental = instrumental
        self.lines = lines
    }

    /// Index der Zeile, die bei `seconds` gesungen wird (die letzte, die schon begonnen hat), wie `currentLineIndex`
    /// im Web-Client; nil davor oder bei reinem Text.
    public func currentLine(at seconds: Double) -> Int? {
        guard synced else { return nil }
        let ms = Int((seconds * 1000).rounded(.down))
        var low = 0, high = lines.count - 1
        var found: Int?
        while low <= high {
            let mid = (low + high) / 2
            guard let time = lines[mid].timeMs else { return nil }
            if time <= ms {
                found = mid
                low = mid + 1
            } else {
                high = mid - 1
            }
        }
        return found
    }

    /// Für die Fußzeile, wie `lyricsSourceLabel` im Web-Client.
    public var sourceLabel: String? {
        switch source {
        case "Sidecar": ".lrc-Datei"
        case "Tag": "in der Datei eingebettet"
        case "Lrclib": "lrclib.net"
        default: nil
        }
    }
}

public struct LyricLine: Codable, Sendable, Hashable {
    /// Nur bei synchronisierten Texten.
    public let timeMs: Int?
    public let text: String

    public init(timeMs: Int?, text: String) {
        self.timeMs = timeMs
        self.text = text
    }
}

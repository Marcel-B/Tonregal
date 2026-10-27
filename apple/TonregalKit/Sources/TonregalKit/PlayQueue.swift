// SPDX-License-Identifier: AGPL-3.0-or-later
import Foundation

/// Ein Stück in der Warteschlange, wie `QueueItem` im Web-Client (stores/player.ts).
public struct QueueItem: Codable, Sendable, Hashable {
    public let fileId: Int
    public let trackId: Int
    public let title: String
    public let artist: String
    public let album: String
    public let albumId: Int
    public let durationMs: Int

    public init(fileId: Int, trackId: Int, title: String, artist: String, album: String, albumId: Int, durationMs: Int) {
        self.fileId = fileId
        self.trackId = trackId
        self.title = title
        self.artist = artist
        self.album = album
        self.albumId = albumId
        self.durationMs = durationMs
    }

    /// Tracks eines Albums in Abspielreihenfolge; Tracks ohne Datei fallen weg, je Track die bevorzugte Datei.
    public static func album(_ album: AlbumDetail) -> [QueueItem] {
        album.tracks.compactMap { track in
            guard let file = track.files.first else { return nil }
            return QueueItem(
                fileId: file.id, trackId: track.id, title: track.title,
                artist: track.artists.isEmpty ? album.albumArtist.name : track.artists.joined(separator: ", "),
                album: album.title, albumId: album.id, durationMs: track.durationMs)
        }
    }
}

/// Die Warteschlange ohne AVFoundation, damit sie sich testen lässt. Jede Änderung sagt, ob das laufende Stück neu
/// geladen werden muss; der Player tut dann genau das.
public struct PlayQueue: Codable, Sendable, Equatable {
    public private(set) var items: [QueueItem] = []
    public private(set) var index: Int?

    public init(items: [QueueItem] = [], index: Int? = nil) {
        self.items = items
        self.index = index.flatMap { items.indices.contains($0) ? $0 : nil }
    }

    public var current: QueueItem? { index.flatMap { items.indices.contains($0) ? items[$0] : nil } }
    public var hasNext: Bool { index.map { $0 + 1 < items.count } ?? false }
    public var upcoming: ArraySlice<QueueItem> { index.map { items[min($0 + 1, items.count)...] } ?? items[...] }

    public mutating func play(_ newItems: [QueueItem], at position: Int = 0) {
        items = newItems
        index = newItems.isEmpty ? nil : min(max(0, position), newItems.count - 1)
    }

    /// Hinten anhängen; true, wenn die Schlange leer war und damit gleich etwas startet.
    @discardableResult
    public mutating func enqueue(_ newItems: [QueueItem]) -> Bool {
        guard !newItems.isEmpty else { return false }
        let wasEmpty = current == nil
        items.append(contentsOf: newItems)
        if wasEmpty {
            index = items.count - newItems.count
        }
        return wasEmpty
    }

    /// Direkt nach dem laufenden Stück einreihen.
    @discardableResult
    public mutating func playNext(_ newItems: [QueueItem]) -> Bool {
        guard let index else { return enqueue(newItems) }
        items.insert(contentsOf: newItems, at: index + 1)
        return false
    }

    /// Zum nächsten Stück; false am Ende der Schlange.
    public mutating func next() -> Bool {
        guard let index, index + 1 < items.count else { return false }
        self.index = index + 1
        return true
    }

    public mutating func previous() -> Bool {
        guard let index, index > 0 else { return false }
        self.index = index - 1
        return true
    }

    public mutating func jump(to position: Int) -> Bool {
        guard items.indices.contains(position) else { return false }
        index = position
        return true
    }

    /// Entfernt Einträge; true, wenn das laufende Stück darunter war und das nächste geladen werden muss.
    @discardableResult
    public mutating func remove(at offsets: IndexSet) -> Bool {
        guard let current = index else {
            items.removeOffsets(offsets)
            return false
        }
        let removesCurrent = offsets.contains(current)
        let before = offsets.filter { $0 < current }.count
        items.removeOffsets(offsets)
        if items.isEmpty {
            index = nil
        } else {
            index = min(current - before, items.count - 1)
        }
        return removesCurrent
    }

    public mutating func move(from source: IndexSet, to destination: Int) {
        // Dasselbe Stück kann mehrfach in der Schlange stehen; daher die Positionen mitverschieben statt zu suchen.
        var order = Array(items.indices)
        order.moveOffsets(source, to: destination)
        items.moveOffsets(source, to: destination)
        if let playing = index {
            index = order.firstIndex(of: playing)
        }
    }

    public mutating func shuffleUpcoming() {
        guard let index else {
            items.shuffle()
            return
        }
        let rest = items[(index + 1)...].shuffled()
        items.replaceSubrange((index + 1)..., with: rest)
    }

    public mutating func clear() {
        items = []
        index = nil
    }
}

/// Wann eine Wiedergabe zählt: nach der Hälfte, spätestens nach 4 Minuten (Last.fm-Regel, wie Navidrome und der
/// Web-Player sie anwenden).
public enum Scrobble {
    public static func due(elapsed: Double, duration: Double) -> Bool {
        duration > 0 && elapsed >= min(duration / 2, 240)
    }
}

// `remove(atOffsets:)` und `move(fromOffsets:toOffset:)` kommen sonst aus SwiftUI; das Paket bleibt ohne.
extension Array {
    mutating func removeOffsets(_ offsets: IndexSet) {
        for offset in offsets.reversed() where indices.contains(offset) {
            remove(at: offset)
        }
    }

    /// Wie SwiftUIs `move(fromOffsets:toOffset:)`: `destination` zählt in der Liste vor dem Verschieben.
    mutating func moveOffsets(_ source: IndexSet, to destination: Int) {
        let moving = source.filter { indices.contains($0) }.map { self[$0] }
        let target = destination - source.filter { $0 < destination }.count
        removeOffsets(source)
        insert(contentsOf: moving, at: Swift.min(Swift.max(0, target), count))
    }
}

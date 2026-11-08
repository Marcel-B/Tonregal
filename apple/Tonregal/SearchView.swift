// SPDX-License-Identifier: AGPL-3.0-or-later
import SwiftUI
import TonregalKit

/// Suche über die ganze Bibliothek: Titel, Alben und Interpreten auf einmal. Gesucht wird auf dem Server, weil die App
/// keine Titelliste vorhält; Akzente und Großschreibung zählen dort nicht.
struct SearchView: View {
    @Environment(AppModel.self) private var model
    @Environment(Player.self) private var player
    @State private var query = ""
    @State private var result: LibrarySearchResult?
    @State private var searching = false
    @State private var error: String?

    private var trimmed: String { query.trimmingCharacters(in: .whitespacesAndNewlines) }

    var body: some View {
        List {
            if let result, !trimmed.isEmpty {
                if !result.tracks.isEmpty {
                    Section {
                        ForEach(Array(result.tracks.enumerated()), id: \.element.trackId) { index, track in
                            SearchTrackRow(track: track, playing: player.current?.trackId == track.trackId) {
                                // Ab dem Treffer, danach die übrigen: so läuft eine Suche nach einem Interpreten einfach weiter.
                                player.play(result.tracks, at: index)
                            }
                        }
                    } header: {
                        Text(result.trackCount > result.tracks.count ? "Titel · die ersten \(result.tracks.count) von \(result.trackCount)" : "Titel")
                    }
                }
                if !result.albums.isEmpty {
                    Section("Alben") {
                        ForEach(result.albums) { album in
                            NavigationLink(value: Route.album(album.id)) {
                                HStack(spacing: 12) {
                                    CoverImage(url: model.client?.coverURL(album: album.id), cornerRadius: 4)
                                        .frame(width: 44, height: 44)
                                    VStack(alignment: .leading, spacing: 2) {
                                        Text(album.title).lineLimit(1)
                                        Text([album.albumArtist.name, album.year.map(String.init)].compactMap { $0 }.joined(separator: " · "))
                                            .font(.caption)
                                            .foregroundStyle(.secondary)
                                            .lineLimit(1)
                                    }
                                }
                            }
                        }
                    }
                }
                if !result.artists.isEmpty {
                    Section("Interpreten") {
                        ForEach(result.artists) { artist in
                            NavigationLink(value: Route.artist(artist.id)) {
                                Text(artist.name)
                            }
                        }
                    }
                }
            }
        }
        .listStyle(.plain)
        .navigationTitle("Suche")
        .searchable(text: $query, placement: .navigationBarDrawer(displayMode: .always), prompt: "Titel, Album oder Interpret")
        .autocorrectionDisabled()
        .task(id: trimmed) { await search(trimmed) }
        .task(id: result?.tracks.map(\.fileId)) {
            if let files = result?.tracks.map(\.fileId), !files.isEmpty {
                await player.loadRatings(files: files)
            }
        }
        .overlay {
            if trimmed.isEmpty {
                ContentUnavailableView("Bibliothek durchsuchen", systemImage: "magnifyingglass",
                                       description: Text("Nach Titel, Album oder Interpret, auch mehrere Wörter: „beatles help“."))
            } else if let error {
                ContentUnavailableView("Nicht gesucht", systemImage: "wifi.slash", description: Text(error))
            } else if let result, result.isEmpty, !searching {
                ContentUnavailableView.search(text: trimmed)
            } else if result == nil {
                ProgressView()
            }
        }
    }

    /// Kurz warten, bis das Tippen ruht; `.task(id:)` bricht die vorige Suche ab, sobald sich der Text ändert.
    private func search(_ text: String) async {
        guard !text.isEmpty else {
            result = nil
            error = nil
            return
        }
        do {
            try await Task.sleep(for: .milliseconds(300))
        } catch {
            return
        }
        guard let client = model.client else { return }
        searching = true
        defer { searching = false }
        do {
            let found = try await client.search(text)
            guard !Task.isCancelled else { return }
            result = found
            error = nil
        } catch is CancellationError {
        } catch {
            guard !Task.isCancelled else { return }
            self.error = error.localizedDescription
        }
    }
}

private struct SearchTrackRow: View {
    @Environment(AppModel.self) private var model
    @Environment(Player.self) private var player
    let track: QueueItem
    let playing: Bool
    let play: () -> Void

    var body: some View {
        Button(action: play) {
            HStack(spacing: 12) {
                CoverImage(url: model.client?.coverURL(album: track.albumId), cornerRadius: 4)
                    .frame(width: 44, height: 44)
                VStack(alignment: .leading, spacing: 2) {
                    Text(track.title)
                        .foregroundStyle(playing ? AnyShapeStyle(.tint) : AnyShapeStyle(.primary))
                        .lineLimit(1)
                    Text("\(track.artist) · \(track.album)")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                }
                Spacer()
                StarsBadge(rating: player.rating(file: track.fileId))
                Text(Duration.format(seconds: Double(track.durationMs) / 1000))
                    .font(.subheadline.monospacedDigit())
                    .foregroundStyle(.secondary)
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .swipeActions(edge: .leading) {
            Button {
                player.playNext([track])
            } label: {
                Label("Als Nächstes", systemImage: "text.line.first.and.arrowtriangle.forward")
            }
            .tint(.indigo)
        }
        .swipeActions(edge: .trailing) {
            Button {
                player.enqueue([track])
            } label: {
                Label("Anhängen", systemImage: "text.line.last.and.arrowtriangle.forward")
            }
            .tint(.orange)
        }
        .contextMenu {
            Button("Als Nächstes", systemImage: "text.line.first.and.arrowtriangle.forward") { player.playNext([track]) }
            Button("Hinten anhängen", systemImage: "text.line.last.and.arrowtriangle.forward") { player.enqueue([track]) }
            RatingMenu(fileId: track.fileId)
        }
    }
}

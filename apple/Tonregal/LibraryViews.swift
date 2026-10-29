// SPDX-License-Identifier: AGPL-3.0-or-later
import SwiftUI
import TonregalKit

struct AlbumTile: Identifiable, Hashable {
    let id: Int
    let title: String
    let subtitle: String
}

struct AlbumGrid: View {
    @Environment(AppModel.self) private var model
    let albums: [AlbumTile]

    var body: some View {
        LazyVGrid(columns: [GridItem(.adaptive(minimum: 150), spacing: 16)], spacing: 20) {
            ForEach(albums) { album in
                NavigationLink(value: Route.album(album.id)) {
                    VStack(alignment: .leading, spacing: 4) {
                        CoverImage(url: model.client?.coverURL(album: album.id))
                        Text(album.title)
                            .font(.subheadline.weight(.medium))
                            .lineLimit(1)
                        Text(album.subtitle)
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            .lineLimit(1)
                    }
                }
                .buttonStyle(.plain)
            }
        }
        .padding()
    }
}

struct AlbumsView: View {
    @Environment(AppModel.self) private var model
    @State private var search = ""

    private var tiles: [AlbumTile] {
        model.albums
            .filter { search.isEmpty || $0.title.localizedCaseInsensitiveContains(search) || $0.albumArtist.name.localizedCaseInsensitiveContains(search) }
            .map { AlbumTile(id: $0.id, title: $0.title, subtitle: $0.albumArtist.name) }
    }

    var body: some View {
        ScrollView {
            AlbumGrid(albums: tiles)
        }
        .navigationTitle("Alben")
        .searchable(text: $search, prompt: "Album oder Interpret")
        .refreshable {
            ImageCache.shared.forgetMissing()
            await model.loadAlbums()
        }
        .task {
            if model.albums.isEmpty {
                await model.loadAlbums()
            }
        }
        .overlay {
            if model.albums.isEmpty, let error = model.libraryError {
                ContentUnavailableView {
                    Label("Keine Verbindung", systemImage: "wifi.slash")
                } description: {
                    Text(error)
                } actions: {
                    Button("Nochmal") { Task { await model.loadAlbums() } }
                }
            }
        }
    }
}

struct ArtistsView: View {
    @Environment(AppModel.self) private var model
    @State private var search = ""

    private var artists: [ArtistSummary] {
        search.isEmpty ? model.artists : model.artists.filter { $0.name.localizedCaseInsensitiveContains(search) }
    }

    var body: some View {
        List(artists) { artist in
            NavigationLink(value: Route.artist(artist.id)) {
                VStack(alignment: .leading) {
                    Text(artist.name)
                    Text(artist.albumCount == 1 ? "1 Album" : "\(artist.albumCount) Alben")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }
        }
        .listStyle(.plain)
        .navigationTitle("Interpreten")
        .searchable(text: $search, prompt: "Interpret")
        .refreshable { await model.loadArtists() }
        .task {
            if model.artists.isEmpty {
                await model.loadArtists()
            }
        }
        .overlay {
            if model.artists.isEmpty, let error = model.libraryError {
                ContentUnavailableView("Keine Verbindung", systemImage: "wifi.slash", description: Text(error))
            }
        }
    }
}

struct ArtistView: View {
    @Environment(AppModel.self) private var model
    let id: Int
    @State private var artist: ArtistDetail?
    @State private var error: String?

    var body: some View {
        ScrollView {
            if let artist {
                AlbumGrid(albums: artist.albums.map {
                    AlbumTile(id: $0.id, title: $0.title, subtitle: $0.year.map(String.init) ?? "\($0.trackCount) Titel")
                })
            }
        }
        .navigationTitle(artist?.name ?? "")
        .overlay {
            if artist == nil {
                if let error {
                    ContentUnavailableView("Nicht geladen", systemImage: "exclamationmark.triangle", description: Text(error))
                } else {
                    ProgressView()
                }
            }
        }
        .task(id: id) {
            guard let client = model.client else { return }
            do {
                artist = try await client.artist(id)
            } catch {
                self.error = error.localizedDescription
            }
        }
    }
}

struct AlbumView: View {
    @Environment(AppModel.self) private var model
    @Environment(Player.self) private var player
    let id: Int
    @State private var album: AlbumDetail?
    @State private var error: String?

    var body: some View {
        Group {
            if let album {
                content(album)
            } else if let error {
                ContentUnavailableView("Nicht geladen", systemImage: "exclamationmark.triangle", description: Text(error))
            } else {
                ProgressView()
            }
        }
        .navigationBarTitleDisplayMode(.inline)
        .task(id: id) {
            guard let client = model.client else { return }
            do {
                album = try await client.album(id)
            } catch {
                self.error = error.localizedDescription
            }
        }
    }

    private func content(_ album: AlbumDetail) -> some View {
        List {
            Section {
                VStack(spacing: 12) {
                    CoverImage(url: model.client?.coverURL(album: album.id), cornerRadius: 12)
                        .frame(maxWidth: 280)
                        .shadow(radius: 8, y: 4)
                    VStack(spacing: 4) {
                        Text(album.title)
                            .font(.title3.weight(.semibold))
                            .multilineTextAlignment(.center)
                        NavigationLink(value: Route.artist(album.albumArtist.id)) {
                            Text(album.albumArtist.name)
                                .foregroundStyle(.tint)
                        }
                        .buttonStyle(.plain)
                        Text(Self.details(album))
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                    HStack(spacing: 12) {
                        Button {
                            player.playAlbum(album)
                        } label: {
                            Label("Abspielen", systemImage: "play.fill").frame(maxWidth: .infinity)
                        }
                        .buttonStyle(.borderedProminent)
                        Button {
                            player.playAlbum(album, shuffled: true)
                        } label: {
                            Label("Zufällig", systemImage: "shuffle").frame(maxWidth: .infinity)
                        }
                        .buttonStyle(.bordered)
                    }
                    .disabled(QueueItem.album(album).isEmpty)
                }
                .frame(maxWidth: .infinity)
                .listRowBackground(Color.clear)
                .listRowSeparator(.hidden)
            }

            ForEach(discs(album), id: \.self) { disc in
                Section(album.discCount > 1 ? "CD \(disc)" : "") {
                    ForEach(album.tracks.filter { $0.disc == disc }) { track in
                        TrackRow(album: album, track: track, playing: player.current?.trackId == track.id)
                    }
                }
            }
        }
        .listStyle(.plain)
        .toolbar {
            Menu {
                Button {
                    player.playNext(QueueItem.album(album))
                } label: {
                    Label("Als Nächstes", systemImage: "text.line.first.and.arrowtriangle.forward")
                }
                Button {
                    player.enqueue(QueueItem.album(album))
                } label: {
                    Label("Hinten anhängen", systemImage: "text.line.last.and.arrowtriangle.forward")
                }
            } label: {
                Image(systemName: "ellipsis.circle")
            }
        }
    }

    private func discs(_ album: AlbumDetail) -> [Int] {
        Array(Set(album.tracks.map(\.disc))).sorted()
    }

    private static func details(_ album: AlbumDetail) -> String {
        let seconds = Double(album.tracks.reduce(0) { $0 + $1.durationMs }) / 1000
        return [album.year.map(String.init), "\(album.tracks.count) Titel", Duration.format(seconds: seconds)]
            .compactMap { $0 }
            .joined(separator: " · ")
    }
}

private struct TrackRow: View {
    @Environment(Player.self) private var player
    let album: AlbumDetail
    let track: Track
    let playing: Bool

    private var item: QueueItem? {
        QueueItem.album(album).first { $0.trackId == track.id }
    }

    var body: some View {
        Button {
            player.playAlbum(album, from: track.id)
        } label: {
            HStack(spacing: 12) {
                Group {
                    if playing {
                        Image(systemName: "speaker.wave.2.fill").foregroundStyle(.tint)
                    } else {
                        Text("\(track.number)").foregroundStyle(.secondary)
                    }
                }
                .font(.subheadline.monospacedDigit())
                .frame(width: 28, alignment: .trailing)
                VStack(alignment: .leading, spacing: 2) {
                    Text(track.title)
                        .foregroundStyle(playing ? AnyShapeStyle(.tint) : AnyShapeStyle(.primary))
                        .lineLimit(1)
                    if let extra = subtitle {
                        Text(extra)
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            .lineLimit(1)
                    }
                }
                Spacer()
                Text(Duration.format(seconds: Double(track.durationMs) / 1000))
                    .font(.subheadline.monospacedDigit())
                    .foregroundStyle(.secondary)
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .disabled(item == nil)
        .opacity(item == nil ? 0.4 : 1)
        .swipeActions(edge: .leading) {
            if let item {
                Button {
                    player.playNext([item])
                } label: {
                    Label("Als Nächstes", systemImage: "text.line.first.and.arrowtriangle.forward")
                }
                .tint(.indigo)
            }
        }
        .swipeActions(edge: .trailing) {
            if let item {
                Button {
                    player.enqueue([item])
                } label: {
                    Label("Anhängen", systemImage: "text.line.last.and.arrowtriangle.forward")
                }
                .tint(.orange)
            }
        }
        .contextMenu {
            if let item {
                Button("Als Nächstes", systemImage: "text.line.first.and.arrowtriangle.forward") { player.playNext([item]) }
                Button("Hinten anhängen", systemImage: "text.line.last.and.arrowtriangle.forward") { player.enqueue([item]) }
            }
        }
    }

    /// Gäste und Analysewerte (BPM, Tonart), die das Album nicht schon sagt.
    private var subtitle: String? {
        var parts: [String] = []
        let artists = track.artists.joined(separator: ", ")
        if !artists.isEmpty && artists != album.albumArtist.name {
            parts.append(artists)
        }
        if let bpm = track.bpm {
            parts.append("\(Int(bpm.rounded())) BPM")
        }
        if let key = track.initialKey {
            parts.append(key)
        }
        return parts.isEmpty ? nil : parts.joined(separator: " · ")
    }
}

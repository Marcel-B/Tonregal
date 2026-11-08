// SPDX-License-Identifier: AGPL-3.0-or-later
import SwiftUI
import TonregalKit

/// Ein Cover bei einem Album, sonst ein Mosaik aus bis zu vier, wie `PlaylistCover.vue` im Web.
struct PlaylistCover: View {
    @Environment(AppModel.self) private var model
    let albumIds: [Int]
    var cornerRadius: CGFloat = 8

    var body: some View {
        Group {
            if albumIds.count == 1 {
                CoverImage(url: model.client?.coverURL(album: albumIds[0]), cornerRadius: 0)
            } else if albumIds.isEmpty {
                Color(uiColor: .secondarySystemBackground)
                    .aspectRatio(1, contentMode: .fit)
                    .overlay {
                        Image(systemName: "music.note.list")
                            .font(.title)
                            .foregroundStyle(.secondary)
                    }
            } else {
                let ids = Array(albumIds.prefix(4))
                Grid(horizontalSpacing: 0, verticalSpacing: 0) {
                    GridRow {
                        tile(ids[0])
                        tile(ids[1])
                    }
                    GridRow {
                        tile(ids.count > 2 ? ids[2] : ids[1])
                        tile(ids.count > 3 ? ids[3] : ids[0])
                    }
                }
                .aspectRatio(1, contentMode: .fit)
            }
        }
        .clipShape(RoundedRectangle(cornerRadius: cornerRadius))
        .accessibilityHidden(true)
    }

    private func tile(_ id: Int) -> some View {
        CoverImage(url: model.client?.coverURL(album: id), cornerRadius: 0)
    }
}

/// Die eigenen Playlists; anlegen über „+“, füllen über „Zur Playlist …“ an Alben, Titeln, im Player und in der Warteschlange.
struct PlaylistsView: View {
    @Environment(AppModel.self) private var model
    @State private var error: String?
    @State private var naming = false
    @State private var name = ""

    var body: some View {
        List(model.playlists) { playlist in
            NavigationLink(value: Route.playlist(playlist.id)) {
                HStack(spacing: 12) {
                    PlaylistCover(albumIds: playlist.albumIds, cornerRadius: 6)
                        .frame(width: 56, height: 56)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(playlist.name).lineLimit(1)
                        Text(Self.summary(playlist))
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                }
            }
        }
        .listStyle(.plain)
        .navigationTitle("Playlists")
        .toolbar {
            Button {
                name = ""
                naming = true
            } label: {
                Label("Neue Playlist", systemImage: "plus")
            }
        }
        .alert("Neue Playlist", isPresented: $naming) {
            TextField("Name", text: $name)
            Button("Abbrechen", role: .cancel) {}
            Button("Anlegen") { create() }
                .disabled(name.trimmingCharacters(in: .whitespaces).isEmpty)
        }
        .alert("Nicht geklappt", isPresented: Binding(get: { error != nil }, set: { if !$0 { error = nil } })) {
            Button("OK", role: .cancel) {}
        } message: {
            Text(error ?? "")
        }
        .refreshable { await load() }
        .task {
            if !model.playlistsLoaded {
                await load()
            }
        }
        .overlay {
            if model.playlists.isEmpty && model.playlistsLoaded {
                ContentUnavailableView(
                    "Noch keine Playlists", systemImage: "music.note.list",
                    description: Text("Leg mit + eine an oder wähle „Zur Playlist …“ an einem Album oder Titel."))
            }
        }
    }

    private func load() async {
        do {
            try await model.loadPlaylists()
        } catch is CancellationError {
        } catch {
            self.error = error.localizedDescription
        }
    }

    private func create() {
        let wanted = name
        Task {
            do {
                _ = try await model.createPlaylist(name: wanted)
            } catch {
                self.error = error.localizedDescription
            }
        }
    }

    static func summary(_ playlist: PlaylistSummary) -> String {
        let count = playlist.trackCount == 1 ? "1 Titel" : "\(playlist.trackCount) Titel"
        guard playlist.trackCount > 0 else { return count }
        return "\(count) · \(Duration.format(seconds: Double(playlist.durationMs) / 1000))"
    }
}

struct PlaylistDetailView: View {
    @Environment(AppModel.self) private var model
    @Environment(Player.self) private var player
    @Environment(\.dismiss) private var dismiss
    let id: Int
    @State private var detail: PlaylistDetail?
    @State private var error: String?
    @State private var problem: String?
    @State private var renaming = false
    @State private var name = ""
    @State private var confirmingDelete = false

    var body: some View {
        Group {
            if let detail {
                content(detail)
            } else if let error {
                ContentUnavailableView("Nicht geladen", systemImage: "exclamationmark.triangle", description: Text(error))
            } else {
                ProgressView()
            }
        }
        .navigationTitle(detail?.playlist.name ?? "")
        .navigationBarTitleDisplayMode(.inline)
        .task(id: id) { await load() }
        .alert("Umbenennen", isPresented: $renaming) {
            TextField("Name", text: $name)
            Button("Abbrechen", role: .cancel) {}
            Button("Sichern") { rename() }
        }
        .confirmationDialog("Playlist löschen?", isPresented: $confirmingDelete, titleVisibility: .visible) {
            Button("Löschen", role: .destructive) { delete() }
        } message: {
            Text("Die Titel bleiben in der Bibliothek.")
        }
        .alert("Nicht geklappt", isPresented: Binding(get: { problem != nil }, set: { if !$0 { problem = nil } })) {
            Button("OK", role: .cancel) {}
        } message: {
            Text(problem ?? "")
        }
    }

    private func content(_ detail: PlaylistDetail) -> some View {
        List {
            Section {
                VStack(spacing: 12) {
                    PlaylistCover(albumIds: detail.playlist.albumIds, cornerRadius: 12)
                        .frame(maxWidth: 240)
                        .shadow(radius: 8, y: 4)
                    VStack(spacing: 4) {
                        Text(detail.playlist.name)
                            .font(.title3.weight(.semibold))
                            .multilineTextAlignment(.center)
                        if let comment = detail.playlist.comment {
                            Text(comment)
                                .foregroundStyle(.secondary)
                                .multilineTextAlignment(.center)
                        }
                        Text(PlaylistsView.summary(detail.playlist))
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                    HStack(spacing: 12) {
                        Button {
                            player.play(detail.items)
                        } label: {
                            Label("Abspielen", systemImage: "play.fill").frame(maxWidth: .infinity)
                        }
                        .buttonStyle(.borderedProminent)
                        Button {
                            player.play(detail.items.shuffled())
                        } label: {
                            Label("Zufällig", systemImage: "shuffle").frame(maxWidth: .infinity)
                        }
                        .buttonStyle(.bordered)
                    }
                    .disabled(detail.items.isEmpty)
                }
                .frame(maxWidth: .infinity)
                .listRowBackground(Color.clear)
                .listRowSeparator(.hidden)
            }

            Section {
                ForEach(Array(detail.items.enumerated()), id: \.element.trackId) { position, item in
                    row(item, position: position, items: detail.items)
                }
                .onMove { source, destination in
                    var items = detail.items
                    items.move(fromOffsets: source, toOffset: destination)
                    save(items)
                }
                .onDelete { offsets in
                    save(detail.items.enumerated().filter { !offsets.contains($0.offset) }.map(\.element))
                }
            }
        }
        .listStyle(.plain)
        .overlay {
            if detail.items.isEmpty {
                ContentUnavailableView(
                    "Noch leer", systemImage: "music.note.list",
                    description: Text("Wähle „Zur Playlist …“ an einem Album, einem Titel oder im Player."))
            }
        }
        .toolbar {
            ToolbarItem(placement: .topBarTrailing) {
                EditButton().disabled(detail.items.isEmpty)
            }
            ToolbarItem(placement: .topBarTrailing) {
                Menu {
                    Button("Hinten anhängen", systemImage: "text.line.last.and.arrowtriangle.forward") { player.enqueue(detail.items) }
                        .disabled(detail.items.isEmpty)
                    Button("Umbenennen", systemImage: "pencil") {
                        name = detail.playlist.name
                        renaming = true
                    }
                    Button("Löschen", systemImage: "trash", role: .destructive) { confirmingDelete = true }
                } label: {
                    Image(systemName: "ellipsis.circle")
                }
            }
        }
    }

    private func row(_ item: QueueItem, position: Int, items: [QueueItem]) -> some View {
        let playing = player.current?.trackId == item.trackId
        return Button {
            player.play(items, at: position)
        } label: {
            HStack(spacing: 12) {
                CoverImage(url: model.client?.coverURL(album: item.albumId), cornerRadius: 4)
                    .frame(width: 40, height: 40)
                VStack(alignment: .leading, spacing: 2) {
                    Text(item.title)
                        .foregroundStyle(playing ? AnyShapeStyle(.tint) : AnyShapeStyle(.primary))
                        .lineLimit(1)
                    Text("\(item.artist) – \(item.album)")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                }
                Spacer()
                if playing {
                    Image(systemName: player.isPlaying ? "speaker.wave.2.fill" : "speaker.fill").foregroundStyle(.tint)
                }
                Text(Duration.format(seconds: Double(item.durationMs) / 1000))
                    .font(.caption.monospacedDigit())
                    .foregroundStyle(.secondary)
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .swipeActions(edge: .leading) {
            Button {
                player.playNext([item])
            } label: {
                Label("Als Nächstes", systemImage: "text.line.first.and.arrowtriangle.forward")
            }
            .tint(.indigo)
        }
        .contextMenu {
            Button("Als Nächstes", systemImage: "text.line.first.and.arrowtriangle.forward") { player.playNext([item]) }
            Button("Hinten anhängen", systemImage: "text.line.last.and.arrowtriangle.forward") { player.enqueue([item]) }
            Button("Aus der Playlist entfernen", systemImage: "minus.circle", role: .destructive) { remove(item) }
        }
    }

    private func load() async {
        guard let client = model.client else { return }
        do {
            let loaded = try await client.playlist(id)
            detail = loaded
            model.put(loaded.playlist)
            error = nil
        } catch is CancellationError {
        } catch {
            self.error = error.localizedDescription
        }
    }

    /// Sofort zeigen, dann sichern; sagt der Server nein, gilt wieder sein Stand.
    private func save(_ items: [QueueItem]) {
        guard let client = model.client, let before = detail else { return }
        detail = PlaylistDetail(playlist: before.playlist, items: items)
        Task {
            do {
                let saved = try await client.setPlaylistTracks(id, tracks: items.map(\.trackId))
                detail = saved
                model.put(saved.playlist)
            } catch {
                detail = before
                problem = error.localizedDescription
            }
        }
    }

    private func remove(_ item: QueueItem) {
        guard let detail else { return }
        save(detail.items.filter { $0.trackId != item.trackId })
    }

    private func rename() {
        guard let client = model.client, let current = detail else { return }
        let wanted = name
        Task {
            do {
                let saved = try await client.updatePlaylist(id, name: wanted, comment: current.playlist.comment)
                detail = PlaylistDetail(playlist: saved, items: detail?.items ?? current.items)
                model.put(saved)
            } catch {
                problem = error.localizedDescription
            }
        }
    }

    private func delete() {
        guard let client = model.client else { return }
        Task {
            do {
                try await client.deletePlaylist(id)
                model.forgetPlaylist(id)
                dismiss()
            } catch {
                problem = error.localizedDescription
            }
        }
    }
}

/// Welche Titel in eine Playlist sollen; für `.sheet(item:)`.
struct TrackSelection: Identifiable {
    let id = UUID()
    let trackIds: [Int]

    init(_ trackIds: [Int]) {
        // Doppelte zählen einmal, die Reihenfolge bleibt.
        var seen = Set<Int>()
        self.trackIds = trackIds.filter { seen.insert($0).inserted }
    }
}

extension View {
    /// Zeigt die Auswahl „Zur Playlist“, sobald `selection` gesetzt ist.
    func playlistPicker(_ selection: Binding<TrackSelection?>) -> some View {
        sheet(item: selection) { selection in
            AddToPlaylistSheet(trackIds: selection.trackIds)
                .presentationDetents([.medium, .large])
        }
    }
}

/// Eine Playlist wählen oder gleich eine neue anlegen; sagt kurz, was passiert ist, und schließt sich.
struct AddToPlaylistSheet: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    let trackIds: [Int]
    @State private var name = ""
    @State private var busy = false
    @State private var notice: String?
    @State private var error: String?

    var body: some View {
        NavigationStack {
            List {
                Section {
                    HStack {
                        TextField("Neue Playlist", text: $name)
                            .submitLabel(.done)
                            .onSubmit(createAndAdd)
                        Button("Anlegen", action: createAndAdd)
                            .disabled(name.trimmingCharacters(in: .whitespaces).isEmpty || busy)
                    }
                }
                Section {
                    ForEach(model.playlists) { playlist in
                        Button {
                            add(to: playlist)
                        } label: {
                            HStack(spacing: 12) {
                                PlaylistCover(albumIds: playlist.albumIds, cornerRadius: 4)
                                    .frame(width: 40, height: 40)
                                VStack(alignment: .leading, spacing: 2) {
                                    Text(playlist.name).foregroundStyle(.primary)
                                    Text(PlaylistsView.summary(playlist))
                                        .font(.caption)
                                        .foregroundStyle(.secondary)
                                }
                            }
                        }
                        .disabled(busy)
                    }
                }
                if let notice {
                    Section {
                        Label(notice, systemImage: "checkmark.circle.fill").foregroundStyle(.tint)
                    }
                }
                if let error {
                    Section {
                        Label(error, systemImage: "exclamationmark.triangle").foregroundStyle(.red)
                    }
                }
            }
            .navigationTitle(trackIds.count == 1 ? "Zur Playlist" : "\(trackIds.count) Titel zur Playlist")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Abbrechen") { dismiss() }
                }
            }
            .task {
                guard !model.playlistsLoaded else { return }
                do {
                    try await model.loadPlaylists()
                } catch is CancellationError {
                } catch {
                    self.error = error.localizedDescription
                }
            }
        }
    }

    private func add(to playlist: PlaylistSummary) {
        run {
            let result = try await model.add(tracks: trackIds, to: playlist.id)
            return Self.notice(result, name: playlist.name)
        }
    }

    private func createAndAdd() {
        let wanted = name
        guard !wanted.trimmingCharacters(in: .whitespaces).isEmpty, !busy else { return }
        run {
            let created = try await model.createPlaylist(name: wanted)
            let result = try await model.add(tracks: trackIds, to: created.id)
            return Self.notice(result, name: created.name)
        }
    }

    private func run(_ action: @escaping @MainActor () async throws -> String) {
        busy = true
        error = nil
        Task {
            defer { busy = false }
            do {
                notice = try await action()
                try? await Task.sleep(for: .milliseconds(900))
                dismiss()
            } catch {
                self.error = error.localizedDescription
            }
        }
    }

    private static func notice(_ result: PlaylistAddResult, name: String) -> String {
        guard result.added > 0 else { return "Stand schon in „\(name)“." }
        let count = result.added == 1 ? "1 Titel" : "\(result.added) Titel"
        return result.skipped > 0
            ? "\(count) zu „\(name)“ hinzugefügt, \(result.skipped) waren schon darin."
            : "\(count) zu „\(name)“ hinzugefügt."
    }
}

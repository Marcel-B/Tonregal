// SPDX-License-Identifier: AGPL-3.0-or-later
import Foundation
import Observation
import TonregalKit

/// Server-Adresse und die Listen der Bibliothek. Die Adresse liegt in den UserDefaults; Tonregal hat keine Anmeldung.
@MainActor
@Observable
final class AppModel {
    private static let serverKey = "tonregal.server"

    private(set) var server: URL?
    private(set) var albums: [AlbumListItem] = []
    private(set) var artists: [ArtistSummary] = []
    private(set) var libraryError: String?
    /// Die eigenen Playlists, nach Namen; jede Änderung legt die Antwort des Servers hier ab.
    private(set) var playlists: [PlaylistSummary] = []
    private(set) var playlistsLoaded = false
    let player = Player()

    var client: TonregalClient? { server.map { TonregalClient(server: $0) } }

    init() {
        server = UserDefaults.standard.string(forKey: Self.serverKey).flatMap(URL.init(string:))
        player.client = client
    }

    /// Prüft die Adresse mit `GET /api/health`, bevor sie gespeichert wird.
    func connect(_ text: String) async throws -> Health {
        guard let url = ServerAddress.parse(text) else {
            throw TonregalError("Das ist keine Adresse. Beispiel: \(ServerAddress.defaultServer)")
        }
        let health = try await TonregalClient(server: url).health()
        if url != server {
            player.stop()
            albums = []
            artists = []
            playlists = []
            playlistsLoaded = false
        }
        server = url
        UserDefaults.standard.set(url.absoluteString, forKey: Self.serverKey)
        player.client = client
        libraryError = nil
        return health
    }

    func disconnect() {
        player.stop()
        server = nil
        albums = []
        artists = []
        playlists = []
        playlistsLoaded = false
        libraryError = nil
        UserDefaults.standard.removeObject(forKey: Self.serverKey)
        player.client = nil
    }

    func loadAlbums() async {
        guard let client else { return }
        do {
            albums = try await client.albums()
            libraryError = nil
        } catch is CancellationError {
        } catch {
            libraryError = error.localizedDescription
        }
    }

    func loadArtists() async {
        guard let client else { return }
        do {
            artists = try await client.artists()
            libraryError = nil
        } catch is CancellationError {
        } catch {
            libraryError = error.localizedDescription
        }
    }

    // MARK: Eigene Playlists

    func loadPlaylists() async throws {
        guard let client else { return }
        playlists = Self.sorted(try await client.playlists())
        playlistsLoaded = true
    }

    func createPlaylist(name: String) async throws -> PlaylistSummary {
        guard let client else { throw TonregalError("Kein Server verbunden.") }
        let created = try await client.createPlaylist(name: name)
        put(created)
        return created
    }

    func add(tracks: [Int], to playlist: Int) async throws -> PlaylistAddResult {
        guard let client else { throw TonregalError("Kein Server verbunden.") }
        let result = try await client.addToPlaylist(playlist, tracks: tracks)
        put(result.playlist)
        return result
    }

    func put(_ playlist: PlaylistSummary) {
        playlists = Self.sorted(playlists.filter { $0.id != playlist.id } + [playlist])
    }

    func forgetPlaylist(_ id: Int) {
        playlists.removeAll { $0.id == id }
    }

    private static func sorted(_ items: [PlaylistSummary]) -> [PlaylistSummary] {
        items.sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
    }
}

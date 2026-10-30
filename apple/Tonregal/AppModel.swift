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
    /// Wann die Listen zuletzt geladen wurden; `refreshLibrary` lädt sie beim Zurückkehren in die App nur, wenn das etwas her ist.
    private var libraryLoaded: Date?
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
            libraryLoaded = nil
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
        libraryLoaded = nil
        libraryError = nil
        UserDefaults.standard.removeObject(forKey: Self.serverKey)
        player.client = nil
    }

    /// Lädt Alben und Interpreten neu, wenn die App wieder nach vorn kommt. iOS hält sie oft tagelang im Speicher;
    /// ohne das blieben neue Titel aus der Bibliothek unsichtbar, bis man die Liste von Hand nach unten zieht.
    func refreshLibrary(olderThan age: TimeInterval = 60) async {
        // Noch nie geladen: das übernehmen die Listen selbst, sobald sie erscheinen.
        guard client != nil, let libraryLoaded, Date().timeIntervalSince(libraryLoaded) >= age else { return }
        async let albumList: Void = loadAlbums()
        async let artistList: Void = loadArtists()
        _ = await (albumList, artistList)
    }

    func loadAlbums() async {
        guard let client else { return }
        do {
            albums = try await client.albums()
            libraryLoaded = .now
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
            libraryLoaded = .now
            libraryError = nil
        } catch is CancellationError {
        } catch {
            libraryError = error.localizedDescription
        }
    }
}

// SPDX-License-Identifier: AGPL-3.0-or-later
import Foundation

public struct TonregalError: LocalizedError, Sendable, Equatable {
    public let message: String
    public init(_ message: String) { self.message = message }
    public var errorDescription: String? { message }
}

/// Die paar Aufrufe, die der Player braucht. Tonregal hat keine Anmeldung; wer im Tailnet ist, kommt heran.
public struct TonregalClient: Sendable {
    public let server: URL
    private let session: URLSession

    public init(server: URL, session: URLSession = .shared) {
        self.server = server
        self.session = session
    }

    public func health() async throws -> Health { try await get("api/health") }
    public func artists() async throws -> [ArtistSummary] { try await get("api/library/artists") }
    public func artist(_ id: Int) async throws -> ArtistDetail { try await get("api/library/artists/\(id)") }
    public func albums() async throws -> [AlbumListItem] { try await get("api/library/albums") }
    public func album(_ id: Int) async throws -> AlbumDetail { try await get("api/library/albums/\(id)") }

    /// Interpreten, Alben und Titel; jedes Wort muss vorkommen, Akzente und Großschreibung zählen nicht.
    public func search(_ query: String) async throws -> LibrarySearchResult {
        try await get("api/library/search?q=" + Self.queryValue(query))
    }

    /// Prozentkodiert für einen Query-Wert: `urlQueryAllowed` ließe `&`, `+` und `=` stehen, `alphanumerics` auch „é“.
    static func queryValue(_ value: String) -> String {
        value.addingPercentEncoding(withAllowedCharacters: unreserved) ?? ""
    }

    private static let unreserved = CharacterSet(charactersIn: "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~")

    /// Songtext aus .lrc, Tags oder lrclib.net; ohne Treffer `source == "None"` und keine Zeilen.
    /// lrclib.net kann beim ersten Abruf eines Songtexts etwas brauchen, daher mehr Geduld als sonst.
    public func lyrics(track id: Int) async throws -> SongLyrics {
        try await send("api/player/tracks/\(id)/lyrics", method: "GET", timeout: 40)
    }

    /// Gespeichertes übergehen und neu bei lrclib.net suchen.
    public func refreshLyrics(track id: Int) async throws -> SongLyrics {
        try await send("api/player/tracks/\(id)/lyrics/refresh", method: "POST", timeout: 40)
    }

    public func coverURL(album id: Int) -> URL { url("api/library/albums/\(id)/cover") }

    /// Über Navidrome (Transcoding, Play-Counts), sonst die Datei selbst; Range-Anfragen gehen durch.
    public func streamURL(file id: Int) -> URL { url("api/player/files/\(id)/stream") }

    /// `submission: false` meldet „läuft gerade“, `true` zählt die Wiedergabe. 202 heißt: Navidrome kennt die Datei
    /// noch nicht, das ist kein Fehler.
    public func scrobble(file id: Int, submission: Bool) async throws {
        var request = URLRequest(url: url("api/player/files/\(id)/scrobble?submission=\(submission)"))
        request.httpMethod = "POST"
        let (data, response) = try await session.data(for: request)
        try check(data, response)
    }

    /// Sterne je Datei-ID, wie Tonregal sie hält; unbewertete fehlen.
    public func ratings(files ids: [Int]) async throws -> [Int: Int] {
        guard !ids.isEmpty else { return [:] }
        let answer: [String: Int] = try await get("api/player/ratings?" + ids.map { "files=\($0)" }.joined(separator: "&"))
        return Dictionary(uniqueKeysWithValues: answer.compactMap { key, value in Int(key).map { ($0, value) } })
    }

    /// 0 nimmt die Bewertung zurück. Gilt sofort, auch für Dateien, die Navidrome noch nicht gescannt hat.
    public func rate(file id: Int, rating: Int) async throws {
        var request = URLRequest(url: url("api/player/files/\(id)/rating"))
        request.httpMethod = "PUT"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONEncoder().encode(["rating": rating])
        let data: Data, response: URLResponse
        do {
            (data, response) = try await session.data(for: request)
        } catch let error as URLError {
            throw TonregalError(Self.describe(error))
        }
        try check(data, response)
    }

    /// Zufällige Warteschlange: alle Stücke eines Interpreten (auch Gastauftritte) oder ohne `artist` die ganze Bibliothek.
    public func shuffle(artist: Int? = nil) async throws -> [QueueItem] {
        try await get(artist.map { "api/player/shuffle?artist=\($0)" } ?? "api/player/shuffle")
    }

    // MARK: Eigene Playlists

    public func playlists() async throws -> [PlaylistSummary] { try await get("api/playlists") }
    public func playlist(_ id: Int) async throws -> PlaylistDetail { try await get("api/playlists/\(id)") }

    public func createPlaylist(name: String) async throws -> PlaylistSummary {
        try await send("api/playlists", method: "POST", body: PlaylistBody(name: name, comment: nil))
    }

    public func updatePlaylist(_ id: Int, name: String, comment: String?) async throws -> PlaylistSummary {
        try await send("api/playlists/\(id)", method: "PUT", body: PlaylistBody(name: name, comment: comment))
    }

    public func deletePlaylist(_ id: Int) async throws {
        try await sendWithoutAnswer("api/playlists/\(id)", method: "DELETE")
    }

    /// Hinten anhängen; was schon darin steht, zählt als übersprungen.
    public func addToPlaylist(_ id: Int, tracks: [Int]) async throws -> PlaylistAddResult {
        try await send("api/playlists/\(id)/tracks", method: "POST", body: TrackList(trackIds: tracks))
    }

    /// Alle Stücke in neuer Reihenfolge; fehlende fliegen heraus.
    public func setPlaylistTracks(_ id: Int, tracks: [Int]) async throws -> PlaylistDetail {
        try await send("api/playlists/\(id)/tracks", method: "PUT", body: TrackList(trackIds: tracks))
    }

    public func removeFromPlaylist(_ id: Int, track: Int) async throws {
        try await sendWithoutAnswer("api/playlists/\(id)/tracks/\(track)", method: "DELETE")
    }

    private struct PlaylistBody: Encodable {
        let name: String
        let comment: String?
    }

    private struct TrackList: Encodable {
        let trackIds: [Int]
    }

    private func url(_ path: String) -> URL {
        URL(string: path, relativeTo: server)!.absoluteURL
    }

    private func get<T: Decodable>(_ path: String) async throws -> T { try await send(path, method: "GET") }

    private func send<T: Decodable>(_ path: String, method: String, timeout: TimeInterval = 20) async throws -> T {
        try await send(path, method: method, body: Optional<String>.none, timeout: timeout)
    }

    private func send<T: Decodable, Body: Encodable>(_ path: String, method: String, body: Body?, timeout: TimeInterval = 20) async throws -> T {
        let data = try await exchange(path, method: method, body: body, timeout: timeout)
        do {
            return try JSONDecoder().decode(T.self, from: data)
        } catch {
            throw TonregalError("Die Antwort des Servers passt nicht zur App (\(path)). Ist Tonregal aktuell?")
        }
    }

    private func sendWithoutAnswer(_ path: String, method: String) async throws {
        _ = try await exchange(path, method: method, body: Optional<String>.none, timeout: 20)
    }

    private func exchange<Body: Encodable>(_ path: String, method: String, body: Body?, timeout: TimeInterval) async throws -> Data {
        var request = URLRequest(url: url(path))
        request.httpMethod = method
        request.timeoutInterval = timeout
        if let body {
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            request.httpBody = try JSONEncoder().encode(body)
        }
        let data: Data, response: URLResponse
        do {
            (data, response) = try await session.data(for: request)
        } catch let error as URLError {
            throw TonregalError(Self.describe(error))
        }
        try check(data, response)
        return data
    }

    private func check(_ data: Data, _ response: URLResponse) throws {
        guard let http = response as? HTTPURLResponse else { return }
        guard (200..<300).contains(http.statusCode) else {
            // ASP.NET antwortet Fehler als Problem Details; deren `detail` ist die lesbare Meldung.
            throw TonregalError(Self.message(problem: data) ?? "Der Server antwortet mit \(http.statusCode).")
        }
    }

    static func describe(_ error: URLError) -> String {
        switch error.code {
        case .cannotFindHost, .dnsLookupFailed:
            "Server nicht gefunden. Ist Tailscale auf dem iPhone verbunden?"
        case .cannotConnectToHost, .timedOut, .networkConnectionLost, .notConnectedToInternet:
            "Tonregal antwortet nicht. Ist Tailscale verbunden und läuft der Dienst auf oi-o-mat?"
        case .appTransportSecurityRequiresSecureConnection:
            "iOS lässt die Verbindung ohne HTTPS nicht zu."
        default:
            error.localizedDescription
        }
    }

    /// Validierungsfehler tragen ihre Meldung je Feld in `errors`, der Titel sagt dort nur „One or more validation errors“.
    static func message(problem data: Data) -> String? {
        guard let problem = try? JSONDecoder().decode(Problem.self, from: data) else { return nil }
        return problem.detail ?? problem.errors?.values.lazy.compactMap(\.first).first ?? problem.title
    }

    private struct Problem: Decodable {
        let title: String?
        let detail: String?
        let errors: [String: [String]]?
    }
}

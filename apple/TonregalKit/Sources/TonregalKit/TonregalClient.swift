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

    /// Sterne je Datei-ID aus Navidrome; unbewertete fehlen.
    public func ratings(files ids: [Int]) async throws -> [Int: Int] {
        guard !ids.isEmpty else { return [:] }
        let answer: [String: Int] = try await get("api/player/ratings?" + ids.map { "files=\($0)" }.joined(separator: "&"))
        return Dictionary(uniqueKeysWithValues: answer.compactMap { key, value in Int(key).map { ($0, value) } })
    }

    /// 0 nimmt die Bewertung zurück. Navidrome muss die Datei schon kennen (Scan alle 15 Minuten).
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

    private func url(_ path: String) -> URL {
        URL(string: path, relativeTo: server)!.absoluteURL
    }

    private func get<T: Decodable>(_ path: String) async throws -> T { try await send(path, method: "GET") }

    private func send<T: Decodable>(_ path: String, method: String, timeout: TimeInterval = 20) async throws -> T {
        var request = URLRequest(url: url(path))
        request.httpMethod = method
        request.timeoutInterval = timeout
        let data: Data, response: URLResponse
        do {
            (data, response) = try await session.data(for: request)
        } catch let error as URLError {
            throw TonregalError(Self.describe(error))
        }
        try check(data, response)
        do {
            return try JSONDecoder().decode(T.self, from: data)
        } catch {
            throw TonregalError("Die Antwort des Servers passt nicht zur App (\(path)). Ist Tonregal aktuell?")
        }
    }

    private func check(_ data: Data, _ response: URLResponse) throws {
        guard let http = response as? HTTPURLResponse else { return }
        guard (200..<300).contains(http.statusCode) else {
            // ASP.NET antwortet Fehler als Problem Details; deren `detail` ist die lesbare Meldung.
            let problem = try? JSONDecoder().decode(Problem.self, from: data)
            throw TonregalError(problem?.detail ?? problem?.title ?? "Der Server antwortet mit \(http.statusCode).")
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

    private struct Problem: Decodable {
        let title: String?
        let detail: String?
    }
}

// SPDX-License-Identifier: AGPL-3.0-or-later
import Foundation
import XCTest
@testable import TonregalKit

final class ModelTests: XCTestCase {
    /// So antwortet `GET /api/library/albums/{id}` (camelCase, Enums als PascalCase-Strings).
    func testAlbumDetailDecodes() throws {
        let json = """
        {"id":7,"title":"Auberge","year":1991,"kind":"Album","discCount":1,
         "albumArtist":{"id":3,"name":"Chris Rea"},
         "tracks":[
          {"id":11,"disc":1,"number":1,"title":"Auberge","artists":[],"durationMs":434000,"bpm":96.5,"initialKey":"Am","genre":null,
           "files":[{"id":21,"format":"Flac","bitrate":900,"sampleRate":44100,"sizeBytes":40000000,"path":"Chris Rea/Auberge/01.flac"}]},
          {"id":12,"disc":1,"number":2,"title":"Gone Fishing","artists":["Chris Rea","Gast"],"durationMs":300000,"bpm":null,"initialKey":null,"genre":"Rock","files":[]}
         ],"canFetchCover":false,"releaseCount":1}
        """
        let album = try JSONDecoder().decode(AlbumDetail.self, from: Data(json.utf8))
        XCTAssertEqual(album.tracks.count, 2)
        XCTAssertEqual(album.tracks[0].files[0].format, "Flac")

        let queue = QueueItem.album(album)
        XCTAssertEqual(queue.map(\.fileId), [21], "Tracks ohne Datei fallen weg")
        XCTAssertEqual(queue[0].artist, "Chris Rea", "ohne Track-Interpreten der Albuminterpret")
    }

    func testAlbumListDecodes() throws {
        let json = #"[{"id":2,"title":"B","year":null,"kind":"Compilation","trackCount":12,"albumArtist":{"id":1,"name":"Various Artists"}}]"#
        let albums = try JSONDecoder().decode([AlbumListItem].self, from: Data(json.utf8))
        XCTAssertEqual(albums.first?.albumArtist.name, "Various Artists")
        XCTAssertNil(albums.first?.year)
    }

    func testDuration() {
        XCTAssertEqual(Duration.format(seconds: 187.9), "3:07")
        XCTAssertEqual(Duration.format(seconds: 3723), "1:02:03")
        XCTAssertEqual(Duration.format(seconds: .nan), "0:00")
    }
}

final class ServerAddressTests: XCTestCase {
    func testBareHostGetsHttpAndPort() {
        XCTAssertEqual(ServerAddress.parse("oi-o-mat")?.absoluteString, "http://oi-o-mat:5100")
        XCTAssertEqual(ServerAddress.parse(" 100.64.1.2 ")?.absoluteString, "http://100.64.1.2:5100")
    }

    func testKeepsGivenSchemeAndPort() {
        XCTAssertEqual(ServerAddress.parse("100.64.1.2:8080")?.absoluteString, "http://100.64.1.2:8080")
        XCTAssertEqual(ServerAddress.parse("https://oi-o-mat.tail1234.ts.net")?.absoluteString, "https://oi-o-mat.tail1234.ts.net")
    }

    func testDropsPathOfAWebPage() {
        XCTAssertEqual(ServerAddress.parse("http://oi-o-mat:5100/albums/7?x=1")?.absoluteString, "http://oi-o-mat:5100")
    }

    func testRejectsNonsense() {
        XCTAssertNil(ServerAddress.parse(""))
        XCTAssertNil(ServerAddress.parse("ftp://oi-o-mat"))
    }

    func testClientBuildsUrlsBelowTheRoot() {
        let client = TonregalClient(server: ServerAddress.parse("oi-o-mat")!)
        XCTAssertEqual(client.streamURL(file: 5).absoluteString, "http://oi-o-mat:5100/api/player/files/5/stream")
        XCTAssertEqual(client.coverURL(album: 9).absoluteString, "http://oi-o-mat:5100/api/library/albums/9/cover")
    }
}

final class PlayQueueTests: XCTestCase {
    private func items(_ ids: [Int]) -> [QueueItem] {
        ids.map { QueueItem(fileId: $0, trackId: $0, title: "T\($0)", artist: "A", album: "Al", albumId: 1, durationMs: 1000) }
    }

    func testPlayAndStepThrough() {
        var queue = PlayQueue()
        queue.play(items([1, 2, 3]), at: 1)
        XCTAssertEqual(queue.current?.fileId, 2)
        XCTAssertTrue(queue.next())
        XCTAssertFalse(queue.next(), "am Ende bleibt es beim letzten")
        XCTAssertEqual(queue.current?.fileId, 3)
        XCTAssertTrue(queue.previous())
        XCTAssertEqual(queue.current?.fileId, 2)
    }

    func testEnqueueOnEmptyStarts() {
        var queue = PlayQueue()
        XCTAssertTrue(queue.enqueue(items([1, 2])))
        XCTAssertEqual(queue.current?.fileId, 1)
        XCTAssertFalse(queue.enqueue(items([3])))
        XCTAssertEqual(queue.items.map(\.fileId), [1, 2, 3])
    }

    func testPlayNextInsertsAfterCurrent() {
        var queue = PlayQueue()
        queue.play(items([1, 2, 3]), at: 0)
        queue.playNext(items([9]))
        XCTAssertEqual(queue.items.map(\.fileId), [1, 9, 2, 3])
        XCTAssertEqual(queue.current?.fileId, 1)
    }

    func testRemoveKeepsCurrent() {
        var queue = PlayQueue()
        queue.play(items([1, 2, 3, 4]), at: 2)
        XCTAssertFalse(queue.remove(at: [0]))
        XCTAssertEqual(queue.current?.fileId, 3)
        XCTAssertTrue(queue.remove(at: [1]), "das laufende Stück entfernt")
        XCTAssertEqual(queue.current?.fileId, 4)
        XCTAssertTrue(queue.remove(at: [1]))
        XCTAssertEqual(queue.current?.fileId, 2, "am Ende rückt das vorige nach")
        queue.remove(at: [0])
        XCTAssertNil(queue.current)
    }

    func testMoveFollowsTheCurrentEvenWithDuplicates() {
        var queue = PlayQueue()
        queue.play(items([1, 2, 1, 3]), at: 2)
        queue.move(from: [2], to: 0)
        XCTAssertEqual(queue.items.map(\.fileId), [1, 1, 2, 3])
        XCTAssertEqual(queue.index, 0)
        queue.move(from: [3], to: 0)
        XCTAssertEqual(queue.items.map(\.fileId), [3, 1, 1, 2])
        XCTAssertEqual(queue.index, 1)
        queue.move(from: [1], to: 4)
        XCTAssertEqual(queue.items.map(\.fileId), [3, 1, 2, 1])
        XCTAssertEqual(queue.index, 3)
    }

    func testShuffleKeepsPlayedAndCurrent() {
        var queue = PlayQueue()
        queue.play(items(Array(1...20)), at: 4)
        queue.shuffleUpcoming()
        XCTAssertEqual(queue.items.prefix(5).map(\.fileId), [1, 2, 3, 4, 5])
        XCTAssertEqual(Set(queue.items.map(\.fileId)), Set(1...20))
    }

    func testRoundTripsThroughJson() throws {
        var queue = PlayQueue()
        queue.play(items([1, 2]), at: 1)
        let decoded = try JSONDecoder().decode(PlayQueue.self, from: JSONEncoder().encode(queue))
        XCTAssertEqual(decoded, queue)
    }

    func testScrobbleRule() {
        XCTAssertFalse(Scrobble.due(elapsed: 89, duration: 180))
        XCTAssertTrue(Scrobble.due(elapsed: 90, duration: 180))
        XCTAssertTrue(Scrobble.due(elapsed: 240, duration: 1200), "lange Stücke nach 4 Minuten")
        XCTAssertFalse(Scrobble.due(elapsed: 10, duration: 0))
    }
}

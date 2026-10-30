// SPDX-License-Identifier: AGPL-3.0-or-later
import AVFoundation
import Foundation
import MediaPlayer
import Observation
import TonregalKit
import UIKit

/// Der eine Player, das native Gegenstück zu stores/player.ts und PlayerBar.vue. AVPlayer streamt über
/// `/api/player/files/{id}/stream` (Navidrome dahinter); Sperrbildschirm, Kontrollzentrum, AirPods und CarPlay-Tasten
/// gehen über Now Playing und die Remote Commands, Hintergrund-Audio über die Audio-Session `.playback`.
@MainActor
@Observable
final class Player {
    private static let storageKey = "tonregal.player"

    var client: TonregalClient?
    private(set) var queue = PlayQueue()
    private(set) var isPlaying = false
    private(set) var elapsed: Double = 0
    private(set) var duration: Double = 0
    /// Warum das laufende Stück nicht spielt (Server weg, Datei fehlt), sonst nil.
    private(set) var problem: String?
    /// Sterne je Datei-ID, wie Navidrome sie hält; fehlt eine, ist sie unbewertet oder noch nicht geladen.
    private(set) var ratings: [Int: Int] = [:]
    /// Warum die letzte Bewertung nicht ankam, sonst nil.
    var ratingProblem: String?

    var current: QueueItem? { queue.current }
    var repeatMode: RepeatMode { queue.repeatMode }

    @ObservationIgnored private let player = AVPlayer()
    @ObservationIgnored private var observers: [NSObjectProtocol] = []
    @ObservationIgnored private var timeObserver: Any?
    @ObservationIgnored private var statusObservation: NSKeyValueObservation?
    @ObservationIgnored private var itemObservation: NSKeyValueObservation?
    /// Ob der Nutzer spielen will; nach einem Anruf geht es nur dann weiter.
    @ObservationIgnored private var wantsToPlay = false
    @ObservationIgnored private var scrobbled = false
    @ObservationIgnored private var artwork: (albumId: Int, image: MPMediaItemArtwork)?
    @ObservationIgnored private var lastSaved = Date.distantPast

    init() {
        restore()
        timeObserver = player.addPeriodicTimeObserver(forInterval: CMTime(value: 1, timescale: 2), queue: .main) {
            [weak self] time in
            MainActor.assumeIsolated { self?.tick(time) }
        }
        let center = NotificationCenter.default
        observers.append(center.addObserver(forName: AVPlayerItem.didPlayToEndTimeNotification, object: nil, queue: .main) {
            [weak self] notification in
            let item = notification.object as? AVPlayerItem
            MainActor.assumeIsolated { self?.ended(item) }
        })
        observers.append(center.addObserver(
            forName: AVAudioSession.interruptionNotification, object: AVAudioSession.sharedInstance(), queue: .main
        ) { [weak self] notification in
            let info = notification.userInfo ?? [:]
            let type = (info[AVAudioSessionInterruptionTypeKey] as? UInt).flatMap(AVAudioSession.InterruptionType.init(rawValue:))
            let options = AVAudioSession.InterruptionOptions(rawValue: info[AVAudioSessionInterruptionOptionKey] as? UInt ?? 0)
            MainActor.assumeIsolated {
                if type == .ended && options.contains(.shouldResume) {
                    self?.resumeAfterInterruption()
                }
            }
        })
        statusObservation = player.observe(\.timeControlStatus, options: [.new]) { [weak self] player, _ in
            let playing = player.timeControlStatus != .paused
            Task { @MainActor in self?.playingChanged(playing) }
        }
        setUpRemoteCommands()
        try? AVAudioSession.sharedInstance().setCategory(.playback, mode: .default)
    }

    // MARK: Warteschlange

    func play(_ items: [QueueItem], at position: Int = 0) {
        queue.play(items, at: position)
        start()
    }

    func playAlbum(_ album: AlbumDetail, from trackId: Int? = nil, shuffled: Bool = false) {
        var items = QueueItem.album(album)
        if shuffled {
            items.shuffle()
        }
        play(items, at: trackId.flatMap { id in items.firstIndex { $0.trackId == id } } ?? 0)
    }

    func enqueue(_ items: [QueueItem]) {
        if queue.enqueue(items) {
            start()
        } else {
            save()
            updateRemoteCommands()
        }
    }

    func playNext(_ items: [QueueItem]) {
        if queue.playNext(items) {
            start()
        } else {
            save()
            updateRemoteCommands()
        }
    }

    func jump(to position: Int) {
        if queue.jump(to: position) {
            start()
        }
    }

    func remove(at offsets: IndexSet) {
        let wasPlaying = wantsToPlay
        if queue.remove(at: offsets) {
            if queue.current == nil {
                stop()
                return
            }
            start(autoplay: wasPlaying)
        }
        save()
        updateRemoteCommands()
    }

    func move(from source: IndexSet, to destination: Int) {
        queue.move(from: source, to: destination)
        save()
        updateRemoteCommands()
    }

    func shuffleUpcoming() {
        queue.shuffleUpcoming()
        save()
    }

    /// Alle Stücke eines Interpreten oder ohne `artist` die ganze Bibliothek, in zufälliger Reihenfolge vom Server.
    func shuffle(artist: Int? = nil) async throws {
        guard let client else { return }
        let items = try await client.shuffle(artist: artist)
        guard !items.isEmpty else { throw TonregalError("Keine Stücke zum Abspielen.") }
        play(items)
    }

    func cycleRepeat() {
        queue.repeatMode = queue.repeatMode.next
        save()
        updateRemoteCommands()
    }

    // MARK: Bewertung

    func rating(file id: Int) -> Int { ratings[id] ?? 0 }

    func loadRatings(files ids: [Int]) async {
        guard let client, !ids.isEmpty, let loaded = try? await client.ratings(files: ids) else { return }
        for id in ids {
            ratings[id] = loaded[id] ?? 0
        }
    }

    /// Sofort zeigen, bei einer Absage zurücknehmen; 0 nimmt die Bewertung zurück.
    func rate(file id: Int, _ stars: Int) {
        guard let client else { return }
        let before = rating(file: id)
        ratings[id] = stars
        ratingProblem = nil
        Task {
            do {
                try await client.rate(file: id, rating: stars)
            } catch {
                ratings[id] = before
                ratingProblem = error.localizedDescription
            }
        }
    }

    // MARK: Abspielen

    func toggle() {
        isPlaying ? pause() : resume()
    }

    func resume() {
        guard current != nil else { return }
        if player.currentItem == nil || problem != nil {
            // Nach dem Start der App (oder einem Fehler) ist noch nichts geladen: an der gemerkten Stelle weiter.
            start(at: elapsed)
        } else {
            wantsToPlay = true
            activateSession()
            player.play()
        }
    }

    func pause() {
        wantsToPlay = false
        player.pause()
        save()
    }

    func next() {
        if queue.next() {
            start()
        }
    }

    /// Zum Anfang des Stücks, in den ersten Sekunden zum vorigen, wie jede Musik-App.
    func previous() {
        if elapsed > 3 || !queue.previous() {
            seek(to: 0)
        } else {
            start()
        }
    }

    func seek(to seconds: Double) {
        elapsed = seconds
        player.seek(to: CMTime(seconds: seconds, preferredTimescale: 600), toleranceBefore: .zero, toleranceAfter: .zero)
        updateNowPlaying()
    }

    func stop() {
        wantsToPlay = false
        player.pause()
        player.replaceCurrentItem(with: nil)
        itemObservation = nil
        queue.clear()
        elapsed = 0
        duration = 0
        problem = nil
        MPNowPlayingInfoCenter.default().nowPlayingInfo = nil
        save()
        updateRemoteCommands()
    }

    private func start(autoplay: Bool = true, at position: Double = 0) {
        guard let item = queue.current, let client else { return }
        elapsed = position
        duration = Double(item.durationMs) / 1000
        problem = nil
        scrobbled = false
        let playerItem = AVPlayerItem(url: client.streamURL(file: item.fileId))
        itemObservation = playerItem.observe(\.status, options: [.new]) { [weak self] observed, _ in
            guard observed.status == .failed else { return }
            let message = observed.error?.localizedDescription ?? "Das Stück lässt sich nicht abspielen."
            Task { @MainActor in self?.failed(observed, message) }
        }
        player.replaceCurrentItem(with: playerItem)
        if position > 0 {
            player.seek(to: CMTime(seconds: position, preferredTimescale: 600))
        }
        wantsToPlay = autoplay
        if autoplay {
            activateSession()
            player.play()
            nowPlaying(item)
        }
        updateNowPlaying()
        updateRemoteCommands()
        loadArtwork(for: item)
        Task { await loadRatings(files: [item.fileId]) }
        save()
    }

    private func activateSession() {
        try? AVAudioSession.sharedInstance().setActive(true)
    }

    private func resumeAfterInterruption() {
        guard wantsToPlay, player.currentItem != nil else { return }
        activateSession()
        player.play()
    }

    private func failed(_ item: AVPlayerItem, _ message: String) {
        guard item === player.currentItem else { return }
        problem = message
        isPlaying = false
    }

    private func tick(_ time: CMTime) {
        guard player.currentItem != nil else { return }
        elapsed = time.seconds.isFinite ? time.seconds : 0
        if let length = player.currentItem?.duration.seconds, length.isFinite, length > 0 {
            duration = length
        }
        if !scrobbled, isPlaying, let item = current, Scrobble.due(elapsed: elapsed, duration: duration) {
            scrobbled = true
            scrobble(item, submission: true)
        }
        if Date().timeIntervalSince(lastSaved) > 5 {
            save()
        }
    }

    private func ended(_ item: AVPlayerItem?) {
        guard item === player.currentItem else { return }
        if queue.finished() {
            start()
        } else {
            wantsToPlay = false
            player.pause()
            seek(to: 0)
        }
    }

    private func playingChanged(_ playing: Bool) {
        isPlaying = playing
        updateNowPlaying()
    }

    // MARK: Navidrome

    /// „Läuft gerade“ an Navidrome, sobald ein Stück startet; gezählt wird erst nach der Hälfte (tick).
    private func nowPlaying(_ item: QueueItem) {
        scrobble(item, submission: false)
    }

    private func scrobble(_ item: QueueItem, submission: Bool) {
        guard let client else { return }
        Task { try? await client.scrobble(file: item.fileId, submission: submission) }
    }

    // MARK: Speichern

    private struct Saved: Codable {
        let queue: PlayQueue
        let position: Double
    }

    func save() {
        lastSaved = Date()
        guard let data = try? JSONEncoder().encode(Saved(queue: queue, position: elapsed)) else { return }
        UserDefaults.standard.set(data, forKey: Self.storageKey)
    }

    private func restore() {
        guard let data = UserDefaults.standard.data(forKey: Self.storageKey),
              let saved = try? JSONDecoder().decode(Saved.self, from: data)
        else { return }
        queue = saved.queue
        elapsed = saved.position
        duration = Double(saved.queue.current?.durationMs ?? 0) / 1000
    }

    // MARK: Sperrbildschirm und Tasten

    private func setUpRemoteCommands() {
        let center = MPRemoteCommandCenter.shared()
        center.playCommand.addTarget { [weak self] _ in
            Task { @MainActor in self?.resume() }
            return .success
        }
        center.pauseCommand.addTarget { [weak self] _ in
            Task { @MainActor in self?.pause() }
            return .success
        }
        center.togglePlayPauseCommand.addTarget { [weak self] _ in
            Task { @MainActor in self?.toggle() }
            return .success
        }
        center.nextTrackCommand.addTarget { [weak self] _ in
            Task { @MainActor in self?.next() }
            return .success
        }
        center.previousTrackCommand.addTarget { [weak self] _ in
            Task { @MainActor in self?.previous() }
            return .success
        }
        center.changeRepeatModeCommand.addTarget { [weak self] event in
            guard let event = event as? MPChangeRepeatModeCommandEvent else { return .commandFailed }
            let mode: RepeatMode = switch event.repeatType {
            case .off: .off
            case .one: .one
            default: .all
            }
            Task { @MainActor in
                guard let self else { return }
                self.queue.repeatMode = mode
                self.save()
                self.updateRemoteCommands()
            }
            return .success
        }
        center.changePlaybackPositionCommand.addTarget { [weak self] event in
            guard let event = event as? MPChangePlaybackPositionCommandEvent else { return .commandFailed }
            let position = event.positionTime
            Task { @MainActor in self?.seek(to: position) }
            return .success
        }
        updateRemoteCommands()
    }

    private func updateRemoteCommands() {
        let center = MPRemoteCommandCenter.shared()
        center.nextTrackCommand.isEnabled = queue.hasNext
        center.previousTrackCommand.isEnabled = current != nil
        center.changeRepeatModeCommand.currentRepeatType = switch queue.repeatMode {
        case .off: .off
        case .all: .all
        case .one: .one
        }
    }

    private func updateNowPlaying() {
        let center = MPNowPlayingInfoCenter.default()
        guard let item = current else {
            center.nowPlayingInfo = nil
            return
        }
        var info: [String: Any] = [
            MPMediaItemPropertyTitle: item.title,
            MPMediaItemPropertyArtist: item.artist,
            MPMediaItemPropertyAlbumTitle: item.album,
            MPNowPlayingInfoPropertyElapsedPlaybackTime: elapsed,
            MPNowPlayingInfoPropertyPlaybackRate: isPlaying ? 1.0 : 0.0,
            MPNowPlayingInfoPropertyMediaType: MPNowPlayingInfoMediaType.audio.rawValue,
        ]
        if duration > 0 {
            info[MPMediaItemPropertyPlaybackDuration] = duration
        }
        if let artwork, artwork.albumId == item.albumId {
            info[MPMediaItemPropertyArtwork] = artwork.image
        }
        center.nowPlayingInfo = info
    }

    private func loadArtwork(for item: QueueItem) {
        guard artwork?.albumId != item.albumId, let client else { return }
        let url = client.coverURL(album: item.albumId)
        Task {
            guard let image = await ImageCache.shared.image(for: url), current?.albumId == item.albumId else { return }
            artwork = (item.albumId, MPMediaItemArtwork(boundsSize: image.size) { _ in image })
            updateNowPlaying()
        }
    }
}

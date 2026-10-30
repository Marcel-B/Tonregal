// SPDX-License-Identifier: AGPL-3.0-or-later
import AVKit
import SwiftUI
import TonregalKit

struct PlayerBar: View {
    @Environment(AppModel.self) private var model
    @Environment(Player.self) private var player
    let open: () -> Void

    var body: some View {
        if let item = player.current {
            VStack(spacing: 0) {
                ProgressView(value: player.duration > 0 ? min(player.elapsed / player.duration, 1) : 0)
                    .progressViewStyle(.linear)
                    .tint(.accentColor)
                    .scaleEffect(x: 1, y: 0.6, anchor: .top)
                HStack(spacing: 12) {
                    CoverImage(url: model.client?.coverURL(album: item.albumId), cornerRadius: 6)
                        .frame(width: 44, height: 44)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(item.title)
                            .font(.subheadline.weight(.medium))
                            .lineLimit(1)
                        Text(player.problem ?? item.artist)
                            .font(.caption)
                            .foregroundStyle(player.problem == nil ? AnyShapeStyle(.secondary) : AnyShapeStyle(.red))
                            .lineLimit(1)
                    }
                    Spacer(minLength: 8)
                    Button(action: player.toggle) {
                        Image(systemName: player.isPlaying ? "pause.fill" : "play.fill")
                            .font(.title2)
                            .frame(width: 44, height: 44)
                    }
                    .accessibilityLabel(player.isPlaying ? "Pause" : "Abspielen")
                    Button(action: player.next) {
                        Image(systemName: "forward.fill")
                            .font(.title3)
                            .frame(width: 36, height: 44)
                    }
                    .disabled(!player.queue.hasNext)
                    .accessibilityLabel("Nächstes")
                }
                .buttonStyle(.plain)
                .padding(.horizontal)
                .padding(.vertical, 8)
            }
            .background(.bar)
            .contentShape(Rectangle())
            .onTapGesture(perform: open)
        }
    }
}

struct NowPlayingView: View {
    @Environment(AppModel.self) private var model
    @Environment(Player.self) private var player
    @State private var scrub: Double?
    @State private var showsQueue = false
    @AppStorage("tonregal.showsLyrics") private var showsLyrics = false

    var body: some View {
        VStack(spacing: 24) {
            if !showsLyrics {
                Spacer(minLength: 12)
            }
            if let item = player.current {
                if showsLyrics {
                    LyricsView(trackId: item.trackId)
                } else {
                    CoverImage(url: model.client?.coverURL(album: item.albumId), cornerRadius: 16)
                        .frame(maxWidth: 360)
                        .shadow(radius: 16, y: 8)
                        .scaleEffect(player.isPlaying ? 1 : 0.92)
                        .animation(.spring(duration: 0.4), value: player.isPlaying)
                }
                VStack(spacing: 4) {
                    Text(item.title)
                        .font(.title2.weight(.semibold))
                        .lineLimit(2)
                        .multilineTextAlignment(.center)
                    Text("\(item.artist) – \(item.album)")
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                    StarRating(rating: player.rating(file: item.fileId)) { player.rate(file: item.fileId, $0) }
                        .padding(.top, 4)
                    if let problem = player.ratingProblem {
                        Text(problem)
                            .font(.caption)
                            .foregroundStyle(.red)
                            .multilineTextAlignment(.center)
                    }
                    if let problem = player.problem {
                        Text(problem)
                            .font(.caption)
                            .foregroundStyle(.red)
                            .multilineTextAlignment(.center)
                    }
                }
                timeline
                controls
            } else {
                ContentUnavailableView("Nichts in der Warteschlange", systemImage: "music.note")
            }
            if !showsLyrics {
                Spacer(minLength: 12)
            }
            HStack {
                RoutePicker()
                    .frame(width: 44, height: 44)
                Spacer()
                Button {
                    withAnimation(.easeInOut(duration: 0.25)) { showsLyrics.toggle() }
                } label: {
                    Image(systemName: showsLyrics ? "quote.bubble.fill" : "quote.bubble")
                        .font(.title3)
                        .frame(width: 44, height: 44)
                }
                .disabled(player.current == nil)
                .accessibilityLabel(showsLyrics ? "Cover zeigen" : "Songtext")
                Spacer()
                Button {
                    showsQueue = true
                } label: {
                    Image(systemName: "list.bullet")
                        .font(.title3)
                        .frame(width: 44, height: 44)
                }
                .accessibilityLabel("Warteschlange")
            }
        }
        .padding(.horizontal, 28)
        .padding(.bottom, 8)
        .sheet(isPresented: $showsQueue) {
            NavigationStack {
                QueueView()
                    .toolbar {
                        ToolbarItem(placement: .confirmationAction) {
                            Button("Fertig") { showsQueue = false }
                        }
                    }
            }
        }
    }

    private var timeline: some View {
        let length = max(player.duration, 1)
        return VStack(spacing: 4) {
            Slider(
                value: Binding(get: { min(scrub ?? player.elapsed, length) }, set: { scrub = $0 }),
                in: 0...length
            ) { editing in
                if !editing, let scrub {
                    player.seek(to: scrub)
                    self.scrub = nil
                }
            }
            HStack {
                Text(Duration.format(seconds: scrub ?? player.elapsed))
                Spacer()
                Text("-" + Duration.format(seconds: max(0, player.duration - (scrub ?? player.elapsed))))
            }
            .font(.caption.monospacedDigit())
            .foregroundStyle(.secondary)
        }
    }

    private var controls: some View {
        HStack(spacing: 28) {
            Button(action: player.shuffleUpcoming) {
                Image(systemName: "shuffle")
                    .font(.title3)
                    .foregroundStyle(.secondary)
                    .frame(width: 36, height: 44)
                    .contentShape(Rectangle())
            }
            .disabled(player.queue.upcoming.count < 2)
            .accessibilityLabel("Rest mischen")
            Button(action: player.previous) {
                Image(systemName: "backward.fill").font(.title)
            }
            .accessibilityLabel("Zurück")
            Button(action: player.toggle) {
                Image(systemName: player.isPlaying ? "pause.circle.fill" : "play.circle.fill")
                    .font(.system(size: 72))
            }
            .accessibilityLabel(player.isPlaying ? "Pause" : "Abspielen")
            Button(action: player.next) {
                Image(systemName: "forward.fill").font(.title)
            }
            .disabled(!player.queue.hasNext)
            .accessibilityLabel("Nächstes")
            RepeatButton()
        }
        .buttonStyle(.plain)
    }
}

struct QueueView: View {
    @Environment(Player.self) private var player

    var body: some View {
        List {
            ForEach(Array(player.queue.items.enumerated()), id: \.offset) { position, item in
                Button {
                    player.jump(to: position)
                } label: {
                    HStack(spacing: 12) {
                        if position == player.queue.index {
                            Image(systemName: player.isPlaying ? "speaker.wave.2.fill" : "speaker.fill")
                                .foregroundStyle(.tint)
                                .frame(width: 24)
                        } else {
                            Color.clear.frame(width: 24)
                        }
                        VStack(alignment: .leading, spacing: 2) {
                            Text(item.title)
                                .lineLimit(1)
                                .foregroundStyle(position == player.queue.index ? AnyShapeStyle(.tint) : AnyShapeStyle(.primary))
                            Text("\(item.artist) – \(item.album)")
                                .font(.caption)
                                .foregroundStyle(.secondary)
                                .lineLimit(1)
                        }
                        Spacer()
                        Text(Duration.format(seconds: Double(item.durationMs) / 1000))
                            .font(.caption.monospacedDigit())
                            .foregroundStyle(.secondary)
                    }
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                // Schon Gespieltes blass, wie im Web-Player.
                .opacity(player.queue.index.map { position < $0 } ?? false ? 0.5 : 1)
            }
            .onDelete { player.remove(at: $0) }
            .onMove { player.move(from: $0, to: $1) }
        }
        .listStyle(.plain)
        .navigationTitle("Warteschlange")
        .overlay {
            if player.queue.items.isEmpty {
                ContentUnavailableView("Leer", systemImage: "music.note.list", description: Text("Spiel ein Album ab oder häng Titel an."))
            }
        }
        .toolbar {
            ToolbarItem(placement: .topBarLeading) {
                EditButton().disabled(player.queue.items.isEmpty)
            }
            ToolbarItem(placement: .topBarTrailing) {
                Menu {
                    Button("Rest mischen", systemImage: "shuffle") { player.shuffleUpcoming() }
                    Button("Leeren", systemImage: "trash", role: .destructive) { player.stop() }
                } label: {
                    Image(systemName: "ellipsis.circle")
                }
                .disabled(player.queue.items.isEmpty)
            }
        }
    }
}

/// AirPlay und Bluetooth-Ausgänge, Apples eigener Knopf.
struct RoutePicker: UIViewRepresentable {
    func makeUIView(context: Context) -> AVRoutePickerView {
        let view = AVRoutePickerView()
        view.prioritizesVideoDevices = false
        return view
    }

    func updateUIView(_ uiView: AVRoutePickerView, context: Context) {}
}

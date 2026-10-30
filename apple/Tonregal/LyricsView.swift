// SPDX-License-Identifier: AGPL-3.0-or-later
import SwiftUI
import TonregalKit

/// Songtext im großen Player, das Gegenstück zu LyricsPanel.vue: synchronisierte Texte laufen mit, ein Tipp auf eine
/// Zeile springt dorthin.
struct LyricsView: View {
    @Environment(AppModel.self) private var model
    @Environment(Player.self) private var player
    let trackId: Int

    @State private var lyrics: SongLyrics?
    @State private var problem: String?
    @State private var loading = false

    var body: some View {
        Group {
            if let lyrics {
                content(lyrics)
            } else if let problem {
                ContentUnavailableView {
                    Label("Kein Songtext", systemImage: "exclamationmark.triangle")
                } description: {
                    Text(problem)
                } actions: {
                    Button("Erneut versuchen") { Task { await load(refresh: false) } }
                }
            } else {
                ProgressView()
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .task(id: trackId) { await load(refresh: false) }
    }

    @ViewBuilder
    private func content(_ lyrics: SongLyrics) -> some View {
        if lyrics.instrumental {
            ContentUnavailableView("Instrumental", systemImage: "pianokeys")
        } else if lyrics.lines.isEmpty {
            ContentUnavailableView {
                Label("Kein Songtext gefunden", systemImage: "text.quote")
            } description: {
                if let problem { Text(problem) }
            } actions: {
                Button("Neu suchen") { Task { await load(refresh: true) } }
                    .disabled(loading)
            }
        } else {
            let active = lyrics.currentLine(at: player.elapsed)
            ScrollViewReader { proxy in
                ScrollView {
                    VStack(alignment: .leading, spacing: lyrics.synced ? 14 : 4) {
                        ForEach(Array(lyrics.lines.enumerated()), id: \.offset) { index, line in
                            row(line, synced: lyrics.synced, active: index == active)
                                .id(index)
                        }
                        footer(lyrics)
                            .padding(.top, 16)
                    }
                    .padding(.vertical, 40)
                }
                .scrollIndicators(.hidden)
                // Oben und unten ausblenden, damit die Zeilen nicht hart abgeschnitten wirken.
                .mask(LinearGradient(stops: [
                    .init(color: .clear, location: 0), .init(color: .black, location: 0.08),
                    .init(color: .black, location: 0.92), .init(color: .clear, location: 1),
                ], startPoint: .top, endPoint: .bottom))
                .onAppear {
                    if let active { proxy.scrollTo(active, anchor: .center) }
                }
                .onChange(of: active) { _, index in
                    guard let index else { return }
                    withAnimation(.easeInOut(duration: 0.4)) { proxy.scrollTo(index, anchor: .center) }
                }
            }
        }
    }

    @ViewBuilder
    private func row(_ line: LyricLine, synced: Bool, active: Bool) -> some View {
        if synced {
            Text(line.text.isEmpty ? "♪" : line.text)
                .font(.title3.weight(.bold))
                .foregroundStyle(active ? AnyShapeStyle(.primary) : AnyShapeStyle(.tertiary))
                .frame(maxWidth: .infinity, alignment: .leading)
                .contentShape(Rectangle())
                .onTapGesture {
                    if let ms = line.timeMs { player.seek(to: Double(ms) / 1000) }
                }
                .animation(.easeInOut(duration: 0.25), value: active)
        } else {
            Text(line.text.isEmpty ? " " : line.text)
                .font(.body)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    private func footer(_ lyrics: SongLyrics) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            if let label = lyrics.sourceLabel {
                Text("Quelle: \(label)\(lyrics.synced ? ", synchronisiert" : "")")
            }
            if let problem {
                Text(problem).foregroundStyle(.red)
            }
            Button {
                Task { await load(refresh: true) }
            } label: {
                Label("Neu suchen", systemImage: "arrow.clockwise")
            }
            .disabled(loading)
        }
        .font(.footnote)
        .foregroundStyle(.secondary)
    }

    /// `refresh` behält den bisherigen Text, bis der neue da ist.
    private func load(refresh: Bool) async {
        guard let client = model.client else { return }
        if !refresh { lyrics = nil }
        problem = nil
        loading = true
        defer { loading = false }
        do {
            let result = refresh ? try await client.refreshLyrics(track: trackId) : try await client.lyrics(track: trackId)
            guard !Task.isCancelled else { return }
            lyrics = result
        } catch {
            guard !Task.isCancelled else { return }
            problem = error.localizedDescription
        }
    }
}

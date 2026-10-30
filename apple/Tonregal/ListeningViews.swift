// SPDX-License-Identifier: AGPL-3.0-or-later
import SwiftUI
import TonregalKit

/// Ein bis fünf Sterne; ein Tipp auf den gesetzten Stern nimmt die Bewertung zurück, wie im Web-Player.
struct StarRating: View {
    let rating: Int
    var size: Font = .title3
    let rate: (Int) -> Void

    var body: some View {
        HStack(spacing: 6) {
            ForEach(1...5, id: \.self) { star in
                Button {
                    rate(star == rating ? 0 : star)
                } label: {
                    Image(systemName: star <= rating ? "star.fill" : "star")
                        .font(size)
                        .foregroundStyle(star <= rating ? AnyShapeStyle(.tint) : AnyShapeStyle(.secondary))
                        .frame(minWidth: 28, minHeight: 32)
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .accessibilityLabel(star == 1 ? "1 Stern" : "\(star) Sterne")
            }
        }
        .accessibilityElement(children: .contain)
        .accessibilityValue(rating == 0 ? "unbewertet" : "\(rating) von 5")
    }
}

/// Sterne zum Ansehen, etwa in der Titelliste; unbewertet zeigt nichts.
struct StarsBadge: View {
    let rating: Int

    var body: some View {
        if rating > 0 {
            HStack(spacing: 1) {
                ForEach(0..<rating, id: \.self) { _ in
                    Image(systemName: "star.fill")
                }
            }
            .font(.system(size: 9))
            .foregroundStyle(.secondary)
            .accessibilityLabel(rating == 1 ? "1 Stern" : "\(rating) Sterne")
        }
    }
}

/// Untermenü „Bewerten“ für Kontextmenüs.
struct RatingMenu: View {
    @Environment(Player.self) private var player
    let fileId: Int

    var body: some View {
        Menu {
            ForEach((1...5).reversed(), id: \.self) { stars in
                Button {
                    player.rate(file: fileId, stars)
                } label: {
                    Text(String(repeating: "★", count: stars) + String(repeating: "☆", count: 5 - stars))
                    if player.rating(file: fileId) == stars {
                        Image(systemName: "checkmark")
                    }
                }
            }
            if player.rating(file: fileId) > 0 {
                Button("Bewertung entfernen", systemImage: "star.slash", role: .destructive) {
                    player.rate(file: fileId, 0)
                }
            }
        } label: {
            Label("Bewerten", systemImage: "star")
        }
    }
}

/// Holt eine zufällige Warteschlange vom Server: einen Interpreten oder die ganze Bibliothek.
struct ShuffleButton: View {
    @Environment(Player.self) private var player
    var artist: Int?
    var title = "Zufällig"
    @State private var loading = false
    @State private var error: String?

    var body: some View {
        Button {
            Task {
                loading = true
                defer { loading = false }
                do {
                    try await player.shuffle(artist: artist)
                } catch is CancellationError {
                } catch {
                    self.error = error.localizedDescription
                }
            }
        } label: {
            if loading {
                ProgressView()
            } else {
                Label(title, systemImage: "shuffle")
            }
        }
        .disabled(loading)
        .alert("Nicht gemischt", isPresented: Binding(get: { error != nil }, set: { if !$0 { error = nil } })) {
            Button("OK", role: .cancel) {}
        } message: {
            Text(error ?? "")
        }
    }
}

/// Aus, alle, dieses Stück, wie in der Musik-App.
struct RepeatButton: View {
    @Environment(Player.self) private var player

    var body: some View {
        Button(action: player.cycleRepeat) {
            Image(systemName: player.repeatMode == .one ? "repeat.1" : "repeat")
                .font(.title3)
                .foregroundStyle(player.repeatMode == .off ? AnyShapeStyle(.secondary) : AnyShapeStyle(.tint))
                .frame(width: 36, height: 44)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityLabel(label)
    }

    private var label: String {
        switch player.repeatMode {
        case .off: "Wiederholen: aus"
        case .all: "Wiederholen: alle"
        case .one: "Wiederholen: dieses Stück"
        }
    }
}

// SPDX-License-Identifier: AGPL-3.0-or-later
import SwiftUI
import TonregalKit

struct RootView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        if model.server == nil {
            SetupView()
        } else {
            MainView()
        }
    }
}

enum Route: Hashable {
    case album(Int)
    case artist(Int)
}

extension View {
    func libraryDestinations() -> some View {
        navigationDestination(for: Route.self) { route in
            switch route {
            case .album(let id): AlbumView(id: id)
            case .artist(let id): ArtistView(id: id)
            }
        }
    }
}

struct MainView: View {
    @Environment(AppModel.self) private var model
    @State private var showsPlayer = false

    var body: some View {
        TabView {
            NavigationStack { AlbumsView().libraryDestinations() }
                .modifier(PlayerInset(showsPlayer: $showsPlayer))
                .tabItem { Label("Alben", systemImage: "square.stack") }
            NavigationStack { ArtistsView().libraryDestinations() }
                .modifier(PlayerInset(showsPlayer: $showsPlayer))
                .tabItem { Label("Interpreten", systemImage: "music.mic") }
            NavigationStack { QueueView().libraryDestinations() }
                .modifier(PlayerInset(showsPlayer: $showsPlayer))
                .tabItem { Label("Warteschlange", systemImage: "list.bullet") }
            NavigationStack { SettingsView() }
                .tabItem { Label("Einstellungen", systemImage: "gearshape") }
        }
        .sheet(isPresented: $showsPlayer) {
            NowPlayingView()
                .presentationDragIndicator(.visible)
        }
    }
}

/// Die Leiste „läuft gerade“ über der Tab-Leiste, solange etwas in der Warteschlange steht.
private struct PlayerInset: ViewModifier {
    @Environment(Player.self) private var player
    @Binding var showsPlayer: Bool

    func body(content: Content) -> some View {
        content.safeAreaInset(edge: .bottom, spacing: 0) {
            if player.current != nil {
                PlayerBar { showsPlayer = true }
            }
        }
    }
}

struct SetupView: View {
    @Environment(AppModel.self) private var model
    @State private var address = ""
    @State private var connecting = false
    @State private var error: String?

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    TextField("oi-o-mat oder 100.x.y.z", text: $address)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                        .keyboardType(.URL)
                        .submitLabel(.go)
                        .onSubmit(connect)
                } header: {
                    Text("Server")
                } footer: {
                    Text("Die Adresse von Tonregal im Tailnet, wie im Browser. Ohne Angabe gelten http:// und Port 5100. Tailscale muss auf dem iPhone verbunden sein.")
                }
                if let error {
                    Section {
                        Label(error, systemImage: "exclamationmark.triangle")
                            .foregroundStyle(.red)
                    }
                }
                Section {
                    Button(action: connect) {
                        HStack {
                            Text("Verbinden")
                            if connecting {
                                Spacer()
                                ProgressView()
                            }
                        }
                    }
                    .disabled(connecting || address.trimmingCharacters(in: .whitespaces).isEmpty)
                }
            }
            .navigationTitle("Tonregal")
        }
    }

    private func connect() {
        guard !connecting else { return }
        connecting = true
        error = nil
        Task {
            defer { connecting = false }
            do {
                _ = try await model.connect(address)
            } catch {
                self.error = error.localizedDescription
            }
        }
    }
}

struct SettingsView: View {
    @Environment(AppModel.self) private var model
    @State private var health: Health?
    @State private var error: String?

    var body: some View {
        Form {
            Section("Server") {
                LabeledContent("Adresse", value: model.server?.absoluteString ?? "–")
                if let health {
                    LabeledContent("Tonregal", value: health.version)
                    LabeledContent("Library", value: health.libraryMounted ? "eingehängt" : "nicht erreichbar")
                } else if let error {
                    Text(error).foregroundStyle(.red)
                }
                Button("Anderen Server verbinden", role: .destructive) {
                    model.disconnect()
                }
            }
            Section("App") {
                LabeledContent("Version", value: Self.appVersion)
            }
        }
        .navigationTitle("Einstellungen")
        .task {
            guard let client = model.client else { return }
            do {
                health = try await client.health()
            } catch {
                self.error = error.localizedDescription
            }
        }
    }

    private static var appVersion: String {
        let info = Bundle.main.infoDictionary
        let version = info?["CFBundleShortVersionString"] as? String ?? "?"
        let build = info?["CFBundleVersion"] as? String ?? "?"
        return "\(version) (\(build))"
    }
}

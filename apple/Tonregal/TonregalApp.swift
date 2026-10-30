// SPDX-License-Identifier: AGPL-3.0-or-later
import SwiftUI

@main
struct TonregalApp: App {
    @State private var model = AppModel()
    @Environment(\.scenePhase) private var scenePhase

    var body: some Scene {
        WindowGroup {
            RootView()
                .environment(model)
                .environment(model.player)
        }
        .onChange(of: scenePhase) { _, phase in
            // iOS beendet eine Hintergrund-App ohne Bescheid; Schlange und Position vorher sichern.
            if phase != .active {
                model.player.save()
            } else {
                Task { await model.refreshLibrary() }
            }
        }
    }
}

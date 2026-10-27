// swift-tools-version: 6.0
// SPDX-License-Identifier: AGPL-3.0-or-later
import PackageDescription

// Was die iPhone-App ohne UIKit braucht: Datentypen der API, Client, Adresse, Warteschlange. Ohne Abhängigkeiten,
// damit `swift test` auf jedem Mac (und in der CI) läuft; Seiten und Player liegen im App-Target.
let package = Package(
    name: "TonregalKit",
    platforms: [.iOS(.v17), .macOS(.v14)],
    products: [
        .library(name: "TonregalKit", targets: ["TonregalKit"]),
    ],
    targets: [
        .target(name: "TonregalKit"),
        .testTarget(name: "TonregalKitTests", dependencies: ["TonregalKit"]),
    ]
)

// SPDX-License-Identifier: AGPL-3.0-or-later
import Foundation

/// Die Adresse, die beim ersten Start eingetragen wird. Tonregal lauscht auf oi-o-mat nur auf `localhost:5100`;
/// Tailscale Serve veröffentlicht es im eigenen Tailnet unter `https://oi-o-mat.tail6105c0.ts.net` (Port 443, TLS).
public enum ServerAddress {
    /// Vorgabe im Eingabefeld: der Zugang über Tailscale Serve.
    public static let defaultServer = "https://oi-o-mat.tail6105c0.ts.net"
    public static let defaultPort = 5100

    /// Die Wurzel des Servers aus dem Getippten. Eine vollständige URL mit `http://` oder `https://` bleibt, wie sie
    /// ist (ohne Port also der des Schemas); nur ein bloßer Hostname oder eine IP bekommt `http://` und Port 5100
    /// (`oi-o-mat` → `http://oi-o-mat:5100`). Pfad, Query und Fragment fallen weg, sodass auch die Adresse einer
    /// Seite der Weboberfläche zum selben Server führt.
    public static func parse(_ text: String) -> URL? {
        var trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return nil }
        let bare = !trimmed.contains("://")
        if bare {
            trimmed = "http://" + trimmed
        }
        guard var components = URLComponents(string: trimmed),
              let scheme = components.scheme?.lowercased(), scheme == "https" || scheme == "http",
              let host = components.host, !host.isEmpty
        else { return nil }
        components.scheme = scheme
        if bare && components.port == nil {
            components.port = defaultPort
        }
        components.user = nil
        components.password = nil
        components.path = ""
        components.query = nil
        components.fragment = nil
        return components.url
    }
}

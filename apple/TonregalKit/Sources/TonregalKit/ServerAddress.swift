// SPDX-License-Identifier: AGPL-3.0-or-later
import Foundation

/// Die Adresse, die beim ersten Start eingetragen wird. Tonregal läuft auf oi-o-mat unter `http://<Tailscale-IP>:5100`
/// und ist nur im eigenen Tailnet erreichbar; eingebaut ist nichts.
public enum ServerAddress {
    public static let defaultPort = 5100

    /// Die Wurzel des Servers aus dem Getippten: ohne Schema `http://`, ohne Port bei http der Port 5100
    /// (`oi-o-mat` → `http://oi-o-mat:5100`); Pfad, Query und Fragment fallen weg, sodass auch die Adresse einer
    /// Seite der Weboberfläche zum selben Server führt.
    public static func parse(_ text: String) -> URL? {
        var trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return nil }
        if !trimmed.contains("://") {
            trimmed = "http://" + trimmed
        }
        guard var components = URLComponents(string: trimmed),
              let scheme = components.scheme?.lowercased(), scheme == "https" || scheme == "http",
              let host = components.host, !host.isEmpty
        else { return nil }
        components.scheme = scheme
        if scheme == "http" && components.port == nil {
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

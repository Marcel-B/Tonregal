// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Tonregal.Application;

/// <summary>Zugang zu Navidrome für den Player; Abschnitt <c>Tonregal:Navidrome</c>.</summary>
public sealed class NavidromeOptions
{
    public const string SectionName = "Tonregal:Navidrome";

    /// <summary>Basis-URL, z. B. <c>http://100.x.y.z:4533</c>; leer = der Player spielt die Dateien direkt.</summary>
    public string Url { get; set; } = "";

    /// <summary>Navidrome-Benutzer; Passwort nur in appsettings.Production.json, nie im Repo.</summary>
    public string User { get; set; } = "";

    public string Password { get; set; } = "";

    /// <summary>navidrome.db, um Dateien den Navidrome-IDs zuzuordnen; leer = <c>{MusicRoot}/navidrome/navidrome.db</c>.</summary>
    public string Database { get; set; } = "";

    public bool Configured => Url.Length > 0 && User.Length > 0 && Password.Length > 0;
}

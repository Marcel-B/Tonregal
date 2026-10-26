// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Tonregal.Application;

/// <summary>Nächtliches Backup nach Nextcloud per rclone; Abschnitt <c>Tonregal:Backup</c>.</summary>
public sealed class BackupOptions
{
    public const string SectionName = "Tonregal:Backup";

    /// <summary>Ohne <c>false</c> läuft das Backup jede Nacht zur <see cref="Time"/>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Pfad zu rclone; ohne Pfad wird es im PATH gesucht.</summary>
    public string RclonePath { get; set; } = "rclone";

    /// <summary>Ziel als rclone-Pfad; der Remote (hier <c>nextcloud</c>) kommt aus <c>rclone config</c>, nie aus dem Repo.</summary>
    public string Remote { get; set; } = "nextcloud:Musik";

    /// <summary>Startzeit (Ortszeit).</summary>
    public TimeOnly Time { get; set; } = new(3, 0);

    /// <summary>So viele Tage bleiben die Datenbank-Stände unter <c>_state/</c> liegen.</summary>
    public int KeepStateDays { get; set; } = 7;

    /// <summary>
    /// Bricht ab, wenn rclone mehr Dateien in Nextcloud löschen würde – Schutz davor, dass eine leere
    /// oder falsch gemountete Library das Backup leert.
    /// </summary>
    public int MaxDelete { get; set; } = 500;

    /// <summary>navidrome.db für den Datenbank-Stand; leer = <c>{MusicRoot}/navidrome/navidrome.db</c>.</summary>
    public string NavidromeDatabase { get; set; } = "";

    /// <summary>Optionales Bandbreitenlimit für rclone, z. B. <c>8M</c>.</summary>
    public string? BandwidthLimit { get; set; }

    /// <summary>Höchstdauer eines Laufs; der erste kann bei großer Library Stunden dauern.</summary>
    public int TimeoutHours { get; set; } = 12;

    /// <summary>Name des rclone-Remotes, z. B. <c>nextcloud</c>.</summary>
    public string RemoteName => Remote.Split(':', 2)[0];
}

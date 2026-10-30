// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Tonregal.Domain.Entities;

/// <summary>
/// Songtext eines Tracks samt Herkunft. Lässt sich aus den <c>.lrc</c>-Dateien neben den Audiodateien und den Tags
/// neu aufbauen; die Zeile merkt sich vor allem, wann zuletzt gesucht wurde, damit ein Track ohne Text nicht bei jedem
/// Abspielen erneut bei LRCLIB angefragt wird.
/// </summary>
public class TrackLyrics
{
    public int TrackId { get; set; }
    public Track Track { get; set; } = null!;
    public LyricsSource Source { get; set; }

    /// <summary>LRC (mit Zeitmarken) oder reiner Text; <c>null</c>, wenn nichts gefunden wurde oder der Track instrumental ist.</summary>
    public string? Text { get; set; }
    public bool Synced { get; set; }
    public bool Instrumental { get; set; }
    public DateTimeOffset CheckedAt { get; set; }
}

// SPDX-License-Identifier: AGPL-3.0-or-later
using Tonregal.Domain.Entities;

namespace Tonregal.Application.Abstractions;

/// <summary>Eine Zeile; <see cref="TimeMs"/> nur bei synchronisierten Texten.</summary>
public sealed record LyricLine(int? TimeMs, string Text);

public sealed record SongLyrics(LyricsSource Source, bool Synced, bool Instrumental, IReadOnlyList<LyricLine> Lines);

/// <summary>
/// Songtexte: erst die <c>.lrc</c>-Datei neben der Audiodatei, dann der eingebettete Text, dann lrclib.net.
/// Ergebnisse (auch „nichts gefunden“) liegen in der Datenbank.
/// </summary>
public interface ILyricsService
{
    /// <returns><c>null</c>, wenn es den Track nicht gibt.</returns>
    /// <param name="refresh">Gespeichertes und <c>.lrc</c> übergehen und neu bei lrclib.net suchen.</param>
    /// <exception cref="ExternalToolUnavailableException">lrclib.net nicht erreichbar und nichts Lokales vorhanden.</exception>
    Task<SongLyrics?> GetAsync(int trackId, bool refresh, CancellationToken ct);
}

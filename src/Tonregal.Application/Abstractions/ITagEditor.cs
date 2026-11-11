// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Tonregal.Application.Abstractions;

/// <summary>
/// Änderungen aus dem Tag-Editor. <c>null</c> lässt ein Feld, wie es ist; ein leerer Text bei Genre, 0 bei Jahr oder
/// Nummer entfernt den Wert. Titel, Nummer und Disc gibt es nur für einen einzelnen Track.
/// </summary>
public sealed record TagEdit
{
    public string? Title { get; init; }

    /// <summary>Die Interpreten des Tracks, in ihrer Reihenfolge.</summary>
    public IReadOnlyList<string>? Artists { get; init; }

    public string? AlbumArtist { get; init; }
    public string? Album { get; init; }
    public int? Year { get; init; }
    public string? Genre { get; init; }
    public int? Number { get; init; }
    public int? Disc { get; init; }
}

/// <param name="AlbumId">Das Album, in dem die Tracks jetzt stehen; nach neuem Album oder Interpreten ein anderes.</param>
/// <param name="Changed">Dateien mit neuen Tags.</param>
/// <param name="Moved">Davon nach der Ordnerregel umbenannt.</param>
/// <param name="Errors">Dateien, die sich nicht schreiben ließen, mit Grund; die übrigen sind trotzdem geändert.</param>
public sealed record TagEditResult(int? AlbumId, int Changed, int Moved, IReadOnlyList<string> Errors);

/// <summary>Ein unzulässiger Wert im Tag-Editor, z. B. ein leerer Titel.</summary>
public sealed class TagEditException(string message) : Exception(message);

/// <summary>
/// Schreibt Änderungen aus dem Browser in die Dateien (die Wahrheit), benennt sie nach der Ordnerregel um und zieht
/// den Index nach. Bewertungen, Playlists und Songtexte (<c>.lrc</c>) gehen mit.
/// </summary>
public interface ITagEditor
{
    /// <exception cref="KeyNotFoundException">Track gibt es nicht.</exception>
    /// <exception cref="TagEditException">Unzulässiger Wert.</exception>
    /// <exception cref="LibraryUnavailableException">Mount-Marker fehlt.</exception>
    Task<TagEditResult> EditTrackAsync(int trackId, TagEdit edit, CancellationToken ct);

    /// <summary>Für alle Tracks des Albums; Titel, Nummer und Disc sind hier nicht erlaubt.</summary>
    /// <exception cref="KeyNotFoundException">Album gibt es nicht.</exception>
    /// <exception cref="TagEditException">Unzulässiger Wert.</exception>
    /// <exception cref="LibraryUnavailableException">Mount-Marker fehlt.</exception>
    Task<TagEditResult> EditAlbumAsync(int albumId, TagEdit edit, CancellationToken ct);

    /// <summary>Neues Cover: <c>cover.jpg</c> bzw. <c>cover.png</c> im Albumordner und eingebettet in jede Datei.</summary>
    /// <exception cref="KeyNotFoundException">Album gibt es nicht.</exception>
    /// <exception cref="TagEditException">Kein JPEG oder PNG.</exception>
    /// <exception cref="LibraryUnavailableException">Mount-Marker fehlt.</exception>
    Task<TagEditResult> SetCoverAsync(int albumId, EmbeddedPicture cover, CancellationToken ct);
}

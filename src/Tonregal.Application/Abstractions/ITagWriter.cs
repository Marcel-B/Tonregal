// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Tonregal.Application.Abstractions;

/// <summary>
/// Was der Tag-Editor in eine Datei schreibt; <c>null</c> lässt das Feld, wie es ist. Jahr, Nummer oder Disc 0 und ein
/// leeres Genre entfernen den Wert.
/// </summary>
public sealed record TagChanges
{
    public string? Title { get; init; }
    public IReadOnlyList<string>? Artists { get; init; }
    public string? AlbumArtist { get; init; }
    public string? Album { get; init; }
    public int? Year { get; init; }
    public string? Genre { get; init; }
    public int? Track { get; init; }
    public int? Disc { get; init; }
    public bool? Compilation { get; init; }

    /// <summary>
    /// Die MusicBrainz-IDs des Releases entfernen (Release, Release-Group, Release-Track, Album-Artist), weil der Track
    /// in ein anderes Album wandert; die Aufnahme behält ihre ID.
    /// </summary>
    public bool DropRelease { get; init; }

    public bool IsEmpty => this == new TagChanges();
}

public interface ITagWriter
{
    /// <summary>
    /// Schreibt Titel, Artists, Album, Nummern und MusicBrainz-IDs (ID3v2.4, Vorbis Comments oder MP4,
    /// Feldnamen wie Picard). Andere Tags (BPM, Tonart, ReplayGain, Genre) bleiben erhalten.
    /// Ein Cover wird nur eingebettet, wenn die Datei noch keins hat.
    /// </summary>
    /// <exception cref="InvalidDataException">Datei ist kein schreibbares Audioformat.</exception>
    void Write(string path, ReleaseTrack track, EmbeddedPicture? cover);

    /// <summary>
    /// Nur Titel, Artists, Album und Album-Artist, für Dateien ohne MusicBrainz-Treffer. Alles
    /// andere (Nummern, Jahr, IDs, Cover) bleibt, wie es ist.
    /// </summary>
    /// <exception cref="InvalidDataException">Datei ist kein schreibbares Audioformat.</exception>
    void WriteBasic(string path, string title, IReadOnlyList<string> artists, string albumArtist, string album);

    /// <summary>
    /// Für Stücke aus der Ingest-API: Titel, Artist, Album, Jahr, Tracknummer, Genre, BPM und Tonart;
    /// leere Werte lassen das Feld, wie es ist. Ein Cover wird nur eingebettet, wenn die Datei noch keins hat.
    /// </summary>
    /// <exception cref="InvalidDataException">Datei ist kein schreibbares Audioformat.</exception>
    void WriteIngest(string path, IngestMetadata metadata, string album, EmbeddedPicture? cover);

    /// <summary>Setzt BPM (gerundet, TBPM/BPM) und Tonart (TKEY/INITIALKEY); <c>null</c> lässt den Wert, wie er ist.</summary>
    /// <exception cref="InvalidDataException">Datei ist kein schreibbares Audioformat.</exception>
    void WriteAnalysis(string path, double? bpm, string? key);

    /// <summary>
    /// Änderungen aus dem Tag-Editor. Ein neuer Interpret oder Album-Interpret nimmt die alte Sortierform mit weg,
    /// sie gehörte zum alten Namen.
    /// </summary>
    /// <exception cref="InvalidDataException">Datei ist kein schreibbares Audioformat.</exception>
    void WriteChanges(string path, TagChanges changes);

    /// <summary>Ersetzt alle eingebetteten Bilder durch dieses Front-Cover.</summary>
    /// <exception cref="InvalidDataException">Datei ist kein schreibbares Audioformat.</exception>
    void ReplaceCover(string path, EmbeddedPicture cover);
}

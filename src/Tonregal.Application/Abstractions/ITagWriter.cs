// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Tonregal.Application.Abstractions;

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
}

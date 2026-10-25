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
}

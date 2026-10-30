// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Tonregal.Application;

/// <summary>Songtexte; Abschnitt <c>Tonregal:Lyrics</c>.</summary>
public sealed class LyricsOptions
{
    public const string SectionName = "Tonregal:Lyrics";

    /// <summary>Fehlende Texte bei lrclib.net suchen. Aus: nur <c>.lrc</c>-Dateien und Tags.</summary>
    public bool Online { get; set; } = true;

    /// <summary>Gefundene Texte als <c>.lrc</c> neben die Audiodatei schreiben, damit sie Neuaufbau und Backup überstehen.</summary>
    public bool WriteSidecar { get; set; } = true;

    /// <summary>Nach so vielen Tagen wird ein Track ohne (synchronisierten) Text erneut gesucht.</summary>
    public int RetryDays { get; set; } = 30;
}

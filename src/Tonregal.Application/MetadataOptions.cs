// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Tonregal.Application;

/// <summary>Externe Werkzeuge und Metadaten-Dienste; Abschnitt <c>Tonregal:Metadata</c>.</summary>
public sealed class MetadataOptions
{
    public const string SectionName = "Tonregal:Metadata";

    /// <summary>Pfad zu fpcalc (Chromaprint); ohne Pfad wird es im PATH gesucht.</summary>
    public string FpcalcPath { get; set; } = "fpcalc";

    /// <summary>Client-Key von https://acoustid.org/new-application – gehört in appsettings.Production.json, nicht ins Repo.</summary>
    public string? AcoustIdApiKey { get; set; }

    /// <summary>Kontakt im User-Agent, Pflicht bei MusicBrainz.</summary>
    public string Contact { get; set; } = "https://github.com/Marcel-B/Tonregal";

    /// <summary>Wie lange MusicBrainz-Antworten im DB-Cache gültig sind.</summary>
    public int CacheDays { get; set; } = 30;

    /// <summary>AcoustID-Treffer unter diesem Score werden verworfen.</summary>
    public double MinimumScore { get; set; } = 0.5;

    /// <summary>Ab diesem Score wird ein eindeutiger Treffer ohne Review übernommen.</summary>
    public double AutoMatchScore { get; set; } = 0.9;
}

// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Tonregal.Application;

/// <summary>Ingest-API für eigene Stücke (z. B. aus Tonwerk-AI); Abschnitt <c>Tonregal:Ingest</c>.</summary>
public sealed class IngestOptions
{
    public const string SectionName = "Tonregal:Ingest";

    /// <summary>
    /// API-Key je Quelle, z. B. <c>{ "tonwerk-ai": "…" }</c>; der Key bestimmt die Quelle. Nur in
    /// appsettings.Production.json, nie im Repo. Ohne Eintrag ist die Ingest-API abgeschaltet.
    /// </summary>
    public Dictionary<string, string> Keys { get; set; } = [];
}

// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Tonregal.Application;

/// <summary>Ingest-API für eigene Stücke (z. B. aus Tonwerk-AI); Abschnitt <c>Tonregal:Ingest</c>.</summary>
public sealed class IngestOptions
{
    public const string SectionName = "Tonregal:Ingest";

    /// <summary>Quelle, die die Seite „Verbindungen“ immer anbietet, auch ohne Key.</summary>
    public const string DefaultSource = "tonwerk-ai";

    /// <summary>
    /// API-Key je Quelle, z. B. <c>{ "tonwerk-ai": "…" }</c>; der Key bestimmt die Quelle. Nur in
    /// appsettings.Production.json, nie im Repo. Daneben gelten die auf der Seite „Verbindungen“ erzeugten
    /// Keys (Tabelle <c>IngestKeys</c>); ein Key hier hat für seine Quelle Vorrang und lässt sich dort nicht ändern.
    /// Ohne Key ist die Ingest-API abgeschaltet.
    /// </summary>
    public Dictionary<string, string> Keys { get; set; } = [];
}

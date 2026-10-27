// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Tonregal.Domain.Entities;

/// <summary>In der Oberfläche erzeugter API-Key einer Ingest-Quelle (z. B. <c>tonwerk-ai</c>).</summary>
public class IngestKey
{
    public required string Source { get; set; }
    public required string Key { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

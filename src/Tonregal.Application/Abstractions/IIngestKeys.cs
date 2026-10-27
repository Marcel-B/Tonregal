// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Tonregal.Application.Abstractions;

/// <summary>Key einer Ingest-Quelle; <paramref name="Key"/> ist <c>null</c>, solange keiner erzeugt wurde.</summary>
/// <param name="FromConfig">Steht in appsettings und lässt sich nur dort ändern.</param>
public sealed record IngestKeyInfo(string Source, string? Key, bool FromConfig, DateTimeOffset? CreatedAt);

/// <summary>API-Keys der Ingest-API: aus <see cref="IngestOptions.Keys"/> und in der Datenbank erzeugte.</summary>
public interface IIngestKeys
{
    /// <summary>Alle Quellen mit Key, dazu immer <see cref="IngestOptions.DefaultSource"/>.</summary>
    Task<IReadOnlyList<IngestKeyInfo>> ListAsync(CancellationToken ct);

    /// <summary>Quelle zum Key oder <c>null</c>; vergleicht in konstanter Zeit.</summary>
    Task<string?> AuthenticateAsync(string presented, CancellationToken ct);

    /// <summary>Ob es überhaupt einen Key gibt; sonst ist die Ingest-API abgeschaltet.</summary>
    Task<bool> AnyAsync(CancellationToken ct);

    /// <summary>Erzeugt einen neuen Key für die Quelle; der alte gilt danach nicht mehr.</summary>
    /// <exception cref="ArgumentException">Ungültiger Name der Quelle.</exception>
    /// <exception cref="InvalidOperationException">Der Key der Quelle steht in appsettings.</exception>
    Task<IngestKeyInfo> RegenerateAsync(string source, CancellationToken ct);
}

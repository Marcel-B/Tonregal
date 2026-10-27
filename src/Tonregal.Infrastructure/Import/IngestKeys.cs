// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tonregal.Application;
using Tonregal.Application.Abstractions;
using Tonregal.Domain.Entities;
using Tonregal.Infrastructure.Persistence;

namespace Tonregal.Infrastructure.Import;

/// <summary>
/// Keys aus appsettings (fest) und aus der Datenbank (auf der Seite „Verbindungen“ erzeugt). Für eine Quelle mit
/// Key in appsettings gilt nur dieser, damit ein alter Datenbank-Key nicht still weiter funktioniert.
/// </summary>
public sealed partial class IngestKeys(TonregalDbContext db, IOptions<IngestOptions> options, TimeProvider time) : IIngestKeys
{
    public async Task<IReadOnlyList<IngestKeyInfo>> ListAsync(CancellationToken ct)
    {
        var configured = Configured();
        var stored = await db.IngestKeys.AsNoTracking().ToListAsync(ct);
        var keys = configured.Select(k => new IngestKeyInfo(k.Key, k.Value, true, null))
            .Concat(stored.Where(k => !configured.ContainsKey(k.Source)).Select(k => new IngestKeyInfo(k.Source, k.Key, false, k.CreatedAt)))
            .ToList();
        if (keys.All(k => k.Source != IngestOptions.DefaultSource))
            keys.Add(new IngestKeyInfo(IngestOptions.DefaultSource, null, false, null));
        return keys.OrderBy(k => k.Source, StringComparer.Ordinal).ToList();
    }

    public async Task<string?> AuthenticateAsync(string presented, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(presented);
        if (bytes.Length == 0)
            return null;
        var configured = Configured();
        var stored = await db.IngestKeys.AsNoTracking().ToListAsync(ct);
        return configured
            .Concat(stored.Where(k => !configured.ContainsKey(k.Source)).Select(k => KeyValuePair.Create(k.Source, k.Key)))
            .FirstOrDefault(k => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(k.Value), bytes)).Key;
    }

    public async Task<bool> AnyAsync(CancellationToken ct) => Configured().Count > 0 || await db.IngestKeys.AnyAsync(ct);

    public async Task<IngestKeyInfo> RegenerateAsync(string source, CancellationToken ct)
    {
        if (!SourcePattern().IsMatch(source))
            throw new ArgumentException("Quelle: Kleinbuchstaben, Ziffern und Bindestriche, höchstens 64 Zeichen", nameof(source));
        if (Configured().ContainsKey(source))
            throw new InvalidOperationException($"Der Key für {source} steht in appsettings.Production.json und lässt sich nur dort ändern");

        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var entry = await db.IngestKeys.FindAsync([source], ct);
        if (entry is null)
            db.IngestKeys.Add(entry = new IngestKey { Source = source, Key = key });
        else
            entry.Key = key;
        entry.CreatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return new IngestKeyInfo(source, key, false, entry.CreatedAt);
    }

    private Dictionary<string, string> Configured() =>
        options.Value.Keys.Where(k => !string.IsNullOrWhiteSpace(k.Value)).ToDictionary(k => k.Key, k => k.Value);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex SourcePattern();
}

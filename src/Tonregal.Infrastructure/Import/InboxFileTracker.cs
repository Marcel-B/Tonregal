// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;

namespace Tonregal.Infrastructure.Import;

/// <summary>
/// Merkt sich, seit wann eine Inbox-Datei ihre Größe nicht geändert hat. Erst danach gilt sie als
/// fertig kopiert; Änderungszeiten taugen dafür nicht, weil der Finder sie vom Original übernimmt.
/// </summary>
public sealed class InboxFileTracker(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, (long Size, DateTimeOffset Since)> _seen = new(StringComparer.Ordinal);

    public bool IsStable(string path, long size, TimeSpan stableAfter)
    {
        var now = time.GetUtcNow();
        var entry = _seen.AddOrUpdate(path, (size, now), (_, old) => old.Size == size ? old : (size, now));
        return now - entry.Since >= stableAfter;
    }

    /// <summary>Vergisst Dateien, die nicht mehr in der Inbox liegen.</summary>
    public void Retain(IReadOnlySet<string> paths)
    {
        foreach (var path in _seen.Keys.Where(p => !paths.Contains(p)))
            _seen.TryRemove(path, out _);
    }
}

// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;

namespace Tonregal.Infrastructure.Library;

/// <summary>
/// Papierkorb unter <c>Library/.trash/{Datum}/</c>: verworfene Dateien behalten dort ihren relativen
/// Pfad und werden nach <see cref="Retention"/> endgültig gelöscht. Der Scanner ignoriert Punkt-Ordner.
/// </summary>
public static class LibraryTrash
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private const string DateFormat = "yyyy-MM-dd";

    /// <summary>Verschiebt <paramref name="source"/> nach <c>{trash}/{Datum}/{relative}</c>, ohne etwas zu überschreiben.</summary>
    public static string Move(string trashRoot, string source, string relative, DateTimeOffset now)
    {
        var target = Path.Combine(trashRoot, Day(now), relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var free = target;
        for (var i = 2; File.Exists(free); i++)
            free = Path.Combine(Path.GetDirectoryName(target)!, $"{Path.GetFileNameWithoutExtension(target)} ({i}){Path.GetExtension(target)}");
        File.Move(source, free);
        return free;
    }

    /// <summary>Name des Tagesordners, in den <see cref="Move"/> heute verschiebt.</summary>
    public static string Day(DateTimeOffset now) => now.UtcDateTime.ToString(DateFormat, CultureInfo.InvariantCulture);

    public static int Purge(string trashRoot, DateTimeOffset now)
    {
        if (!Directory.Exists(trashRoot))
            return 0;
        var removed = 0;
        foreach (var folder in Directory.EnumerateDirectories(trashRoot))
        {
            if (!DateTime.TryParseExact(Path.GetFileName(folder), DateFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var day))
                continue;
            if (now.UtcDateTime - day < Retention)
                continue;
            Directory.Delete(folder, recursive: true);
            removed++;
        }

        return removed;
    }
}

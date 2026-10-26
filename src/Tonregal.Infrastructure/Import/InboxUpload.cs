// SPDX-License-Identifier: AGPL-3.0-or-later
using Tonregal.Infrastructure.Tagging;

namespace Tonregal.Infrastructure.Import;

/// <summary>
/// Legt hochgeladene Dateien in die Inbox. Der relative Pfad aus dem Browser bleibt erhalten,
/// damit ein Album-Ordner ein Album-Kandidat bleibt. Geschrieben wird erst in eine versteckte
/// Temp-Datei, damit der Watcher keine halbe Datei sieht; vorhandene Dateien werden nie überschrieben.
/// </summary>
public static class InboxUpload
{
    private static readonly char[] InvalidChars = [.. Path.GetInvalidFileNameChars(), ':', '\\'];

    /// <returns>Pfad relativ zur Inbox, mit <c>/</c> getrennt.</returns>
    /// <exception cref="ArgumentException">Pfad unzulässig oder kein unterstütztes Audioformat.</exception>
    /// <param name="ingest">Nur die Ingest-API darf nach <c>_api/</c> schreiben.</param>
    public static async Task<string> SaveAsync(string inboxRoot, string relativePath, Stream content, CancellationToken ct, bool ingest = false)
    {
        var segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .ToList();
        if (segments.Count == 0 || segments.Any(s => s is "" or "." or ".." || s.StartsWith('.') || s.IndexOfAny(InvalidChars) >= 0))
            throw new ArgumentException($"Ungültiger Pfad: {relativePath}");
        if ((segments[0] == InboxImporter.ApiFolder) != ingest)
            throw new ArgumentException($"{InboxImporter.ApiFolder} ist der Ingest-API vorbehalten");
        var extension = Path.GetExtension(segments[^1]);
        if (!TagLibTagReader.SupportedExtensions.Contains(extension))
            throw new ArgumentException($"Kein unterstütztes Audioformat: {segments[^1]}");

        var root = Path.GetFullPath(inboxRoot);
        var target = Path.Combine([root, .. segments]);
        var folder = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(folder);

        var temp = Path.Combine(folder, $".upload-{Guid.NewGuid():N}{extension}");
        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                await content.CopyToAsync(file, ct);

            var name = Path.GetFileNameWithoutExtension(target);
            for (var i = 2; ; i++)
            {
                try
                {
                    File.Move(temp, target, overwrite: false);
                    break;
                }
                catch (IOException) when (File.Exists(target))
                {
                    target = Path.Combine(folder, $"{name} ({i}){extension}");
                }
            }
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }

        return Path.GetRelativePath(root, target).Replace(Path.DirectorySeparatorChar, '/');
    }
}

// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Tonregal.Domain.Entities;

namespace Tonregal.Domain.ValueObjects;

/// <summary>
/// Ordnerregel der Library:
/// <c>{AlbumArtist}/{Year} - {Album}/{Disc-}{NN} - {Title}.{ext}</c>, Compilations unter
/// <c>Various Artists</c>. Liefert Pfade relativ zum Library-Wurzelordner.
/// Sonderzeichen werden für das Dateisystem ersetzt, der Originaltitel bleibt im Tag.
/// </summary>
public static class LibraryPath
{
    private const int MaxSegmentBytes = 200;
    private static readonly char[] InvalidChars = ['/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    public static string AlbumFolder(string albumArtist, int? year, string albumTitle, AlbumKind kind)
    {
        var artistFolder = kind == AlbumKind.Compilation ? Artist.VariousArtists : albumArtist;
        var albumFolder = year is { } y ? $"{y} - {albumTitle}" : albumTitle;
        return Path.Combine(Sanitize(artistFolder), Sanitize(albumFolder));
    }

    public static string FileName(int disc, int discCount, int number, string title, string extension)
    {
        var prefix = discCount > 1 ? $"{disc}-{number:00}" : $"{number:00}";
        var ext = extension.TrimStart('.').ToLowerInvariant();
        return Sanitize($"{prefix} - {title}") + "." + ext;
    }

    public static string For(
        string albumArtist, int? year, string albumTitle, AlbumKind kind,
        int disc, int discCount, int number, string title, string extension) =>
        Path.Combine(
            AlbumFolder(albumArtist, year, albumTitle, kind),
            FileName(disc, discCount, number, title, extension));

    /// <summary>Macht aus einem beliebigen Text ein gültiges, sichtbares Pfadsegment.</summary>
    public static string Sanitize(string segment)
    {
        var sb = new StringBuilder(segment.Length);
        foreach (var c in segment.Normalize(NormalizationForm.FormC))
        {
            if (char.IsControl(c))
                continue;
            sb.Append(Array.IndexOf(InvalidChars, c) >= 0 ? '_' : c);
        }

        var result = sb.ToString().Trim().TrimStart('.').TrimEnd('.', ' ');
        result = Truncate(result, MaxSegmentBytes);
        return result.Length == 0 ? "_" : result;
    }

    private static string Truncate(string value, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
            return value;

        var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(value);
        var sb = new StringBuilder();
        var bytes = 0;
        while (enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();
            var size = Encoding.UTF8.GetByteCount(element);
            if (bytes + size > maxBytes)
                break;
            sb.Append(element);
            bytes += size;
        }

        return sb.ToString().TrimEnd('.', ' ');
    }
}

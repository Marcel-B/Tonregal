// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Tonregal.Domain.ValueObjects;

/// <summary>
/// Fallback-Identität eines Tracks ohne Fingerprint-Treffer: normalisierter Artist und Titel
/// plus Dauer. Zwei Keys gelten als gleich, wenn Text identisch ist und die Dauer um höchstens
/// <see cref="DurationTolerance"/> abweicht.
/// </summary>
public sealed partial record TrackKey(string Artist, string Title, int DurationSeconds)
{
    public const int DurationTolerance = 2;

    public static TrackKey Create(string artist, string title, TimeSpan duration) =>
        new(Normalize(artist), Normalize(StripFeaturing(title)), (int)Math.Round(duration.TotalSeconds));

    /// <summary>Textteil des Keys, geeignet als DB-Index; die Dauer wird beim Vergleich separat geprüft.</summary>
    public string TextKey => $"{Artist}|{Title}";

    public bool Matches(TrackKey other) =>
        Artist == other.Artist
        && Title == other.Title
        && Math.Abs(DurationSeconds - other.DurationSeconds) <= DurationTolerance;

    public static string Normalize(string value)
    {
        var decomposed = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark)
                continue;
            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }

        var collapsed = Whitespace().Replace(sb.ToString(), " ").Trim();
        if (collapsed.StartsWith("the ", StringComparison.Ordinal))
            collapsed = collapsed[4..];
        return collapsed.Replace("ß", "ss").Normalize(NormalizationForm.FormC);
    }

    private static string StripFeaturing(string title) => Featuring().Replace(title, string.Empty);

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\s*[\(\[](feat\.?|ft\.?|featuring)\s[^\)\]]*[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex Featuring();
}

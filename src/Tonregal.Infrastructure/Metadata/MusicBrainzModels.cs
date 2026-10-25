// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text.Json.Serialization;

namespace Tonregal.Infrastructure.Metadata;

// Ausschnitt der MusicBrainz-JSON-Antworten (ws/2, fmt=json), nur die Felder, die Tonregal nutzt.

public sealed record MbArtist(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("sort-name")] string? SortName);

public sealed record MbArtistCredit(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("joinphrase")] string? JoinPhrase,
    [property: JsonPropertyName("artist")] MbArtist Artist);

public sealed record MbReleaseGroup(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("primary-type")] string? PrimaryType,
    [property: JsonPropertyName("secondary-types")] List<string>? SecondaryTypes);

public sealed record MbTrack(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("number")] string? Number,
    [property: JsonPropertyName("position")] int Position,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("length")] int? Length,
    [property: JsonPropertyName("recording")] MbRecording? Recording);

public sealed record MbMedium(
    [property: JsonPropertyName("position")] int Position,
    [property: JsonPropertyName("format")] string? Format,
    [property: JsonPropertyName("track-count")] int TrackCount,
    [property: JsonPropertyName("tracks")] List<MbTrack>? Tracks);

public sealed record MbRelease(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("date")] string? Date,
    [property: JsonPropertyName("country")] string? Country,
    [property: JsonPropertyName("artist-credit")] List<MbArtistCredit>? ArtistCredit,
    [property: JsonPropertyName("release-group")] MbReleaseGroup? ReleaseGroup,
    [property: JsonPropertyName("media")] List<MbMedium>? Media)
{
    /// <summary>Jahr aus "2013", "2013-05" oder "2013-05-17".</summary>
    public int? Year => Date is { Length: >= 4 } d && int.TryParse(d[..4], NumberStyles.None, CultureInfo.InvariantCulture, out var y) ? y : null;
}

public sealed record MbRecording(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("length")] int? Length,
    [property: JsonPropertyName("artist-credit")] List<MbArtistCredit>? ArtistCredit,
    [property: JsonPropertyName("releases")] List<MbRelease>? Releases);

public static class MbArtistCreditExtensions
{
    /// <summary>Anzeigename wie auf dem Release, z. B. "Daft Punk feat. Pharrell Williams".</summary>
    public static string? Format(this List<MbArtistCredit>? credits) =>
        credits is null or [] ? null : string.Concat(credits.Select(c => c.Name + c.JoinPhrase));
}

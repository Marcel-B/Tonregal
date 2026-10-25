// SPDX-License-Identifier: AGPL-3.0-or-later
using Tonregal.Domain.Entities;

namespace Tonregal.Domain.ValueObjects;

public enum TrackIdentityKind
{
    MusicBrainzRecording,
    AcoustId,
    TrackKey,
}

/// <summary>
/// Identität eines Tracks: MusicBrainz-Recording-ID, wenn vorhanden, sonst AcoustID,
/// sonst der <see cref="ValueObjects.TrackKey"/> aus Artist, Titel und Dauer.
/// </summary>
public sealed record TrackIdentity(TrackIdentityKind Kind, string Value)
{
    public static TrackIdentity For(Track track, string artist)
    {
        if (track.MusicBrainzRecordingId is { } mbid)
            return new(TrackIdentityKind.MusicBrainzRecording, mbid.ToString());
        if (track.AcoustId is { } acoustId)
            return new(TrackIdentityKind.AcoustId, acoustId.ToString());

        var key = TrackKey.Create(artist, track.Title, TimeSpan.FromMilliseconds(track.DurationMs));
        return new(TrackIdentityKind.TrackKey, key.TextKey);
    }
}

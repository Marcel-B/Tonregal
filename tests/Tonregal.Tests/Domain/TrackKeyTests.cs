// SPDX-License-Identifier: AGPL-3.0-or-later
using Tonregal.Domain.Entities;
using Tonregal.Domain.ValueObjects;

namespace Tonregal.Tests.Domain;

public class TrackKeyTests
{
    [Fact]
    public void Normalisiert_Schreibweise_Akzente_und_Artikel()
    {
        var a = TrackKey.Create("The Beatles", "Hey Jude!", TimeSpan.FromSeconds(431));
        var b = TrackKey.Create("beatles", "hey  jude", TimeSpan.FromSeconds(431));

        Assert.True(a.Matches(b));
        Assert.Equal("sigur ros", TrackKey.Normalize("Sigur Rós"));
        Assert.Equal("strasse", TrackKey.Normalize("Straße"));
    }

    [Fact]
    public void Ignoriert_Featuring_im_Titel()
    {
        var a = TrackKey.Create("Daft Punk", "Get Lucky (feat. Pharrell Williams)", TimeSpan.FromSeconds(248));
        var b = TrackKey.Create("Daft Punk", "Get Lucky", TimeSpan.FromSeconds(248));

        Assert.True(a.Matches(b));
    }

    [Theory]
    [InlineData(200, 202, true)]
    [InlineData(200, 198, true)]
    [InlineData(200, 203, false)]
    public void Dauer_Toleranz_zwei_Sekunden(int a, int b, bool expected)
    {
        var x = TrackKey.Create("A", "B", TimeSpan.FromSeconds(a));
        var y = TrackKey.Create("A", "B", TimeSpan.FromSeconds(b));

        Assert.Equal(expected, x.Matches(y));
    }

    [Fact]
    public void Identitaet_bevorzugt_MusicBrainz_dann_AcoustId_dann_TrackKey()
    {
        var mbid = Guid.NewGuid();
        var acoustId = Guid.NewGuid();
        var track = new Track { Title = "Song", DurationMs = 180_000, AcoustId = acoustId, MusicBrainzRecordingId = mbid };

        Assert.Equal(new TrackIdentity(TrackIdentityKind.MusicBrainzRecording, mbid.ToString()), TrackIdentity.For(track, "Artist"));

        track.MusicBrainzRecordingId = null;
        Assert.Equal(TrackIdentityKind.AcoustId, TrackIdentity.For(track, "Artist").Kind);

        track.AcoustId = null;
        Assert.Equal(new TrackIdentity(TrackIdentityKind.TrackKey, "artist|song"), TrackIdentity.For(track, "Artist"));
    }
}

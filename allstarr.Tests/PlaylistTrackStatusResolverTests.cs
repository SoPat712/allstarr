using allstarr.Models.Domain;
using allstarr.Models.Spotify;
using allstarr.Services.Admin;

namespace allstarr.Tests;

public class PlaylistTrackStatusResolverTests
{
    [Theory]
    [InlineData("Like I'm Gonna Lose You (feat. John Legend)", "Meghan Trainor", "Like I’m Gonna Lose You", "Meghan Trainor")]
    [InlineData("I know love (feat. The Kid LAROI)", "Tate McRae", "I know love", "Tate McRae")]
    public void MaterializedIdentityMatches_FeaturingDecoratorDifferences_ReturnsTrue(
        string sourceTitle,
        string sourceArtist,
        string materializedTitle,
        string materializedArtist)
    {
        var matches = PlaylistTrackStatusResolver.MaterializedIdentityMatches(
            sourceTitle,
            sourceArtist,
            materializedTitle,
            [materializedArtist]);

        Assert.True(matches);
    }

    [Fact]
    public void MaterializedIdentityMatches_DifferentPrimaryArtist_ReturnsFalse()
    {
        var matches = PlaylistTrackStatusResolver.MaterializedIdentityMatches(
            "Same title (feat. Guest)",
            "Artist A",
            "Same title",
            ["Artist B"]);

        Assert.False(matches);
    }

    [Fact]
    public void TryResolveFromMatchedTrack_LocalMatch_ReturnsLocal()
    {
        var matchedBySpotifyId = new Dictionary<string, MatchedTrack>(StringComparer.OrdinalIgnoreCase)
        {
            ["1UNWD6R5EOFklUHKZZvww2"] = new MatchedTrack
            {
                SpotifyId = "1UNWD6R5EOFklUHKZZvww2",
                MatchedSong = new Song
                {
                    IsLocal = true
                }
            }
        };

        var resolved = PlaylistTrackStatusResolver.TryResolveFromMatchedTrack(
            matchedBySpotifyId,
            "1UNWD6R5EOFklUHKZZvww2",
            out var isLocal,
            out var externalProvider);

        Assert.True(resolved);
        Assert.True(isLocal);
        Assert.Null(externalProvider);
    }

    [Fact]
    public void TryResolveFromMatchedTrack_MetadataOnlyMatch_IsTreatedAsMissing()
    {
        var matchedBySpotifyId = new Dictionary<string, MatchedTrack>(StringComparer.OrdinalIgnoreCase)
        {
            ["6zSpb8dQRaw0M1dK8PBwQz"] = new MatchedTrack
            {
                SpotifyId = "6zSpb8dQRaw0M1dK8PBwQz",
                MatchedSong = new Song
                {
                    IsLocal = false,
                    ExternalProvider = "squidwtf"
                }
            }
        };

        var resolved = PlaylistTrackStatusResolver.TryResolveFromMatchedTrack(
            matchedBySpotifyId,
            "6zspb8dqraw0m1dk8pbwqz",
            out var isLocal,
            out var externalProvider);

        Assert.False(resolved);
        Assert.Null(isLocal);
        Assert.Null(externalProvider);
    }

    [Fact]
    public void TryResolveFromMatchedTrack_NoMatch_ReturnsFalse()
    {
        var matchedBySpotifyId = new Dictionary<string, MatchedTrack>(StringComparer.OrdinalIgnoreCase)
        {
            ["abc"] = new MatchedTrack
            {
                SpotifyId = "abc",
                MatchedSong = new Song { IsLocal = true }
            }
        };

        var resolved = PlaylistTrackStatusResolver.TryResolveFromMatchedTrack(
            matchedBySpotifyId,
            "def",
            out var isLocal,
            out var externalProvider);

        Assert.False(resolved);
        Assert.Null(isLocal);
        Assert.Null(externalProvider);
    }

    [Fact]
    public void TryResolveFromMatchedTrack_NullMatchedSong_ReturnsFalse()
    {
        var matchedBySpotifyId = new Dictionary<string, MatchedTrack>(StringComparer.OrdinalIgnoreCase)
        {
            ["abc"] = new MatchedTrack
            {
                SpotifyId = "abc",
                MatchedSong = null!
            }
        };

        var resolved = PlaylistTrackStatusResolver.TryResolveFromMatchedTrack(
            matchedBySpotifyId,
            "abc",
            out var isLocal,
            out var externalProvider);

        Assert.False(resolved);
        Assert.Null(isLocal);
        Assert.Null(externalProvider);
    }
}

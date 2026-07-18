using allstarr.Models.Domain;
using allstarr.Models.Spotify;
using allstarr.Services.Spotify;

namespace allstarr.Tests;

public sealed class LegacyPlaylistMatchRecoveryTests
{
    [Fact]
    public void ReconstructExact_PreservesSpotifyOrderAndPlayableIdentity()
    {
        var source = new[]
        {
            Track("spotify-b", 4, "Second", "Artist B"),
            Track("spotify-a", 1, "First", "Artist A")
        };
        var songs = new[]
        {
            Song("deezer", "22", "Second", "Artist B"),
            Song("deezer", "11", "First", "Artist A")
        };

        var result = LegacyPlaylistMatchRecovery.ReconstructExact(source, songs);

        Assert.Equal(["spotify-a", "spotify-b"], result.Select(item => item.SpotifyId));
        Assert.Equal([1, 4], result.Select(item => item.Position));
        Assert.All(result, item => Assert.Equal("legacy-exact-identity", item.MatchType));
    }

    [Fact]
    public void ReconstructExact_SkipsAmbiguousOrUnavailableSongs()
    {
        var source = new[]
        {
            Track("duplicate-a", 0, "Same", "Artist"),
            Track("duplicate-b", 1, "Same", "Artist"),
            Track("blocked", 2, "Blocked", "Artist")
        };
        var songs = new[]
        {
            Song("deezer", "1", "Same", "Artist"),
            Song("squidwtf", "2", "Blocked", "Artist")
        };

        Assert.Empty(LegacyPlaylistMatchRecovery.ReconstructExact(source, songs));
    }

    private static SpotifyPlaylistTrack Track(string id, int position, string title, string artist) => new()
    {
        SpotifyId = id,
        Position = position,
        Title = title,
        Artists = [artist]
    };

    private static Song Song(string provider, string id, string title, string artist) => new()
    {
        Id = $"ext-{provider}-song-{id}",
        ExternalId = id,
        ExternalProvider = provider,
        IsLocal = false,
        Title = title,
        Artist = artist
    };
}

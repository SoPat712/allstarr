using allstarr.Models.Settings;

namespace allstarr.Tests;

public sealed class SpotifyPlaylistConfigParserTests
{
    [Fact]
    public void Serialize_RoundTripsCurrentPlaylistShape()
    {
        var original = new SpotifyPlaylistConfig
        {
            Name = "Release Radar",
            Id = "spotify-id",
            JellyfinId = "jellyfin-id",
            LocalTracksPosition = LocalTracksPosition.Last,
            SyncSchedule = "0 9 * * 5",
            UserId = "user-id"
        };

        var parsed = Assert.Single(SpotifyPlaylistConfigParser.Parse(
            SpotifyPlaylistConfigParser.Serialize([original])));

        Assert.Equal(original.Name, parsed.Name);
        Assert.Equal(original.Id, parsed.Id);
        Assert.Equal(original.JellyfinId, parsed.JellyfinId);
        Assert.Equal(original.LocalTracksPosition, parsed.LocalTracksPosition);
        Assert.Equal(original.SyncSchedule, parsed.SyncSchedule);
        Assert.Equal(original.UserId, parsed.UserId);
    }
}

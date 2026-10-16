using allstarr.Services.Admin;

namespace allstarr.Tests;

public sealed class ActivityPresentationTests
{
    [Theory]
    [InlineData("library.index", "Jellyfin library")]
    [InlineData("playlist.materialize", "Playlists")]
    [InlineData("playlist.retain-track", "Playlists")]
    [InlineData("playback.signal", "Listening")]
    [InlineData("favorite.process", "Favorites")]
    [InlineData("favorite.display-backfill", "Favorites")]
    [InlineData("recording.kept-cleanup", "Recordings")]
    [InlineData("recording.keep", "Recordings")]
    [InlineData("recording.musicbrainz.enrich", "Recordings")]
    [InlineData("recording.odesli.enrich", "Recordings")]
    [InlineData("track-match.rematch", "Matching")]
    [InlineData("recommendation.generate", "Recommendations")]
    [InlineData("future.operation", "System")]
    public void JobsHaveReadableSources(string type, string expected) =>
        Assert.Equal(expected, ActivityPresentation.JobSource(type, "jellyfin"));

    [Theory]
    [InlineData("navidrome", "Navidrome library")]
    [InlineData("subsonic", "Subsonic library")]
    [InlineData("opensubsonic", "Subsonic library")]
    [InlineData(null, "Local library")]
    public void LibraryNamesUseTheBackend(string? backend, string expected) =>
        Assert.Equal(expected, ActivityPresentation.LibraryName(backend));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("00000000000000000000000000000001")]
    [InlineData("00000000-0000-0000-0000-000000000001")]
    public void MissingNamesNeverExposeRawIds(string? name) =>
        Assert.Equal("Unknown user", ActivityPresentation.UserName(name));
}

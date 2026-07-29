using allstarr.Services.Jellyfin;
using Microsoft.AspNetCore.Http;

namespace allstarr.Tests;

public sealed class JellyfinMusicEndpointPolicyTests
{
    [Theory]
    [InlineData("GET", "/Artists")]
    [InlineData("GET", "/MusicGenres/Rock")]
    [InlineData("GET", "/Playlists/playlist-id/Items")]
    [InlineData("GET", "/Search/Hints?SearchTerm=radiohead")]
    [InlineData("POST", "/Sessions/Playing")]
    [InlineData("GET", "/System/Info/Public")]
    [InlineData("GET", "/socket?api_key=redacted")]
    [InlineData("GET", "/Items/Latest")]
    [InlineData("GET", "/Items/Suggestions")]
    [InlineData("GET", "/Items/Filters")]
    [InlineData("GET", "/Genres")]
    [InlineData("GET", "/UserViews")]
    [InlineData("GET", "/Library/MediaFolders")]
    [InlineData("GET", "/Items/Root")]
    [InlineData("GET", "/Items/Counts")]
    [InlineData("GET", "/UserItems/Resume")]
    [InlineData("GET", "/Genres/Rock")]
    [InlineData("GET", "/Users/user-id")]
    [InlineData("HEAD", "/Users/user-id")]
    public void Evaluate_AllowsMusicAndRequiredClientRoutes(string method, string target)
    {
        var decision = Evaluate(method, target);

        Assert.True(decision.Allowed, decision.Reason);
    }

    [Theory]
    [InlineData("GET", "/Videos/video-id/stream")]
    [InlineData("GET", "/Movies/movie-id/Similar")]
    [InlineData("GET", "/Shows/show-id/Similar")]
    [InlineData("GET", "/System/Logs")]
    [InlineData("POST", "/ScheduledTasks/task-id")]
    [InlineData("POST", "/Users/New")]
    [InlineData("GET", "/Sessions")]
    [InlineData("POST", "/Sessions/session-id/Command/PlayPause")]
    [InlineData("GET", "/Items/Latest?IncludeItemTypes=Movie,Audio")]
    [InlineData("POST", "/Items/RemoteSearch/Movie")]
    [InlineData("DELETE", "/Items/local-audio-id")]
    [InlineData("POST", "/Items/local-audio-id/Refresh")]
    [InlineData("GET", "/Items/Suggestions?Type=Audio,Movie")]
    [InlineData("GET", "/Genres?IncludeItemTypes=Audio,Series")]
    [InlineData("GET", "/Search/Hints?IncludeItemTypes=Movie")]
    public void Evaluate_DeniesVideoAdminAndBroadControlRoutes(string method, string target)
    {
        var decision = Evaluate(method, target);

        Assert.Equal(JellyfinEndpointAccess.Denied, decision.Access);
    }

    [Theory]
    [InlineData("GET", "/Items/local-audio-id")]
    [InlineData("GET", "/Items/local-audio-id/Images/Primary")]
    [InlineData("GET", "/Items/local-audio-id/PlaybackInfo")]
    [InlineData("GET", "/Audio/local-audio-id/stream")]
    [InlineData("POST", "/UserFavoriteItems/local-audio-id")]
    [InlineData("POST", "/Items/local-audio-id/PlaybackInfo")]
    [InlineData("POST", "/Users/user-id/FavoriteItems/local-audio-id")]
    public void Evaluate_RequiresSemanticValidationForOpaqueItemRoutes(string method, string target)
    {
        var decision = Evaluate(method, target);

        Assert.Equal(JellyfinEndpointAccess.RequiresMusicItem, decision.Access);
        Assert.Equal("local-audio-id", JellyfinMusicEndpointPolicy.ReferencedItemId(new Uri("http://localhost" + target).AbsolutePath));
    }

    [Fact]
    public void Evaluate_AllowsUntypedItemsOnlyBecauseControllerConstrainsItToMusic()
    {
        Assert.Equal(JellyfinEndpointAccess.Music, Evaluate("GET", "/Items").Access);
        Assert.Equal(
            JellyfinEndpointAccess.Denied,
            Evaluate("GET", "/Items?IncludeItemTypes=Audio,Movie").Access);
    }

    [Theory]
    [InlineData("Audio", true)]
    [InlineData("MusicAlbum", true)]
    [InlineData("MusicArtist", true)]
    [InlineData("Playlist", true)]
    [InlineData("MusicGenre", true)]
    [InlineData("Movie", false)]
    [InlineData("Series", false)]
    [InlineData("Episode", false)]
    [InlineData("MusicVideo", false)]
    public void IsMusicItemType_UsesAudioLibraryTypesOnly(string itemType, bool expected)
    {
        Assert.Equal(expected, JellyfinMusicEndpointPolicy.IsMusicItemType(itemType));
    }

    [Theory]
    [InlineData("ext-spotify-song-123", true)]
    [InlineData("vplaylist-legacy", true)]
    [InlineData("allstarr-vpl-0198a537719c7ea89e5a17e1f2f963f0", true)]
    [InlineData("opaque-jellyfin-id", false)]
    public void SynthesizedMusicItemIds_BypassBackendTypeLookup(string itemId, bool expected)
    {
        Assert.Equal(expected, JellyfinMusicEndpointPolicy.IsSynthesizedMusicItemId(itemId));
    }

    private static JellyfinEndpointDecision Evaluate(string method, string target)
    {
        var uri = new Uri("http://localhost" + target);
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = uri.AbsolutePath;
        context.Request.QueryString = new QueryString(uri.Query);
        return JellyfinMusicEndpointPolicy.Evaluate(context.Request);
    }
}

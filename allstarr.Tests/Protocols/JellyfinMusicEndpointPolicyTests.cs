using allstarr.Services.Jellyfin;
using Microsoft.AspNetCore.Http;

namespace allstarr.Tests;

public sealed class JellyfinMusicEndpointPolicyTests
{
    [Theory]
    [InlineData("GET", "/Artists")]
    [InlineData("GET", "/MusicGenres/Rock")]
    [InlineData("GET", "/MusicGenres")]
    [InlineData("GET", "/Playlists/playlist-id/Items")]
    [InlineData("GET", "/Search/Hints?SearchTerm=radiohead")]
    [InlineData("POST", "/Sessions/Playing")]
    [InlineData("GET", "/System/Info/Public")]
    [InlineData("POST", "/System/Ping")]
    [InlineData("GET", "/System/Endpoint")]
    [InlineData("GET", "/GetUtcTime")]
    [InlineData("GET", "/socket?api_key=redacted")]
    [InlineData("GET", "/Items/Latest")]
    [InlineData("GET", "/Items/Suggestions")]
    [InlineData("GET", "/Items/Filters")]
    [InlineData("GET", "/Genres")]
    [InlineData("GET", "/UserViews")]
    [InlineData("GET", "/Users/user-id/Views")]
    [InlineData("GET", "/UserViews/GroupingOptions")]
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
    [InlineData("POST", "/Items/local-audio-id/Refresh")]
    [InlineData("GET", "/Items/Suggestions?Type=Audio,Movie")]
    [InlineData("GET", "/Genres?IncludeItemTypes=Audio,Series")]
    [InlineData("GET", "/Search/Hints?IncludeItemTypes=Movie")]
    [InlineData("POST", "/Audio/local-audio-id/Lyrics")]
    [InlineData("DELETE", "/Audio/local-audio-id/Lyrics")]
    [InlineData("POST", "/Audio/local-audio-id/RemoteSearch/Lyrics/provider-result")]
    [InlineData("GET", "/web/ConfigurationPage")]
    [InlineData("GET", "/web/ConfigurationPages")]
    [InlineData("DELETE", "/QuickConnect/Enabled")]
    [InlineData("GET", "/Playlists/playlist-id/Unreviewed")]
    [InlineData("GET", "/Providers/Lyrics/lyric-id/Unreviewed")]
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
    [InlineData("POST", "/PlayingItems/local-audio-id")]
    [InlineData("POST", "/PlayingItems/local-audio-id/Progress")]
    [InlineData("DELETE", "/PlayingItems/local-audio-id")]
    [InlineData("GET", "/Audio/local-audio-id/master.m3u8")]
    public void Evaluate_RequiresSemanticValidationForOpaqueItemRoutes(string method, string target)
    {
        var decision = Evaluate(method, target);

        Assert.Equal(JellyfinEndpointAccess.RequiresMusicItem, decision.Access);
        Assert.Equal("local-audio-id", JellyfinMusicEndpointPolicy.ReferencedItemId(new Uri("http://localhost" + target).AbsolutePath));
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("DELETE")]
    public void Evaluate_RequiresPlaylistValidationBeforeMutatingLibraryItems(string method)
    {
        var decision = Evaluate(method, "/Items/local-playlist-id");

        Assert.Equal(JellyfinEndpointAccess.RequiresPlaylistItem, decision.Access);
        Assert.Equal(
            "local-playlist-id",
            JellyfinMusicEndpointPolicy.ReferencedItemId("/Items/local-playlist-id"));
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

    [Theory]
    [InlineData("GET", "/Items/ext-provider-song-1", true)]
    [InlineData("GET", "/Items/ext-provider-song-1/Images/Primary", true)]
    [InlineData("HEAD", "/Items/ext-provider-song-1/Images/Primary/0/tag/png/300/300/0/0", true)]
    [InlineData("GET", "/Items/ext-provider-song-1/Images/Backdrop", false)]
    [InlineData("GET", "/Items/ext-provider-song-1/Images/Primary/0/tag/png/300/300/20/0", false)]
    [InlineData("GET", "/Items/ext-provider-song-1/Images/Primary/0/tag/gif/300/300/0/0", false)]
    [InlineData("POST", "/Items/ext-provider-song-1/PlaybackInfo", true)]
    [InlineData("HEAD", "/Audio/ext-provider-song-1/stream.flac", true)]
    [InlineData("POST", "/UserFavoriteItems/ext-provider-song-1", true)]
    [InlineData("GET", "/Items/ext-provider-album-1/PlaybackInfo", false)]
    [InlineData("GET", "/Items/ext-provider-album-1/InstantMix", false)]
    [InlineData("GET", "/Items/ext-provider-artist-1/Similar", true)]
    [InlineData("GET", "/Audio/ext-provider-playlist-1/Lyrics", false)]
    [InlineData("POST", "/UserFavoriteItems/allstarr-vpl-0198a537719c7ea89e5a17e1f2f963f0", false)]
    [InlineData("GET", "/Items/ext-provider-song-1/Ancestors", false)]
    [InlineData("GET", "/Items/ext-provider-song-1/Images", false)]
    [InlineData("GET", "/Items/ext-provider-song-1/RemoteImages", false)]
    [InlineData("POST", "/UserItems/ext-provider-song-1/UserData", false)]
    [InlineData("POST", "/UserPlayedItems/ext-provider-song-1", false)]
    [InlineData("POST", "/PlayingItems/ext-provider-song-1", false)]
    public void SynthesizedMusicItems_OnlyUseImplementedClientRoutes(
        string method,
        string target,
        bool expected)
    {
        var uri = new Uri("http://localhost" + target);
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = uri.AbsolutePath;

        Assert.Equal(
            expected,
            JellyfinMusicEndpointPolicy.SupportsSynthesizedItemRoute(
                context.Request,
                JellyfinMusicEndpointPolicy.ReferencedItemId(uri.AbsolutePath)!));
    }

    [Theory]
    [InlineData("GET", "/Artists/ext-provider-artist-1", true)]
    [InlineData("GET", "/Artists/ext-provider-artist-1/InstantMix", true)]
    [InlineData("GET", "/Artists/ext-provider-artist-1/Similar", true)]
    [InlineData("GET", "/Artists/ext-provider-artist-1/Images/Primary/0", false)]
    [InlineData("GET", "/Albums/ext-provider-album-1/InstantMix", true)]
    [InlineData("GET", "/Albums/ext-provider-album-1/Similar", false)]
    [InlineData("GET", "/Songs/ext-provider-song-1/InstantMix", true)]
    [InlineData("GET", "/MusicGenres/ext-provider-genre-1/InstantMix", false)]
    public void SynthesizedTypedResources_NeverLeakIntoNativeRoutes(
        string method,
        string target,
        bool expected)
    {
        var uri = new Uri("http://localhost" + target);
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = uri.AbsolutePath;
        var itemId = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)[1];

        Assert.Equal(
            expected,
            JellyfinMusicEndpointPolicy.SupportsSynthesizedItemRoute(context.Request, itemId));
        Assert.Equal(
            expected ? JellyfinEndpointAccess.Music : JellyfinEndpointAccess.Denied,
            JellyfinMusicEndpointPolicy.Evaluate(context.Request).Access);
    }

    [Theory]
    [InlineData("GET", "/Playlists/ext-provider-playlist-1", true)]
    [InlineData("GET", "/Playlists/allstarr-vpl-0198a537719c7ea89e5a17e1f2f963f0/Items", true)]
    [InlineData("GET", "/Playlists/ext-provider-playlist-1/Users", true)]
    [InlineData("GET", "/Playlists/ext-provider-playlist-1/Users/user-1", true)]
    [InlineData("GET", "/Playlists/ext-provider-playlist-1/InstantMix", true)]
    [InlineData("POST", "/Playlists/ext-provider-playlist-1", true)]
    [InlineData("POST", "/Playlists/ext-provider-playlist-1/Items", true)]
    [InlineData("DELETE", "/Playlists/allstarr-vpl-0198a537719c7ea89e5a17e1f2f963f0/Items", true)]
    [InlineData("POST", "/Playlists/ext-provider-playlist-1/Items/item-1/Move/2", true)]
    [InlineData("POST", "/Playlists/ext-provider-playlist-1/Users/user-1", true)]
    [InlineData("DELETE", "/Playlists/ext-provider-playlist-1/Users/user-1", true)]
    [InlineData("GET", "/Playlists/ext-provider-playlist-1/Unreviewed", false)]
    public void SynthesizedPlaylists_AllowOnlyReviewedRoutesForTargetResolution(
        string method,
        string target,
        bool expected)
    {
        var uri = new Uri("http://localhost" + target);
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = uri.AbsolutePath;
        var playlistId = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)[1];

        Assert.Equal(
            expected,
            JellyfinMusicEndpointPolicy.SupportsSynthesizedPlaylistRoute(
                context.Request, playlistId));
        Assert.Equal(
            expected ? JellyfinEndpointAccess.Music : JellyfinEndpointAccess.Denied,
            JellyfinMusicEndpointPolicy.Evaluate(context.Request).Access);
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

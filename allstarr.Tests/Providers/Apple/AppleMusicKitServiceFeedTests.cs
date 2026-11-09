using System.Net;
using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Core.Storage;
using allstarr.Core.Providers.AppleMusicKit;
using allstarr.Core.Providers.Spotify;

namespace allstarr.Tests;

public sealed class AppleMusicKitServiceFeedTests
{
    [Fact]
    public async Task Recommendations_and_library_expose_only_service_curated_playlists()
    {
        var paths = new List<string>();
        using var http = Transport(paths, request => request.RequestUri!.AbsolutePath.EndsWith("recommendations")
            ? """
              {"data":[{"id":"group","type":"personal-recommendations","relationships":{"contents":{"data":[
                {"id":"pl.mix","type":"playlists","attributes":{"name":"New Music Mix","curatorName":"Apple Music"}},
                {"id":"album","type":"albums","attributes":{"name":"Album","curatorName":"Apple Music"}},
                {"id":"station","type":"stations","attributes":{"name":"Station","curatorName":"Apple Music"}},
                {"id":"pl.editorial","type":"playlists","attributes":{"name":"An editorial selection","curatorName":"Another Curator"}},
                {"id":"pl.unknown","type":"playlists","attributes":{"name":"Favorites Mix"}},
                {"id":"nested","type":"personal-recommendations","relationships":{"contents":{"data":[
                  {"id":"pl.chill","type":"playlists","attributes":{"name":"Chill Mix","curatorName":"Apple Music"}}
                ]}}}
              ]}}}],"included":[{"id":"pl.mix","type":"playlists","attributes":{"name":"New Music Mix","curatorName":"Apple Music"}}]}
              """
            : """
              {"data":[
                {"id":"p.mix","type":"library-playlists","attributes":{"name":"Favorites Mix","curatorName":"Apple Music"}},
                {"id":"p.own","type":"library-playlists","attributes":{"name":"My playlist"}}
              ]}
              """);
        var adapter = new AppleMusicKitPlaylistCapabilityAdapter(AppleProviderTestFactory.Client(http), http);

        var result = await adapter.GetServiceFeedsAsync(Context(), new());

        Assert.True(result.IsSuccess, result.Error?.Code);
        Assert.Equal(new[] { "p.mix", "pl.chill", "pl.mix" }, result.RequireValue().Items.Select(item => item.Id.Value));
        Assert.All(result.RequireValue().Items, item => Assert.Equal("Apple Music", item.Owner.ProviderUserId));
        Assert.Null(result.RequireValue().NextCursor);
        Assert.Contains(paths, path => path.StartsWith("/v1/me/recommendations?", StringComparison.Ordinal) && path.Contains("include=contents", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Source_and_nested_pages_are_deduplicated_before_result_pagination()
    {
        var paths = new List<string>();
        using var http = Transport(paths, request => request.RequestUri!.PathAndQuery switch
        {
            "/v1/me/recommendations?offset=25" => """{"data":[{"id":"pl.a","type":"playlists","attributes":{"name":"Mix A","curatorName":"Apple Music"}}]}""",
            "/v1/me/recommendations/group/contents?offset=25" => """{"data":[{"id":"pl.b","type":"playlists","attributes":{"name":"Mix B","curatorName":"Apple Music"}}]}""",
            "/v1/me/library/playlists?offset=25" => """{"data":[{"id":"p.d","type":"library-playlists","attributes":{"name":"Mix D","curatorName":"Apple Music"}}]}""",
            var path when path.StartsWith("/v1/me/recommendations?", StringComparison.Ordinal) => """
              {"next":"/v1/me/recommendations?offset=25","data":[{"id":"group","type":"personal-recommendations","relationships":{"contents":{
                "next":"/v1/me/recommendations/group/contents?offset=25","data":[
                  {"id":"pl.a","type":"playlists","attributes":{"name":"Mix A","curatorName":"Apple Music"}}
              ]}}}],"included":[{"id":"group","type":"personal-recommendations","relationships":{"contents":{
                "next":"/v1/me/recommendations/group/contents?offset=25","data":[]
              }}}]}
              """,
            _ => """{"next":"/v1/me/library/playlists?offset=25","data":[{"id":"p.c","type":"library-playlists","attributes":{"name":"Mix C","curatorName":"Apple Music"}}]}"""
        });
        var adapter = new AppleMusicKitPlaylistCapabilityAdapter(AppleProviderTestFactory.Client(http), http);

        var first = (await adapter.GetServiceFeedsAsync(Context(), new(2))).RequireValue();
        var second = (await adapter.GetServiceFeedsAsync(Context(), new(2, first.NextCursor))).RequireValue();

        Assert.Equal(new[] { "p.c", "p.d" }, first.Items.Select(item => item.Id.Value));
        Assert.Equal("2", first.NextCursor);
        Assert.True(first.IsPartial);
        Assert.Equal(new[] { "pl.a", "pl.b" }, second.Items.Select(item => item.Id.Value));
        Assert.Null(second.NextCursor);
        Assert.False(second.IsPartial);
        Assert.Equal(10, paths.Count);
    }

    [Theory]
    [InlineData("https://other.example/v1/me/recommendations?offset=25")]
    [InlineData("/v1/catalog/us/playlists?offset=25")]
    [InlineData("/v1/me/recommendations?include=contents&limit=25")]
    public async Task Unsafe_or_repeating_continuations_fail_without_following_them(string next)
    {
        var paths = new List<string>();
        using var http = Transport(paths, _ => "{\"data\":[],\"next\":\"" + next + "\"}");
        var adapter = new AppleMusicKitPlaylistCapabilityAdapter(AppleProviderTestFactory.Client(http), http);

        var result = await adapter.GetServiceFeedsAsync(Context(), new());

        Assert.False(result.IsSuccess);
        Assert.Single(paths);
    }

    [Fact]
    public async Task Library_summary_preserves_curator_and_own_playlist_fallback()
    {
        using var http = Transport([], _ => """
          {"data":[
            {"id":"p.mix","type":"library-playlists","attributes":{"name":"Mix","curatorName":"Apple Music"}},
            {"id":"p.own","type":"library-playlists","attributes":{"name":"Own","curatorName":"Listener"}}
          ]}
          """);
        var adapter = new AppleMusicKitPlaylistCapabilityAdapter(AppleProviderTestFactory.Client(http), http);

        var result = (await adapter.GetUserPlaylistsAsync(Context(), new(new()))).RequireValue();

        Assert.Equal("Apple Music", result.Items[0].Owner.ProviderUserId);
        Assert.Equal("selected-user", result.Items[1].Owner.ProviderUserId);
    }

    private static HttpClient Transport(List<string> paths, Func<HttpRequestMessage, string> body) => new(
        new AppleProviderTestFactory.Handler(request =>
        {
            paths.Add(request.RequestUri!.PathAndQuery);
            Assert.Equal(AppleMusicClient.ApiOrigin.Host, request.RequestUri.Host);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal(AppleProviderTestFactory.Bearer, request.Headers.Authorization.Parameter);
            Assert.Equal("media-user-token=fixture-user", request.Headers.GetValues("Cookie").Single());
            Assert.Equal("https://music.apple.com", request.Headers.GetValues("Origin").Single());
            return new(HttpStatusCode.OK) { Content = new StringContent(body(request), Encoding.UTF8, "application/json") };
        }));

    private static ProviderExecutionContext Context()
    {
        var user = Guid.NewGuid();
        return new(new(ProviderActorKind.User, user, new("jellyfin", "backend", "fixture")), "apple-musickit",
            new(Guid.NewGuid(), "apple-musickit", ProviderAccountScope.Personal, 1,
                ownerUserId: user, secretReferenceId: Guid.NewGuid()),
            new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true), ProviderExplicitContentPolicy.Allow,
                true, true, false, ["apple-musickit"]), "playlist-read", "fixture", DateTimeOffset.UtcNow.AddMinutes(1), default);
    }
}

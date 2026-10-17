using System.Net;
using System.Text;
using System.Text.Json;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using Moq;

namespace allstarr.Tests;

public sealed class JellyfinMatchLibraryClientTests
{
    [Fact]
    public async Task Searches_use_viewer_authentication_library_scope_and_artist_audio_lookup()
    {
        var handler = new LibraryHandler();
        var permissions = new Mock<IBackendLibraryPermissionSource>();
        permissions.Setup(item => item.CreateRequestAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, "https://library.test/UserViews?UserId=listener");
                request.Headers.TryAddWithoutValidation("Authorization", "viewer-fixture");
                return request;
            });
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(item => item.CreateClient(JellyfinMatchLibraryClient.HttpClientName)).Returns(() => new HttpClient(handler, disposeHandler: false));
        var client = new JellyfinMatchLibraryClient(clients.Object, permissions.Object);
        var user = Guid.NewGuid();
        var context = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "server", "listener",
            new AllstarrPrincipal(user, "jellyfin", "server", "listener", "Listener", false), "search", DateTimeOffset.UtcNow.AddMinutes(1), default);

        Assert.Empty(await client.SearchAsync(context, "music", "Silk & Cologne Ei8ht", default));
        Assert.Equal("song", Assert.Single(await client.SearchAsync(context, "music", "Silk & Cologne", default)).BackendItemId);
        var artistSong = Assert.Single(await client.ArtistSongsAsync(context, "music", "Ei8ht", default));
        Assert.Equal("Silk & Cologne", artistSong.Title);
        Assert.Equal(180000, artistSong.DurationMilliseconds);
        Assert.Equal("US1234567890", artistSong.Isrc);
        Assert.Single(await client.IsrcAsync(context, "music", "US1234567890", default));
        Assert.All(handler.Requests, request =>
        {
            Assert.Contains("ParentId=music", request);
            Assert.Contains("UserId=listener", request);
        });
        Assert.Contains(handler.Requests, request => request.Contains("ArtistIds=artist", StringComparison.Ordinal));
        Assert.Contains(handler.Requests, request => request.Contains("AnyProviderIdEquals=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Artist_lookup_reads_songs_after_the_first_page()
    {
        var handler = new LibraryHandler { FullFirstPage = true };
        var permissions = new Mock<IBackendLibraryPermissionSource>();
        permissions.Setup(item => item.CreateRequestAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, "https://library.test/UserViews");
                request.Headers.TryAddWithoutValidation("Authorization", "viewer-fixture");
                return request;
            });
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(item => item.CreateClient(JellyfinMatchLibraryClient.HttpClientName)).Returns(() => new HttpClient(handler, disposeHandler: false));
        var context = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "server", "listener",
            new AllstarrPrincipal(Guid.NewGuid(), "jellyfin", "server", "listener", "Listener", false), "search", DateTimeOffset.UtcNow.AddMinutes(1), default);
        var songs = await new JellyfinMatchLibraryClient(clients.Object, permissions.Object).ArtistSongsAsync(context, "music", "Ei8ht", default);

        Assert.Contains(songs, item => item.BackendItemId == "song");
        Assert.Contains(handler.Requests, request => request.Contains("StartIndex=200", StringComparison.Ordinal));
    }

    private sealed class LibraryHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public bool FullFirstPage { get; init; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("viewer-fixture", request.Headers.GetValues("Authorization").Single());
            var uri = request.RequestUri!;
            Requests.Add(uri.PathAndQuery);
            var query = Uri.UnescapeDataString(uri.Query);
            var body = uri.AbsolutePath.EndsWith("/Artists", StringComparison.Ordinal)
                ? """{"Items":[{"Id":"artist","Name":"Ei8ht"}]}"""
                : query.Contains("SearchTerm=Silk & Cologne Ei8ht", StringComparison.Ordinal)
                    ? """{"Items":[]}"""
                    : JsonSerializer.Serialize(new { Items = new[] { new { Id = "song", Name = "Silk & Cologne", Path = "/music/song.flac", Artists = new[] { "Ei8ht" }, RunTimeTicks = 1800000000L, ProviderIds = new { ISRC = "US1234567890" } } } });
            if (FullFirstPage && query.Contains("ArtistIds=", StringComparison.Ordinal) && !query.Contains("StartIndex=200", StringComparison.Ordinal))
                body = JsonSerializer.Serialize(new
                {
                    Items = Enumerable.Range(0, 200).Select(number => new
                    { Id = $"unrelated-{number}", Name = "Unrelated", Path = $"/music/{number}.flac", Artists = new[] { "Ei8ht" } })
                });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}

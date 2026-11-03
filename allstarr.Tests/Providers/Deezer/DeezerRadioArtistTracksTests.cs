using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Deezer;

namespace allstarr.Tests;

public sealed class DeezerRadioArtistTracksTests
{
    private const string TracksJson = """
        {"data":[
          {"id":111,"title":"First song","duration":180,"isrc":"USABC1234567",
           "artist":{"id":27,"name":"Artist"},"album":{"id":99,"title":"Album"}},
          {"id":112,"title":"Second song","artist":{"id":27,"name":"Artist"}}
        ],"total":100}
        """;

    [Theory]
    [InlineData(20, null, 0, "2")]
    [InlineData(5, "7", 7, "9")]
    public async Task RadioArtistTracksRequestsOnlyTheRequestedPage(int limit, string? cursor, int offset, string expectedNextCursor)
    {
        using var handler = new FixtureHandler();
        using var client = new HttpClient(handler);
        var provider = CreateProvider(client);

        var result = await provider.GetArtistTracksAsync(PublicContext(),
            new(new("deezer", ProviderResourceKind.Artist, "27"), new(limit, cursor)));

        var page = result.RequireValue();
        Assert.Equal($"/artist/27/top?index={offset}&limit={limit}", Assert.Single(handler.Requests));
        Assert.Equal("deezer", page.ProviderId);
        Assert.Equal(["111", "112"], page.Items.Select(track => track.Id.Value));
        var first = page.Items[0];
        Assert.Equal(ProviderResourceKind.Track, first.Id.ResourceKind);
        Assert.Equal("deezer", first.Id.ProviderId);
        Assert.Equal("First song", first.Title);
        var credit = Assert.Single(first.Artists);
        Assert.Equal("Artist", credit.Name);
        Assert.Equal("27", credit.ArtistId!.Value);
        Assert.Equal("99", first.AlbumId!.Value);
        Assert.Equal("Album", first.AlbumTitle);
        Assert.Equal(TimeSpan.FromSeconds(180), first.Duration);
        Assert.Equal("USABC1234567", first.Isrc);
        Assert.Equal(expectedNextCursor, page.NextCursor);
        Assert.True(page.IsPartial);
    }

    [Fact]
    public async Task RadioArtistTracksAdvancesPastUnusableRecordsWithoutReadingAnotherPage()
    {
        using var handler = new FixtureHandler("""
            {"data":[{"id":111,"title":"","artist":{"id":27,"name":"Artist"}},
                     {"id":112,"title":"Second song","artist":{"id":27,"name":"Artist"}}],"total":3}
            """);
        using var client = new HttpClient(handler);
        var provider = CreateProvider(client);

        var page = (await provider.GetArtistTracksAsync(PublicContext(),
            new(new("deezer", ProviderResourceKind.Artist, "27"), new(5)))).RequireValue();

        Assert.Equal("/artist/27/top?index=0&limit=5", Assert.Single(handler.Requests));
        Assert.Equal("112", Assert.Single(page.Items).Id.Value);
        Assert.Equal("2", page.NextCursor);
        Assert.True(page.IsPartial);
    }

    [Theory]
    [InlineData("-1", null)]
    [InlineData("unknown", null)]
    [InlineData("2147483647", null)]
    [InlineData(null, "snapshot")]
    public async Task RadioArtistTracksRejectsUnsupportedPagingWithoutHttp(string? cursor, string? snapshot)
    {
        using var handler = new FixtureHandler();
        using var client = new HttpClient(handler);
        var provider = CreateProvider(client);

        var result = await provider.GetArtistTracksAsync(PublicContext(),
            new(new("deezer", ProviderResourceKind.Artist, "27"), new(5, cursor), snapshot));

        Assert.Equal(ProviderErrorKind.NotSupported, result.Error?.Kind);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RadioArtistTracksRejectsDifferentProviderWithoutHttp()
    {
        using var handler = new FixtureHandler();
        using var client = new HttpClient(handler);
        var provider = CreateProvider(client);

        var result = await provider.GetArtistTracksAsync(PublicContext(),
            new(new("tidal", ProviderResourceKind.Artist, "27"), new(5)));

        Assert.Equal(ProviderErrorKind.Forbidden, result.Error?.Kind);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RadioArtistTracksHonorsCancellationWithoutHttp()
    {
        using var handler = new FixtureHandler();
        using var client = new HttpClient(handler);
        var provider = CreateProvider(client);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await provider.GetArtistTracksAsync(PublicContext(cancellation.Token),
            new(new("deezer", ProviderResourceKind.Artist, "27"), new(5)));

        Assert.Equal(ProviderErrorKind.Canceled, result.Error?.Kind);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "{}", ProviderErrorKind.NotFound)]
    [InlineData(HttpStatusCode.OK, "{\"error\":{\"code\":6,\"message\":\"fixture\"}}", ProviderErrorKind.TransientFailure)]
    [InlineData(HttpStatusCode.OK, "invalid", ProviderErrorKind.TransientFailure)]
    [InlineData(HttpStatusCode.OK, "{\"data\":\"unexpected\"}", ProviderErrorKind.TransientFailure)]
    public async Task RadioArtistTracksPreservesProviderErrors(HttpStatusCode status, string json, ProviderErrorKind expected)
    {
        using var handler = new FixtureHandler(json, status);
        using var client = new HttpClient(handler);
        var provider = CreateProvider(client);

        var result = await provider.GetArtistTracksAsync(PublicContext(),
            new(new("deezer", ProviderResourceKind.Artist, "27"), new(5)));

        Assert.Equal(expected, result.Error?.Kind);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(200)]
    public async Task RadioDefaultArtistTracksKeepsItsExistingCollectionRead(int limit)
    {
        using var handler = new FixtureHandler();
        using var client = new HttpClient(handler);
        var provider = CreateProvider(client);

        var page = (await provider.GetArtistTracksAsync(PublicContext(),
            new(new("deezer", ProviderResourceKind.Artist, "27"), new(limit)))).RequireValue();

        Assert.Equal("/artist/27/top?limit=50", Assert.Single(handler.Requests));
        Assert.Equal(["111", "112"], page.Items.Select(track => track.Id.Value));
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task RadioLegacyArtistTracksKeepsItsExistingLimit()
    {
        using var handler = new FixtureHandler();
        using var client = new HttpClient(handler);
        var provider = CreateProvider(client);

        var tracks = await provider.GetArtistTracksAsync("deezer", "27");

        Assert.Equal("/artist/27/top?limit=50", Assert.Single(handler.Requests));
        Assert.Equal(["111", "112"], tracks.Select(track => track.ExternalId));
    }

    private static DeezerProvider CreateProvider(HttpClient client) =>
        new(new DeezerHttpClient(client, 0, delay: (_, _) => Task.CompletedTask));

    private static ProviderExecutionContext PublicContext(CancellationToken cancellationToken = default) => new(
        new(ProviderActorKind.PublicRead, null), "deezer", null,
        new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, false),
            ProviderExplicitContentPolicy.Allow, false, false, false, ["deezer"]),
        "radio-test", "radio-fixture", DateTimeOffset.UtcNow.AddMinutes(1), cancellationToken);

    private sealed class FixtureHandler(string json = TracksJson, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json) });
        }
    }
}

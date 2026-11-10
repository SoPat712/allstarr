using System.Net;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Spotify;
using allstarr.Models.Settings;
using Microsoft.Extensions.Logging;

namespace allstarr.Tests;

public sealed partial class SpotifyPlaylistCapabilityAdapterTests
{
    [Theory]
    [InlineData(SpotifyApiSettings.DefaultPathfinderEndpoint)]
    [InlineData("https://api-partner.spotify.com/pathfinder/v2/query")]
    public async Task HomeFeedsParseSectionsAndUseTheExistingSignedInRequestProfile(string endpoint)
    {
        var handler = new SpotifyHomeFixtureHandler();
        using var http = new HttpClient(handler);
        var cache = new TestMemoryApplicationCache();
        var client = new SpotifyPathfinderPlaylistClient(http, cache, settings: new() { PathfinderEndpoint = endpoint });
        var result = await client.GetHomePlaylistsAsync("fixture-token", Guid.NewGuid(), default);
        var feeds = result.RequireValue();
        Assert.Equal(11, feeds.Count);
        Assert.Equal(11, feeds.Select(item => item.Id.Value).Distinct().Count());
        Assert.Equal("Made for you", feeds[0].Description);
        Assert.Equal("Your top mixes", feeds[^1].Description);
        Assert.Equal("spotify", feeds[^1].Owner.ProviderUserId);
        Assert.DoesNotContain(feeds, item => item.Name is "My favorites" or "Shared playlist");
        Assert.Contains(feeds, item => item.Name == "Daily Mix 6");
        Assert.Contains(feeds, item => item.Name == "daylist");
        Assert.Contains(feeds, item => item.Name == "Repeat Rewind");
        Assert.Contains(feeds, item => item.Name == "Discover Weekly");
        Assert.Contains(feeds, item => item.Name == "Release Radar");
        Assert.Equal("Bearer fixture-token", handler.HomeAuthorization);
        Assert.Equal("WebPlayer", handler.HomePlatform);
        Assert.Equal(endpoint.EndsWith("/v2/query", StringComparison.Ordinal) ? HttpMethod.Post : HttpMethod.Get, handler.HomeMethod);
        var request = handler.HomeRequest!.Value;
        Assert.Equal(SpotifyApiSettings.DefaultHomeQueryHash, request.GetProperty("extensions").GetProperty("persistedQuery").GetProperty("sha256Hash").GetString());
        var variables = request.GetProperty("variables");
        Assert.Equal(7, variables.EnumerateObject().Count());
        Assert.Equal(10, variables.GetProperty("sectionItemsLimit").GetInt32());
        Assert.Equal("INTEGRATION_WEB_PLAYER", variables.GetProperty("homeEndUserIntegration").GetString());
        Assert.False(string.IsNullOrWhiteSpace(variables.GetProperty("timeZone").GetString()));
        Assert.Equal("", variables.GetProperty("sp_t").GetString());
        Assert.Equal("", variables.GetProperty("facet").GetString());
        Assert.False(variables.GetProperty("includeHomeChapterVideoCards").GetBoolean());
        Assert.False(variables.GetProperty("includeEpisodeContentRatingsV2").GetBoolean());
        var artwork = await client.GetPlaylistArtworkUriAsync("fixture-token", feeds[0].Artwork!, default);
        Assert.Equal($"https://i.scdn.co/image/{feeds[0].Id.Value}", artwork.RequireValue().AbsoluteUri);
        Assert.Equal(1, handler.HomeRequests);
        Assert.Equal(0, handler.LibraryRequests);
    }

    [Fact]
    public async Task ServiceFeedsAppendHomeAfterSavedPagesAndDeduplicateSavedPlaylists()
    {
        var handler = new SpotifyHomeFixtureHandler { LibraryPages = 2 };
        using var http = new HttpClient(handler);
        var adapter = new SpotifyPlaylistCapabilityAdapter(http, new FakeSecretAccessor("fixture-cookie"));
        var context = Context(accountId: Guid.NewGuid());
        var first = (await adapter.GetServiceFeedsAsync(context, new(1))).RequireValue();
        Assert.Single(first.Items);
        Assert.Equal("1", first.NextCursor);
        Assert.Equal(0, handler.HomeRequests);
        var second = (await adapter.GetServiceFeedsAsync(context, new(1, first.NextCursor))).RequireValue();
        Assert.Null(second.NextCursor);
        Assert.False(second.IsPartial);
        var combined = first.Items.Concat(second.Items).DistinctBy(item => item.Id.Value).ToArray();
        Assert.Equal(11, combined.Length);
        Assert.Equal("saved-revision", combined[0].SourceRevision);
        Assert.Equal(1, handler.HomeRequests);
        Assert.Equal(2, handler.LibraryRequests);
    }

    [Theory]
    [InlineData("{\"errors\":[{\"message\":\"PersistedQueryNotFound\",\"extensions\":{\"code\":\"PERSISTED_QUERY_NOT_FOUND\"}}]}")]
    [InlineData("{\"data\":{\"home\":{\"sectionContainer\":{\"sections\":{\"items\":{}}}}}}")]
    [InlineData("{\"data\":{\"home\":{\"sectionContainer\":{\"sections\":{\"items\":[{\"sectionItems\":{\"items\":{}}}]}}}}}")]
    [InlineData("{\"data\":{\"home\":{\"sectionContainer\":{\"sections\":{\"items\":[{\"sectionItems\":{\"items\":[{\"content\":{\"data\":{\"__typename\":\"Playlist\",\"name\":\"Broken\"}}}]}}]}}}}}")]
    [InlineData("not-json")]
    public async Task IncompatibleHomeDisablesOnlyThatAccountUntilHashChangesAndKeepsSavedFeeds(string home)
    {
        var handler = new SpotifyHomeFixtureHandler { HomePayload = home };
        using var http = new HttpClient(handler);
        var messages = new List<string>();
        var settings = new SpotifyApiSettings();
        var adapter = new SpotifyPlaylistCapabilityAdapter(http, new FakeSecretAccessor("fixture-cookie"), new HomeLogger(messages), settings: settings);
        var context = Context(accountId: Guid.NewGuid());
        for (var i = 0; i < 2; i++)
        {
            var result = (await adapter.GetServiceFeedsAsync(context, new(100))).RequireValue();
            Assert.Single(result.Items);
            Assert.False(result.IsPartial);
        }
        Assert.Equal(2, handler.LibraryRequests);
        Assert.Equal(1, handler.HomeRequests);
        Assert.Single(messages);
        Assert.DoesNotContain(messages, message => message.Contains("fixture-token", StringComparison.Ordinal));
        handler.HomePayload = SpotifyHomeFixtureHandler.HomeFixture;
        Assert.Equal(11, (await adapter.GetServiceFeedsAsync(Context(accountId: Guid.NewGuid()), new(100))).RequireValue().Items.Count);
        Assert.Equal(2, handler.HomeRequests);
        settings.HomeQueryHash = new string('b', 64);
        settings.HomeEndUserIntegration = "INTEGRATION_MOBILE_WEB_PLAYER";
        Assert.Equal(11, (await adapter.GetServiceFeedsAsync(context, new(100))).RequireValue().Items.Count);
        Assert.Equal(settings.HomeQueryHash, handler.HomeRequest!.Value.GetProperty("extensions").GetProperty("persistedQuery").GetProperty("sha256Hash").GetString());
        Assert.Equal(settings.HomeEndUserIntegration, handler.HomeRequest.Value.GetProperty("variables").GetProperty("homeEndUserIntegration").GetString());
        Assert.Equal(3, handler.HomeRequests);
        Assert.Single(messages);
    }

    [Fact]
    public async Task HomeTransportFailureKeepsSavedFeedsAndCanRetryOnTheNextDiscovery()
    {
        var handler = new SpotifyHomeFixtureHandler { HomeStatus = HttpStatusCode.TooManyRequests };
        using var http = new HttpClient(handler);
        var adapter = new SpotifyPlaylistCapabilityAdapter(http, new FakeSecretAccessor("fixture-cookie"));
        var context = Context(accountId: Guid.NewGuid());
        Assert.Single((await adapter.GetServiceFeedsAsync(context, new(100))).RequireValue().Items);
        handler.HomeStatus = HttpStatusCode.OK;
        Assert.Equal(11, (await adapter.GetServiceFeedsAsync(context, new(100))).RequireValue().Items.Count);
        Assert.Equal(2, handler.HomeRequests);
    }

    [Fact]
    public async Task EmptyHomeIsValidAndPublicReadsCannotDiscoverPersonalFeeds()
    {
        var handler = new SpotifyHomeFixtureHandler { HomePayload = """{"data":{"home":{"sectionContainer":{"sections":{"items":[]}}}}}""" };
        using var http = new HttpClient(handler);
        var adapter = new SpotifyPlaylistCapabilityAdapter(http, new FakeSecretAccessor("fixture-cookie"));
        Assert.Single((await adapter.GetServiceFeedsAsync(Context(accountId: Guid.NewGuid()), new(100))).RequireValue().Items);
        Assert.Equal(ProviderErrorKind.Forbidden, (await adapter.GetServiceFeedsAsync(PublicContext(), new(100))).Error!.Kind);
        Assert.Equal(1, handler.HomeRequests);
    }

    private sealed class HomeLogger(List<string> messages) : ILogger<SpotifyPathfinderPlaylistClient>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            messages.Add(formatter(state, exception));
    }
}

internal sealed class SpotifyHomeFixtureHandler : HttpMessageHandler
{
    internal static string HomeFixture => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Spotify", "home.json"));
    internal string HomePayload { get; set; } = HomeFixture;
    internal HttpStatusCode HomeStatus { get; set; } = HttpStatusCode.OK;
    internal int LibraryPages { get; set; } = 1;
    internal int HomeRequests { get; private set; }
    internal int LibraryRequests { get; private set; }
    internal JsonElement? HomeRequest { get; private set; }
    internal string? HomeAuthorization { get; private set; }
    internal string? HomePlatform { get; private set; }
    internal HttpMethod? HomeMethod { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        if (uri.Host == "raw.githubusercontent.com")
            return Json(JsonSerializer.Serialize(new[] { new { version = 1, secret = Enumerable.Range(1, 32).ToArray() } }));
        if (uri.Host == "open.spotify.com")
        {
            var result = Json("""{"accessToken":"fixture-token","isAnonymous":false}""");
            result.Headers.Date = DateTimeOffset.UtcNow;
            return result;
        }
        Assert.Equal("api-partner.spotify.com", uri.Host);
        var query = uri.Query.TrimStart('?').Split('&').Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : "");
        using var document = JsonDocument.Parse(request.Content == null
            ? JsonSerializer.Serialize(new { operationName = query["operationName"], variables = JsonSerializer.Deserialize<JsonElement>(query["variables"]), extensions = JsonSerializer.Deserialize<JsonElement>(query["extensions"]) })
            : await request.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;
        if (root.GetProperty("operationName").GetString() == "home")
        {
            HomeRequests++;
            HomeRequest = root.Clone();
            HomeAuthorization = request.Headers.Authorization?.ToString();
            HomePlatform = request.Headers.GetValues("app-platform").Single();
            HomeMethod = request.Method;
            return Json(HomePayload, HomeStatus);
        }
        Assert.Equal("libraryV3", root.GetProperty("operationName").GetString());
        LibraryRequests++;
        return Json(JsonSerializer.Serialize(new
        {
            data = new
            {
                me = new
                {
                    libraryV3 = new
                    {
                        totalCount = LibraryPages,
                        items = new[]
                        {
                            new
                            {
                                item = new
                                {
                                    data = new
                                    {
                                        __typename = "Playlist", uri = "spotify:playlist:37i9dQZ1Efixture0000001", name = "Daily Mix 1",
                                        ownerV2 = new { data = new { username = "spotify", name = "Spotify" } }, revisionId = "saved-revision"
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }));
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

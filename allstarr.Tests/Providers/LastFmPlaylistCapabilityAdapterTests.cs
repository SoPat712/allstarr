using System.Net;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Operations;
using allstarr.Core.Playlists.Sources;
using allstarr.Core.Providers.LastFm;
using allstarr.Core.Providers.Spotify;
using allstarr.Core.Storage;
using allstarr.Services.Common;

namespace allstarr.Tests;

public sealed class LastFmPlaylistCapabilityAdapterTests
{
    private static readonly Guid Listener = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string Mbid = "a6b671a0-6cf0-47a1-aab1-51ce8d12b812";

    [Fact]
    public async Task LovedTracksParseSingleItemsAndArraysAndPreserveOrderAndRepeats()
    {
        var handler = new Handler(query => Page("lovedtracks", query["page"] == "1"
            ? new[] { Track("First", "Artist", Mbid), Track("First", "Artist") }
            : Track("Last", "Other"), query["page"], "2"));
        var adapter = Adapter(handler);

        var result = await new ProviderPlaylistSnapshotCollector().CollectAsync(adapter, Context(), new(Id("loved"), 2));

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal(["First", "First", "Last"], result.Snapshot!.Entries.Select(item => item.Title));
        Assert.Equal([0, 1, 2], result.Snapshot.Entries.Select(item => item.SourcePosition));
        Assert.All(result.Snapshot.Entries, item => Assert.Null(item.CanonicalRecordingId));
        Assert.Equal(Mbid, result.Snapshot.Entries[0].MusicBrainzRecordingId);
        Assert.Equal(2, handler.Queries.Count);
        Assert.All(handler.Queries, query => Assert.Equal("user.getlovedtracks", query["method"]));
    }

    [Fact]
    public async Task LovedPagingStopsAtOneThousandEvenWhenUpstreamHasMore()
    {
        var handler = new Handler(query => Page("lovedtracks",
            Enumerable.Range(0, 200).Select(index => Track($"Song {query["page"]}-{index}", "Artist")).ToArray(),
            query["page"], "99999"));
        var adapter = Adapter(handler);
        var result = await new ProviderPlaylistSnapshotCollector().CollectAsync(adapter, Context(), new(Id("loved")));

        Assert.True(result.IsSuccess);
        Assert.Equal(1000, result.Snapshot!.Entries.Count);
        Assert.Equal(5, handler.Queries.Count);
        Assert.Contains("1,000", result.Snapshot.Description);
    }

    [Fact]
    public async Task ShortLovedPagesAreReadUntilTheUpstreamEnds()
    {
        var handler = new Handler(query => Page("lovedtracks", Track($"Song {query["page"]}", "Artist"), query["page"], "6"));
        var result = await Adapter(handler).GetPlaylistTracksAsync(Context(), new(Id("loved"), new()));
        Assert.Equal(6, result.RequireValue().Tracks.Items.Count);
        Assert.Equal(6, handler.Queries.Count);
    }

    [Fact]
    public async Task RepeatedUpstreamPagesFailWithoutPublishingAPartialPlaylist()
    {
        var handler = new Handler(_ => Page("lovedtracks", Track("Song", "Artist"), "1", "6"));
        var result = await Adapter(handler).GetPlaylistTracksAsync(Context(), new(Id("loved"), new()));
        Assert.False(result.IsSuccess);
        Assert.Equal(2, handler.Queries.Count);
    }

    [Theory]
    [InlineData("7day", "7 days")]
    [InlineData("1month", "1 month")]
    [InlineData("3month", "3 months")]
    [InlineData("12month", "12 months")]
    [InlineData("overall", "all time")]
    public async Task TopPeriodAndDefaultLimitAreExplicit(string period, string label)
    {
        var handler = new Handler(_ => Page("toptracks", Track("Top", "Artist")));
        var result = await Adapter(handler).GetPlaylistTracksAsync(Context(), new(Id($"top:{period}"), new()));

        Assert.True(result.IsSuccess);
        Assert.Contains(label, result.RequireValue().Playlist.Name);
        var query = Assert.Single(handler.Queries);
        Assert.Equal(period, query["period"]);
        Assert.Equal("50", query["limit"]);
        Assert.Equal("listener", query["user"]);
        Assert.DoesNotContain("sk", query.Keys);
    }

    [Theory]
    [InlineData(25)]
    [InlineData(100)]
    public async Task TopLimitUsesTheChosenBound(int limit)
    {
        var handler = new Handler(_ => Page("toptracks",
            Enumerable.Range(0, 110).Select(index => Track($"Song {index}", "Artist")).ToArray()));
        var result = await new ProviderPlaylistSnapshotCollector().CollectAsync(Adapter(handler), Context(), new(Id($"top:overall:{limit}")));
        Assert.Equal(limit, result.Snapshot!.Entries.Count);
        Assert.Equal(limit.ToString(), Assert.Single(handler.Queries)["limit"]);
    }

    [Theory]
    [InlineData("top:6month")]
    [InlineData("top:1month:24")]
    [InlineData("top:1month:101")]
    [InlineData("loved:other-user")]
    public async Task InvalidSelectionsNeverReadAnUpstreamAccount(string selection)
    {
        var handler = new Handler(_ => throw new InvalidOperationException());
        var result = await Adapter(handler).GetPlaylistTracksAsync(Context(), new(Id(selection), new()));
        Assert.False(result.IsSuccess);
        Assert.Empty(handler.Queries);
    }

    [Fact]
    public async Task SimilarMixesRoundRobinDeduplicatesCapsArtistsAndExcludesEverySeed()
    {
        var seeds = Enumerable.Range(0, 12).Select(index => Track($"Seed {index}", $"Seed artist {index}", index == 0 ? Mbid : null)).ToArray();
        var handler = new Handler(query => query["method"] == "user.gettoptracks"
            ? Page("toptracks", seeds)
            : Page("similartracks", new[]
            {
                Track($"Seed {int.Parse(query["track"].Split(' ')[1])}", $"Seed artist {int.Parse(query["track"].Split(' ')[1])}"),
                Track("Alias for seed", "Another credit", Mbid),
                Track($"Shared artist {query["track"]}", "Shared artist"),
                Track("Duplicate", "Duplicate artist"),
                Track(" duplicate ", "DUPLICATE ARTIST"),
                Track($"Unique {query["track"]}", $"Artist {query["track"]}")
            }));
        var result = await Adapter(handler).GetPlaylistTracksAsync(Context(), new(Id("similar"), new()));

        Assert.True(result.IsSuccess);
        var tracks = result.RequireValue().Tracks.Items;
        Assert.Equal(["Shared artist Seed 0", "Shared artist Seed 1", "Shared artist Seed 2"], tracks.Take(3).Select(item => item.Metadata!.Title));
        Assert.Equal(3, tracks.Count(item => item.Metadata!.Artists[0].Name == "Shared artist"));
        Assert.Single(tracks, item => item.Metadata!.Title == "Duplicate");
        Assert.DoesNotContain(tracks, item => item.Metadata!.Title.StartsWith("Seed ", StringComparison.Ordinal) || item.Metadata.Title == "Alias for seed");
        Assert.Equal(14, tracks.Count);
        Assert.Equal(11, handler.Queries.Count);
        Assert.Equal("1month", handler.Queries[0]["period"]);
        Assert.Equal("10", handler.Queries[0]["limit"]);
        Assert.Contains("suggestions from Last.fm", result.RequireValue().Playlist.Description);
    }

    [Fact]
    public async Task SimilarStopsAtFiftyWithSingleItemSeedShape()
    {
        var handler = new Handler(query => query["method"] == "user.gettoptracks"
            ? Page("toptracks", Track("Seed", "Seed artist"))
            : Page("similartracks", Enumerable.Range(0, 100).Select(index => Track($"Song {index}", $"Artist {index}")).ToArray()));
        var result = await Adapter(handler).GetPlaylistTracksAsync(Context(), new(Id("similar"), new()));
        Assert.Equal(50, result.RequireValue().Tracks.Items.Count);
    }

    [Fact]
    public async Task SimilarParsesASingleSuggestionObject()
    {
        var handler = new Handler(query => query["method"] == "user.gettoptracks"
            ? Page("toptracks", Track("Seed", "Seed artist"))
            : Page("similartracks", Track("Suggestion", "Suggested artist")));
        var result = await Adapter(handler).GetPlaylistTracksAsync(Context(), new(Id("similar"), new()));

        Assert.Equal("Suggestion", Assert.Single(result.RequireValue().Tracks.Items).Metadata!.Title);
    }

    [Fact]
    public async Task RoundRobinSkipsExcludedCandidatesWithinEachSeedTurn()
    {
        var handler = new Handler(query => query["method"] == "user.gettoptracks"
            ? Page("toptracks", new[] { Track("Seed A", "Seed artist"), Track("Seed B", "Seed artist") })
            : Page("similartracks", query["track"] == "Seed A"
                ? new[] { Track("Seed A", "Seed artist"), Track("A1", "Artist A"), Track("A2", "Artist A") }
                : new[] { Track("B1", "Artist B"), Track("B2", "Artist B") }));
        var result = await Adapter(handler).GetPlaylistTracksAsync(Context(), new(Id("similar"), new()));
        Assert.Equal(["A1", "B1", "A2", "B2"], result.RequireValue().Tracks.Items.Select(item => item.Metadata!.Title));
    }

    [Theory]
    [InlineData("loved")]
    [InlineData("top:1month")]
    [InlineData("similar")]
    public async Task EmptyUpstreamIsAnEmptyPlaylistWithAReason(string selection)
    {
        var handler = new Handler(query => Page(query["method"] == "user.getlovedtracks" ? "lovedtracks" : "toptracks", Array.Empty<object>(), "1", "0"));
        var result = await Adapter(handler).GetPlaylistTracksAsync(Context(), new(Id(selection), new()));
        Assert.True(result.IsSuccess);
        Assert.Empty(result.RequireValue().Tracks.Items);
        Assert.Contains("No tracks", result.RequireValue().Playlist.Description);
        Assert.Single(handler.Queries);
    }

    [Fact]
    public async Task MissingConnectionAndApiKeyHaveDistinctSafeReasons()
    {
        var handler = new Handler(_ => throw new InvalidOperationException());
        var secrets = new Secrets("""{"username":"listener","sessionKey":"fixture-session"}""");
        var adapter = Adapter(handler, secrets);
        var disconnected = await adapter.GetPlaylistTracksAsync(Context(account: false), new(Id("loved"), new()));
        Assert.Equal("lastfm-not-connected", disconnected.Error!.Code);
        Assert.Equal(0, secrets.Reads);
        var missingKey = await adapter.GetPlaylistTracksAsync(Context(), new(Id("loved"), new()));
        Assert.Equal("lastfm-api-key-required", missingKey.Error!.Code);
        Assert.Equal("Needs Last.fm API key.", missingKey.Error.SafeMessage);
        secrets.Value = """{"apiKey":"fixture-key","username":"listener"}""";
        Assert.Equal("lastfm-not-connected", (await adapter.GetPlaylistTracksAsync(Context(), new(Id("loved"), new()))).Error!.Code);
        Assert.Empty(handler.Queries);
    }

    [Theory]
    [InlineData(17, ProviderErrorKind.Forbidden, "lastfm-private-profile")]
    [InlineData(29, ProviderErrorKind.RateLimited, "rate-limited")]
    [InlineData(11, ProviderErrorKind.TransientFailure, "transient-failure")]
    [InlineData(10, ProviderErrorKind.AccountNeedsConfiguration, "lastfm-api-key-required")]
    public async Task ApiErrorsStayTypedAndNeverExposeUpstreamMessages(int code, ProviderErrorKind kind, string reason)
    {
        var handler = new Handler(_ => JsonSerializer.Serialize(new { error = code, message = "fixture-key fixture-session private data" }));
        var result = await Adapter(handler).GetPlaylistTracksAsync(Context(), new(Id("loved"), new()));
        Assert.Equal(kind, result.Error!.Kind);
        Assert.Equal(reason, result.Error.Code);
        Assert.DoesNotContain("fixture-", result.Error.ToString());
        Assert.Single(handler.Queries);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, ProviderErrorKind.RateLimited)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ProviderErrorKind.TransientFailure)]
    public async Task HttpFailuresUseExistingRetryClassifications(HttpStatusCode status, ProviderErrorKind kind)
    {
        var handler = new Handler(_ => "{}") { Status = status };
        var result = await Adapter(handler).GetPlaylistTracksAsync(Context(), new(Id("loved"), new()));
        Assert.Equal(kind, result.Error!.Kind);
        if (kind == ProviderErrorKind.RateLimited) Assert.Equal(TimeSpan.FromSeconds(17), result.Error.RetryAfter);
        Assert.Single(handler.Queries);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"lovedtracks\":{\"track\":42}}")]
    [InlineData("[]")]
    [InlineData("not json")]
    public async Task MalformedResponsesAreFailuresRatherThanEmptyPlaylists(string body)
    {
        var result = await Adapter(new Handler(_ => body)).GetPlaylistTracksAsync(Context(), new(Id("loved"), new()));
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task MalformedTrackItemsReturnAContractFailure()
    {
        var handler = new Handler(_ => Page("lovedtracks", new object[] { Track("Song", "Artist"), 42 }));
        var result = await Adapter(handler).GetPlaylistTracksAsync(Context(), new(Id("loved"), new()));

        Assert.False(result.IsSuccess);
        Assert.Equal("provider-contract-changed", result.Error!.Code);
    }

    [Fact]
    public async Task SharedAccountsPublicReadsAndExplicitUserLookupsCannotSupplyPersonalTracks()
    {
        var handler = new Handler(_ => throw new InvalidOperationException());
        var secrets = new Secrets();
        var adapter = Adapter(handler, secrets);
        var shared = await adapter.GetPlaylistTracksAsync(Context(shared: true), new(Id("loved"), new()));
        Assert.Equal("lastfm-not-connected", shared.Error!.Code);
        var publicContext = new ProviderExecutionContext(new(ProviderActorKind.PublicRead, null), "lastfm", null,
            Context().Policy, "playlist-read", "fixture", DateTimeOffset.UtcNow.AddMinutes(1), default);
        Assert.Equal("lastfm-not-connected", (await adapter.GetPlaylistTracksAsync(publicContext, new(Id("loved"), new()))).Error!.Code);
        var other = await adapter.GetUserPlaylistsAsync(Context(), new(new(), "other-listener"));
        Assert.Equal(ProviderErrorKind.Forbidden, other.Error!.Kind);
        Assert.Equal(0, secrets.Reads);
        Assert.Empty(handler.Queries);
        Assert.Throws<UnauthorizedAccessException>(() => Context(owner: Guid.NewGuid()));
    }

    [Fact]
    public async Task CachedContinuationIsBoundToTheExactListenerAccountAndRevision()
    {
        var handler = new Handler(query => Page("lovedtracks", new[] { Track(query["user"], "Artist"), Track("Second", "Artist") }));
        var secrets = new Secrets();
        var adapter = Adapter(handler, secrets);
        var first = (await adapter.GetPlaylistTracksAsync(Context(), new(Id("loved"), new(1)))).RequireValue();
        var continued = await adapter.GetPlaylistTracksAsync(Context(), new(Id("loved"), new(1, "1"), first.Playlist.SourceRevision));
        Assert.Equal("Second", Assert.Single(continued.RequireValue().Tracks.Items).Metadata!.Title);
        Assert.Single(handler.Queries);
        secrets.Value = """{"apiKey":"fixture-key","username":"second-listener","sessionKey":"fixture-session"}""";
        var second = await adapter.GetPlaylistTracksAsync(Context(listener: Guid.NewGuid(), accountId: Guid.NewGuid()),
            new(Id("loved"), new(1, "1"), first.Playlist.SourceRevision));
        Assert.False(second.IsSuccess);
        Assert.Equal(2, handler.Queries.Count);
    }

    [Fact]
    public async Task RegistrationExposesOnlyPersonalPlaylistReadsAndDiscoveryListsTheSelections()
    {
        var handler = new Handler(_ => throw new InvalidOperationException());
        var adapter = Adapter(handler);
        var registration = ProviderRegistrationValidator.Validate(LastFmPlaylistCapabilityRegistration.CreateRegistration(adapter));
        var capability = Assert.Single(registration.Descriptor.Capabilities);
        Assert.Equal(ProviderCapabilityKind.Playlist, capability.Capability);
        Assert.Equal([ProviderAccountScope.Personal], capability.AllowedAccountScopes);
        Assert.False(capability.SupportsPublicRead);
        Assert.True(capability.SupportsPlaylistDiscovery);
        Assert.Empty(registration.Descriptor.Settings);
        Assert.Equal([ProviderAccountScope.Shared, ProviderAccountScope.Personal],
            ProviderAccountAudiences.Supported(new ProviderRegistry([registration]), "lastfm"));
        var discovery = await adapter.GetUserPlaylistsAsync(Context(), new(new(100)));
        Assert.Equal(["loved", "top:7day:50", "top:1month:50", "top:3month:50", "top:12month:50", "top:overall:50", "similar"],
            discovery.RequireValue().Items.Select(item => item.Id.Value));
        Assert.Empty(handler.Queries);
    }

    private static LastFmPlaylistCapabilityAdapter Adapter(Handler handler, Secrets? secrets = null) =>
        new(new HttpClient(handler), secrets ?? new(), new MemoryApplicationCache(new SystemPlatformClock()));

    private static ProviderExternalResourceId Id(string value) => new("lastfm", ProviderResourceKind.Playlist, value);

    private static ProviderExecutionContext Context(bool account = true, bool shared = false, Guid? listener = null, Guid? owner = null, Guid? accountId = null)
    {
        var user = listener ?? Listener;
        return new(new(ProviderActorKind.User, user, new("jellyfin", "backend", "principal")), "lastfm",
            account ? new(accountId ?? Guid.Parse("33333333-3333-3333-3333-333333333333"), "lastfm",
                shared ? ProviderAccountScope.Shared : ProviderAccountScope.Personal, 1,
                ownerUserId: shared ? null : owner ?? user, secretReferenceId: Guid.Parse("44444444-4444-4444-4444-444444444444")) : null,
            new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true), ProviderExplicitContentPolicy.Allow,
                true, true, false, ["lastfm"]), "playlist-read", "fixture", DateTimeOffset.UtcNow.AddMinutes(1), default);
    }

    private static object Track(string title, string artist, string? mbid = null) => new { name = title, artist = new { name = artist }, mbid = mbid ?? "" };
    private static string Page(string container, object tracks, string page = "1", string totalPages = "1") =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [container] = new Dictionary<string, object>
            {
                ["track"] = tracks,
                ["@attr"] = new { page, totalPages }
            }
        });

    private sealed class Secrets(string value = """{"apiKey":"fixture-key","username":"listener","sessionKey":"fixture-session"}""") : IProviderAccountSecretAccessor
    {
        public string Value { get; set; } = value;
        public int Reads { get; private set; }
        public Task<T> UseAsync<T>(ProviderAccountContext account, Func<ReadOnlyMemory<byte>, Task<T>> operation, CancellationToken cancellationToken)
        {
            Reads++;
            return operation(Encoding.UTF8.GetBytes(Value));
        }
    }

    private sealed class Handler(Func<Dictionary<string, string>, string> response) : HttpMessageHandler
    {
        public List<Dictionary<string, string>> Queries { get; } = [];
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("ws.audioscrobbler.com", request.RequestUri!.Host);
            var query = request.RequestUri.Query.TrimStart('?').Split('&').Select(part => part.Split('=', 2))
                .ToDictionary(part => Uri.UnescapeDataString(part[0]), part => Uri.UnescapeDataString(part[1]));
            Queries.Add(query);
            var result = new HttpResponseMessage(Status) { Content = new StringContent(response(query)) };
            result.Headers.RetryAfter = new(TimeSpan.FromSeconds(17));
            return Task.FromResult(result);
        }
    }
}

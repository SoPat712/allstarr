using System.Net;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Providers.AppleMusicKit;
using allstarr.Core.Providers.Lyrics;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using Moq;

namespace allstarr.Tests;

public sealed class PaxsenixCapabilityAdapterTests
{
    private const string Song = """{"id":"617154366","type":"songs","attributes":{"name":"月光","artistName":"歌手","durationInMillis":180000,"isrc":"GBARL9300135"}}""";
    private const string Lyrics = """{"type":"Line","content":[{}],"lrc":"[00:01.000]月光"}""";

    [Theory]
    [InlineData("ttmlContent", "<tt><body><p begin='1'><span begin='1' end='2'>月光</span></p></body></tt>", ProviderLyricsFormat.LineTimed)]
    [InlineData("elrc", "[00:01.000]<00:01.000>月<00:01.500>光", ProviderLyricsFormat.LineTimed)]
    [InlineData("lrc", "[00:01.000]月光", ProviderLyricsFormat.LineTimed)]
    [InlineData("plain", "月光", ProviderLyricsFormat.PlainText)]
    public async Task ResponseFormatsPreserveBestTiming(string field, string text, ProviderLyricsFormat format)
    {
        var payload = new Dictionary<string, object> { ["type"] = "Syllable", ["content"] = new[] { new { } }, [field] = text };
        if (field != "plain") payload["plain"] = "fallback";
        if (field == "ttmlContent") payload["elrc"] = "[00:02.000]fallback";
        var handler = new Handler((_, _) => Response(JsonSerializer.Serialize(payload)));
        var value = (await Adapter(handler).FetchLyricsAsync(Context(), Request())).RequireValue();
        Assert.Equal(format, value.Format);
        Assert.Equal("paxsenix", value.Source);
        Assert.StartsWith("sha256:", value.Revision);
        Assert.Contains("月", value.Content);
        Assert.Equal("/apple-music/lyrics", Assert.Single(handler.Requests).AbsolutePath);
        Assert.Equal("?id=617154366", handler.Requests[0].Query);
        if (field == "ttmlContent") Assert.NotNull(Assert.Single(value.TimedLines!).Segments);
        if (field == "elrc") Assert.Equal(2, Assert.Single(allstarr.Core.Music.MusicLyrics.ParseSyncedLines(value.Content)).Cues!.Count);
    }

    [Fact]
    public async Task VerifiedRecordingIdentityPrecedesCatalogReadsAndRouterTrackId()
    {
        var recording = Guid.CreateVersion7();
        var identities = new Mock<ITrackIdentityService>();
        identities.Setup(value => value.ResolveTargetAsync(It.Is<ProviderExecutionContext>(c => c.ProviderId == "apple-musickit" && c.Account == null),
                recording, It.Is<ProviderTrackIdentityTarget>(t => t.ProviderId == "apple-musickit"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TrackIdentityResolution(recording, Guid.CreateVersion7(), new("apple-musickit", ProviderResourceKind.Track, "123"),
                ProviderIdentityScope.Catalog, null, ProviderIdentityVerification.Verified, "source-use", 1));
        var apple = Catalog(_ => throw new InvalidOperationException("Unexpected catalog read."));
        var handler = new Handler((_, _) => Response(Lyrics));
        await Adapter(handler, apple: apple, identities: identities.Object).FetchLyricsAsync(Context(), Request(recording: recording));
        Assert.Equal("?id=123", Assert.Single(handler.Requests).Query);
        identities.VerifyAll();
    }

    [Fact]
    public async Task RecordingIdentityServiceIsResolvedOnlyDuringLookup()
    {
        var resolutions = 0;
        var identities = new Mock<ITrackIdentityService>();
        var handler = new Handler((_, _) => Response(Lyrics));
        var adapter = new PaxsenixCapabilityAdapter(new HttpClient(handler), null,
            new PaxsenixRequestPacer(TimeProvider.System, TimeSpan.Zero), TimeProvider.System, Catalog(),
            () => { resolutions++; return identities.Object; });

        Assert.Equal(0, resolutions);
        var result = await adapter.FetchLyricsAsync(Context(), Request());

        Assert.Equal(ProviderLyricsAvailabilityState.Available, result.RequireValue().Availability);
        Assert.Equal(1, resolutions);
        identities.Verify(value => value.ResolveTargetAsync(It.IsAny<ProviderExecutionContext>(), Recording,
            It.IsAny<ProviderTrackIdentityTarget>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task IdenticalMetadataDoesNotShareLyricsBetweenRecordings()
    {
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var identities = new Mock<ITrackIdentityService>();
        identities.Setup(value => value.ResolveTargetAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<Guid>(),
                It.IsAny<ProviderTrackIdentityTarget>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderExecutionContext _, Guid recording, ProviderTrackIdentityTarget _, CancellationToken _) =>
                new TrackIdentityResolution(recording, Guid.CreateVersion7(),
                    new("apple-musickit", ProviderResourceKind.Track, recording == first ? "123" : "456"),
                    ProviderIdentityScope.Catalog, null, ProviderIdentityVerification.Verified, "source-use", 1));
        var handler = new Handler((uri, _) => Response(Lyrics.Replace("月光", uri.Query)));
        var adapter = Adapter(handler, new TestMemoryApplicationCache(), identities: identities.Object);
        var firstResult = await adapter.FetchLyricsAsync(Context(), Request(recording: first));
        var secondResult = await adapter.FetchLyricsAsync(Context(), Request(recording: second));
        Assert.Contains("123", firstResult.RequireValue().Content);
        Assert.Contains("456", secondResult.RequireValue().Content);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task IsrcPrecedesSearchAndResolvedIdsAreCachedAcrossMetadataChanges()
    {
        var calls = new List<Uri>();
        var apple = Catalog(request =>
        {
            calls.Add(request.RequestUri!);
            Assert.Contains("filter[isrc]=", Uri.UnescapeDataString(request.RequestUri!.Query));
            Assert.Contains("GBARL9300135", request.RequestUri.Query);
            Assert.False(request.Headers.Contains("Cookie"));
            return Response("{\"data\":[" + Song + "]}");
        });
        var handler = new Handler((_, _) => Response(Lyrics));
        var adapter = Adapter(handler, new TestMemoryApplicationCache(), apple);
        await adapter.FetchLyricsAsync(Context(), Request());
        await adapter.FetchLyricsAsync(Context(), Request(title: "Changed title"));
        await adapter.FetchLyricsAsync(Context(), Request(title: "Another title", recording: Guid.CreateVersion7()));
        Assert.Single(calls);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task IsrcMatchesPreferOriginalAlbumsThenEarliestRelease()
    {
        var apple = Catalog(request =>
        {
            Assert.Contains("include=albums", request.RequestUri!.Query);
            return Response("{\"data\":[" + CatalogSong("1", true, "2000-01-01") + "," +
                CatalogSong("2", false, "2020-01-01") + "," + CatalogSong("3", false, "2013-05-17") + "]}");
        });
        var handler = new Handler((_, _) => Response(Lyrics));

        var result = await Adapter(handler, apple: apple).FetchLyricsAsync(Context(), Request());

        Assert.Equal(ProviderLyricsAvailabilityState.Available, result.RequireValue().Availability);
        Assert.Equal("?id=3", Assert.Single(handler.Requests).Query);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task FailedIsrcCandidateFallsThroughToStrictSearch(HttpStatusCode status)
    {
        var apple = Catalog(request => Response(request.RequestUri!.AbsolutePath.EndsWith("/search", StringComparison.Ordinal)
            ? "{\"results\":{\"songs\":{\"data\":[" + CatalogSong("2") + "]}}}"
            : "{\"data\":[" + CatalogSong("1", true) + "]}"));
        var handler = new Handler((_, attempt) => attempt == 1 ? Busy(status) : Response(Lyrics));

        var result = await Adapter(handler, apple: apple).FetchLyricsAsync(Context(), Request());

        Assert.Equal(ProviderLyricsAvailabilityState.Available, result.RequireValue().Availability);
        Assert.Equal(["?id=1", "?id=2"], handler.Requests.Select(uri => uri.Query));
    }

    [Fact]
    public async Task CandidateWithoutAnAnswerFallsThroughToStrictSearch()
    {
        var apple = Catalog(request => Response(request.RequestUri!.AbsolutePath.EndsWith("/search", StringComparison.Ordinal)
            ? "{\"results\":{\"songs\":{\"data\":[" + CatalogSong("2") + "]}}}"
            : "{\"data\":[" + CatalogSong("1", true) + "]}"));
        var handler = new Handler((_, attempt) => attempt == 1 ? throw new HttpRequestException("timed out") : Response(Lyrics));

        var result = await Adapter(handler, apple: apple).FetchLyricsAsync(Context(), Request());

        Assert.Equal(ProviderLyricsAvailabilityState.Available, result.RequireValue().Availability);
        Assert.Equal(["?id=1", "?id=2"], handler.Requests.Select(uri => uri.Query));
    }

    [Theory]
    [InlineData(2, ProviderLyricsAvailabilityState.Available)]
    [InlineData(20, null)]
    public async Task CandidateFallbackKeepsPacingAndTotalBudget(int spacingSeconds, ProviderLyricsAvailabilityState? availability)
    {
        var clock = new ManualTimeProvider();
        var apple = Catalog(_ => Response("{\"data\":[" + CatalogSong("1") + "," + CatalogSong("2") + "]}"));
        var handler = new Handler((_, attempt) => attempt == 1 ? Busy() : Response(Lyrics));
        var adapter = new PaxsenixCapabilityAdapter(new HttpClient(handler), null,
            new PaxsenixRequestPacer(clock, TimeSpan.FromSeconds(spacingSeconds)), clock, apple);

        var pending = adapter.FetchLyricsAsync(Context(), Request());
        await clock.WaitForTimersAsync(TimeSpan.FromSeconds(spacingSeconds));
        Assert.Single(handler.Requests);
        clock.Advance(TimeSpan.FromSeconds(Math.Min(spacingSeconds, 18)));
        var result = await pending;

        if (availability == null)
        {
            Assert.Equal(ProviderErrorKind.TransientFailure, result.Error!.Kind);
            Assert.Single(handler.Requests);
        }
        else
        {
            Assert.Equal(availability, result.RequireValue().Availability);
            Assert.Equal(["?id=1", "?id=2"], handler.Requests.Select(uri => uri.Query));
        }
    }

    [Fact]
    public async Task AllBusyCandidatesReturnTransientFailureAndStopAtThreeDistinctIds()
    {
        var apple = Catalog(_ => Response("{\"data\":[" + string.Join(',', new[] { "1", "1", "2", "3", "4" }
            .Select(id => CatalogSong(id))) + "]}"));
        var handler = new Handler((_, _) => Busy());

        var result = await Adapter(handler, apple: apple).FetchLyricsAsync(Context(), Request());

        Assert.Equal(ProviderErrorKind.TransientFailure, result.Error!.Kind);
        Assert.Equal(["?id=1", "?id=2", "?id=3"], handler.Requests.Select(uri => uri.Query));
    }

    [Theory]
    [InlineData("{\"type\":\"None\"}")]
    [InlineData("{\"type\":\"Line\",\"content\":[],\"lrc\":\"[00:01]月光\"}")]
    public async Task EmptyCandidateAndBusyCandidateReturnUnavailable(string payload)
    {
        var apple = Catalog(request => Response(request.RequestUri!.AbsolutePath.EndsWith("/search", StringComparison.Ordinal)
            ? "{\"results\":{\"songs\":{\"data\":[]}}}"
            : "{\"data\":[" + CatalogSong("1") + "," + CatalogSong("2") + "]}"));
        var handler = new Handler((_, attempt) => attempt == 1 ? Response(payload) : Busy());

        var result = await Adapter(handler, apple: apple).FetchLyricsAsync(Context(), Request());

        Assert.Equal(ProviderLyricsAvailabilityState.Unavailable, result.RequireValue().Availability);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task WinningIdReplacesCachedIdAndPrecedesVerifiedIdentityOnLaterLookups()
    {
        var cache = new TestMemoryApplicationCache();
        await cache.SetAsync($"lyrics:paxsenix:apple-id:recording:{Recording:N}", "1", TimeSpan.FromDays(1));
        var identities = new Mock<ITrackIdentityService>();
        identities.Setup(value => value.ResolveTargetAsync(It.IsAny<ProviderExecutionContext>(), Recording,
                It.IsAny<ProviderTrackIdentityTarget>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TrackIdentityResolution(Recording, Guid.CreateVersion7(),
                new("apple-musickit", ProviderResourceKind.Track, "1"), ProviderIdentityScope.Catalog, null,
                ProviderIdentityVerification.Verified, "source-use", 1));
        var catalogCalls = 0;
        var apple = Catalog(_ => { catalogCalls++; return Response("{\"data\":[" + CatalogSong("1") + "," + CatalogSong("2") + "]}"); });
        var handler = new Handler((uri, _) => uri.Query == "?id=1" ? Busy() : Response(Lyrics));
        var adapter = Adapter(handler, cache, apple, identities.Object);

        await adapter.FetchLyricsAsync(Context(), Request());
        await adapter.FetchLyricsAsync(Context(), Request(title: "Changed title"));
        await adapter.FetchLyricsAsync(Context(), Request(recording: Guid.CreateVersion7()));

        Assert.Equal(["?id=1", "?id=2", "?id=2", "?id=2"], handler.Requests.Select(uri => uri.Query));
        Assert.Equal(1, catalogCalls);
        Assert.Equal("2", await cache.GetAsync<string>($"lyrics:paxsenix:apple-id:recording:{Recording:N}"));
    }

    [Fact]
    public async Task SearchPrefersEqualIsrcAndDeduplicatesEarlierCandidates()
    {
        var apple = Catalog(request => Response(request.RequestUri!.AbsolutePath.EndsWith("/search", StringComparison.Ordinal)
            ? "{\"results\":{\"songs\":{\"data\":[" + CatalogSong("1") + "," +
                CatalogSong("2").Replace("GBARL9300135", "OTHER") + "," + CatalogSong("3") + "]}}}"
            : "{\"data\":[" + CatalogSong("1") + "]}"));
        var handler = new Handler((_, attempt) => attempt == 1 ? Busy() : Response(Lyrics));

        var result = await Adapter(handler, apple: apple).FetchLyricsAsync(Context(), Request());

        Assert.Equal(ProviderLyricsAvailabilityState.Available, result.RequireValue().Availability);
        Assert.Equal(["?id=1", "?id=3"], handler.Requests.Select(uri => uri.Query));
    }

    [Fact]
    public async Task RecordingCacheWorksWithoutIsrcAndUsesConfiguredLyricsLifetime()
    {
        var calls = 0;
        var apple = Catalog(_ => { calls++; return Response("{\"results\":{\"songs\":{\"data\":[" + Song + "]}}}"); });
        var handler = new Handler((_, _) => Response(Lyrics));
        var cache = new TestMemoryApplicationCache();
        var adapter = Adapter(handler, cache, apple);
        await adapter.FetchLyricsAsync(Context(), Request(isrc: null));
        await adapter.FetchLyricsAsync(Context(), Request(isrc: null, title: "Changed title"));
        Assert.Equal(1, calls);
        Assert.Equal(2, handler.Requests.Count);

        var lifetime = TimeSpan.FromHours(12);
        var observed = new Mock<IApplicationCache>();
        observed.Setup(value => value.SetAsync(It.IsAny<string>(), It.IsAny<string>(), lifetime)).ReturnsAsync(true);
        adapter = new PaxsenixCapabilityAdapter(new HttpClient(handler), observed.Object,
            new PaxsenixRequestPacer(TimeProvider.System, TimeSpan.Zero), TimeProvider.System, Catalog(), lyricsTtl: () => lifetime);
        await adapter.FetchLyricsAsync(Context(), Request());
        observed.Verify(value => value.SetAsync(It.IsAny<string>(), "617154366", lifetime), Times.Exactly(2));
    }

    [Fact]
    public async Task IsrcCanResolveWithoutTitleOrArtists()
    {
        var handler = new Handler((_, _) => Response(Lyrics));
        var request = new ProviderLyricsRequest(Recording, new("paxsenix", ProviderResourceKind.Track, "lookup"), isrc: "GBARL9300135");
        Assert.Equal(ProviderLyricsAvailabilityState.Available,
            (await Adapter(handler).FetchLyricsAsync(Context(), request)).RequireValue().Availability);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task AppleCatalogReadCannotExceedTotalLookupBudget()
    {
        var clock = new ManualTimeProvider();
        var slow = new WaitingHandler();
        var apple = new AppleMusicKitMetadataCapabilityAdapter(AppleProviderTestFactory.Client(new HttpClient(slow)));
        var lyrics = new Handler((_, _) => throw new InvalidOperationException("Unexpected lyrics read."));
        var adapter = new PaxsenixCapabilityAdapter(new HttpClient(lyrics), null,
            new PaxsenixRequestPacer(clock), clock, apple);
        var pending = adapter.FetchLyricsAsync(Context(), Request());
        await clock.WaitForTimersAsync(TimeSpan.FromSeconds(18));
        clock.Advance(TimeSpan.FromSeconds(18));
        Assert.Equal(ProviderErrorKind.TransientFailure, (await pending).Error!.Kind);
        Assert.Equal(1, slow.Calls);
        Assert.Empty(lyrics.Requests);
    }

    [Theory]
    [InlineData("Other song", "歌手", 180000)]
    [InlineData("月光", "Someone else", 180000)]
    [InlineData("月光 (Live)", "歌手", 180000)]
    [InlineData("白月光", "歌手", 180000)]
    [InlineData("月光", "歌手", 186000)]
    public async Task SearchRejectsPoorMatches(string title, string artist, int duration)
    {
        var candidate = JsonSerializer.Serialize(new { id = "123", attributes = new { name = title, artistName = artist, durationInMillis = duration } });
        var handler = new Handler((_, _) => throw new InvalidOperationException("Unexpected lyrics read."));
        var apple = Catalog(_ => Response("{\"results\":{\"songs\":{\"data\":[" + candidate + "]}}}"));
        var result = await Adapter(handler, apple: apple).FetchLyricsAsync(Context(), Request(isrc: null));
        Assert.Equal(ProviderLyricsAvailabilityState.Unavailable, result.RequireValue().Availability);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task IsrcMissFallsBackToStrictSearchAndChoosesBestMatch()
    {
        var calls = new List<Uri>();
        var apple = Catalog(request =>
        {
            calls.Add(request.RequestUri!);
            return Response(calls.Count == 1 ? "{\"data\":[]}" : "{\"results\":{\"songs\":{\"data\":[" +
                Song.Replace("月光", "Other song").Replace("617154366", "1") + "," + Song + "]}}}");
        });
        var handler = new Handler((_, _) => Response(Lyrics));
        var result = await Adapter(handler, apple: apple).FetchLyricsAsync(Context(), Request());
        Assert.Equal(ProviderLyricsAvailabilityState.Available, result.RequireValue().Availability);
        Assert.Equal(2, calls.Count);
        Assert.Equal("/v1/catalog/us/search", calls[1].AbsolutePath);
        Assert.Contains(Uri.EscapeDataString("月光 歌手"), calls[1].AbsoluteUri);
        Assert.Equal("?id=617154366", Assert.Single(handler.Requests).Query);
    }

    [Theory]
    [InlineData("{\"type\":\"None\",\"content\":[{}],\"plain\":\"月光\"}")]
    [InlineData("{\"type\":\"Line\",\"content\":[],\"lrc\":\"[00:01]月光\"}")]
    [InlineData("{\"type\":\"Line\",\"content\":[{}],\"plain\":\"\"}")]
    public async Task EmptyLyricsAreUnavailable(string payload)
    {
        var handler = new Handler((_, _) => Response(payload));
        Assert.Equal(ProviderLyricsAvailabilityState.Unavailable,
            (await Adapter(handler).FetchLyricsAsync(Context(), Request())).RequireValue().Availability);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("<html>Error</html>")]
    [InlineData("{\"type\":\"Line\",\"content\":[{}],\"lrc\":42}")]
    [InlineData("{\"type\":\"Line\",\"content\":[{}]}")]
    [InlineData("{\"type\":\"Syllable\",\"content\":[{}],\"elrc\":\"invalid timing\"}")]
    [InlineData("{\"type\":\"Line\",\"content\":[{}],\"plain\":\"<html>Error</html>\"}")]
    public async Task MalformedResponsesFailAsContractChanges(string payload)
    {
        var result = await Adapter(new Handler((_, _) => Response(payload))).FetchLyricsAsync(Context(), Request());
        Assert.Equal("provider-contract-changed", result.Error!.Code);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, ProviderErrorKind.PermanentFailure)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ProviderErrorKind.TransientFailure)]
    [InlineData(HttpStatusCode.TooManyRequests, ProviderErrorKind.RateLimited)]
    [InlineData(HttpStatusCode.TemporaryRedirect, ProviderErrorKind.PermanentFailure)]
    public async Task HttpFailuresKeepPublicLyricsClassification(HttpStatusCode status, ProviderErrorKind kind)
    {
        var handler = new Handler((_, _) => new(status) { Content = new StringContent("{\"error\":\"Forbidden\"}") });
        Assert.Equal(kind, (await Adapter(handler).FetchLyricsAsync(Context(), Request())).Error!.Kind);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task NotFoundIsUnavailableAndRichLyricsAreCachedForProjections()
    {
        var missing = Adapter(new Handler((_, _) => new(HttpStatusCode.NotFound)));
        Assert.Equal(ProviderLyricsAvailabilityState.Unavailable, (await missing.FetchLyricsAsync(Context(), Request())).RequireValue().Availability);
        var handler = new Handler((_, _) => Response(Lyrics));
        var adapter = Adapter(handler, new TestMemoryApplicationCache());
        Assert.Null((await adapter.FetchLyricsAsync(Context(), Request(availability: true))).RequireValue().Content);
        Assert.Equal("月光", (await adapter.FetchLyricsAsync(Context(), Request(format: ProviderLyricsFormat.PlainText))).RequireValue().Content);
        Assert.Contains("[00:01.000]", (await adapter.FetchLyricsAsync(Context(), Request())).RequireValue().Content);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task NoCatalogIdIsUnavailableAndMissesAreCached()
    {
        var calls = 0;
        var apple = Catalog(_ => { calls++; return Response("{\"results\":{\"songs\":{\"data\":[]}}}"); });
        var handler = new Handler((_, _) => throw new InvalidOperationException("Unexpected lyrics read."));
        var adapter = Adapter(handler, new TestMemoryApplicationCache(), apple);
        await adapter.FetchLyricsAsync(Context(), Request(isrc: null));
        Assert.Equal(ProviderLyricsAvailabilityState.Unavailable,
            (await adapter.FetchLyricsAsync(Context(), Request(isrc: null))).RequireValue().Availability);
        Assert.Equal(1, calls);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task OversizedResponsesAreRejected()
    {
        var handler = new Handler((_, _) => Response(new string('x', 2_000_001)));
        var result = await Adapter(handler).FetchLyricsAsync(Context(), Request());
        Assert.Equal("provider-contract-changed", result.Error!.Code);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task SharedPacingSpacesRequestsAcrossInstancesAndCancellationDoesNotSend()
    {
        var clock = new ManualTimeProvider();
        var pacer = new PaxsenixRequestPacer(clock);
        var handler = new Handler((_, _) => new(HttpStatusCode.TooManyRequests));
        var first = new PaxsenixCapabilityAdapter(new HttpClient(handler), null, pacer, clock, Catalog());
        var second = new PaxsenixCapabilityAdapter(new HttpClient(handler), null, pacer, clock, Catalog());
        await first.FetchLyricsAsync(Context(), Request());
        using var cancel = new CancellationTokenSource();
        var pending = second.FetchLyricsAsync(Context(cancel.Token), Request());
        await clock.WaitForTimersAsync(TimeSpan.FromSeconds(2));
        Assert.Single(handler.Requests);
        clock.Advance(TimeSpan.FromSeconds(2));
        await pending;
        Assert.Equal(2, handler.Requests.Count);
        pending = first.FetchLyricsAsync(Context(cancel.Token), Request());
        await clock.WaitForTimersAsync(TimeSpan.FromSeconds(2));
        cancel.Cancel();
        Assert.Equal(ProviderErrorKind.Canceled, (await pending).Error!.Kind);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task RequestTimeoutAndTotalBudgetBoundSlowAndPacedLookups()
    {
        var clock = new ManualTimeProvider();
        var handler = new WaitingHandler();
        var adapter = new PaxsenixCapabilityAdapter(new HttpClient(handler), null,
            new PaxsenixRequestPacer(clock, TimeSpan.Zero), clock, Catalog());
        var pending = adapter.FetchLyricsAsync(Context(), Request());
        await clock.WaitForTimersAsync(TimeSpan.FromSeconds(8));
        clock.Advance(TimeSpan.FromSeconds(8));
        Assert.Equal(ProviderErrorKind.TransientFailure, (await pending).Error!.Kind);
        Assert.Equal(1, handler.Calls);

        var paced = new Handler((_, _) => Response(Lyrics));
        adapter = new PaxsenixCapabilityAdapter(new HttpClient(paced), null,
            new PaxsenixRequestPacer(clock, TimeSpan.FromSeconds(20)), clock, Catalog());
        await adapter.FetchLyricsAsync(Context(), Request());
        pending = adapter.FetchLyricsAsync(Context(), Request());
        await clock.WaitForTimersAsync(TimeSpan.FromSeconds(20));
        clock.Advance(TimeSpan.FromSeconds(18));
        Assert.Equal(ProviderErrorKind.TransientFailure, (await pending).Error!.Kind);
        Assert.Single(paced.Requests);
    }

    private static string CatalogSong(string id, bool compilation = false, string releaseDate = "2013-05-17") =>
        JsonSerializer.Serialize(new
        {
            id,
            type = "songs",
            attributes = new { name = "月光", artistName = "歌手", durationInMillis = 180000, isrc = "GBARL9300135", releaseDate },
            relationships = new { albums = new { data = new[] { new { id = "10" + id, attributes = new { isCompilation = compilation, releaseDate } } } } }
        });

    private static HttpResponseMessage Busy(HttpStatusCode status = HttpStatusCode.ServiceUnavailable) =>
        new(status) { Content = new StringContent("{\"message\":\"Lyrics service busy; retry later\",\"error\":true}") };

    private static AppleMusicKitMetadataCapabilityAdapter Catalog(Func<HttpRequestMessage, HttpResponseMessage>? send = null) =>
        new(AppleProviderTestFactory.Client(new HttpClient(new AppleProviderTestFactory.Handler(send ?? (_ => Response("{\"data\":[" + Song + "]}"))))));
    private static PaxsenixCapabilityAdapter Adapter(HttpMessageHandler handler, IApplicationCache? cache = null,
        AppleMusicKitMetadataCapabilityAdapter? apple = null, ITrackIdentityService? identities = null) =>
        new(new HttpClient(handler), cache, new PaxsenixRequestPacer(TimeProvider.System, TimeSpan.Zero), TimeProvider.System,
            apple ?? Catalog(), identities == null ? null : () => identities);
    private static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };
    private static readonly Guid Recording = Guid.CreateVersion7();
    private static ProviderLyricsRequest Request(bool availability = false, ProviderLyricsFormat? format = null,
        string? isrc = "GBARL9300135", string title = "月光", Guid? recording = null) =>
        new(recording ?? Recording, new("paxsenix", ProviderResourceKind.Track, "lookup"), availability, format,
            title, ["歌手"], durationSeconds: 180, isrc: isrc);
    private static ProviderExecutionContext Context(CancellationToken token = default) =>
        new(new(ProviderActorKind.PublicRead, null), "paxsenix", null,
            new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true), ProviderExplicitContentPolicy.Allow,
                true, true, false, ["paxsenix"]), "lyrics-test", "lyrics-test", DateTimeOffset.UtcNow.AddMinutes(1), token);

    private sealed class Handler(Func<Uri, int, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            Assert.Equal("lyrics.paxsenix.org", request.RequestUri!.Host);
            Assert.NotEmpty(request.Headers.UserAgent);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            return Task.FromResult(response(request.RequestUri!, Requests.Count));
        }
    }
    private sealed class WaitingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new(HttpStatusCode.OK);
        }
    }
}

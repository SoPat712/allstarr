using System.Net;
using System.Net.Http.Headers;
using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.ArtistGrid;

namespace allstarr.Tests.Providers;

public sealed class ArtistGridProviderTests
{
    [Fact]
    public async Task SearchPreservesVersionLabelsAndUsesTheTrackerArtistRatherThanMaintainerCredits()
    {
        using var client = Client(request => request.RequestUri!.AbsolutePath switch
        {
            "/artists.csv" => Response(Csv("Artist", "artist-one", "Tracker maintainer"), "\"index-1\"", "text/csv"),
            "/sh/artist-one/" => Response("""
                {"name":"Artist","tab":{"name":"Demos","gid":"g-1"},"tracks":[
                  {"name":{"raw":"Communicate (v3)","title":"Communicate","credits":["Guest"]},"available_length":"Full","links":[{"url":"https://pillows.su/f/version-3"}]}
                ]}
                """, "\"tracker-1\"", "application/json"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        var result = await Metadata(client).SearchTracksAsync(Context(), new("Artist Communicate v3", new ProviderPageRequest(20)));
        var track = Assert.Single(result.RequireValue().Items);
        Assert.Equal("Communicate (v3)", track.Title);
        Assert.Equal(["Artist", "Guest"], track.Artists.Select(item => item.Name));
        Assert.True($"ext-artistgrid-song-{track.Id.Value}".Length <= 200);
    }
    [Fact]
    public async Task Search_ParsesQuotedCsvAndGroupedAndFlatTracks()
    {
        var payload = TrackerPayload(
            "Artist, One",
            "g-1",
            grouped: "Communicate",
            flat: "Flat Track");
        using var client = Client(request => request.RequestUri!.AbsolutePath switch
        {
            "/artists.csv" => Response(Csv("Artist, One", "artist-one", "Artist, One"), "\"index-1\"", "text/csv"),
            "/sh/artist-one/" => Response(payload, "\"tracker-1\"", "application/json"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        var metadata = Metadata(client);

        var result = await metadata.SearchTracksAsync(Context(), new(
            "Artist, One Communicate",
            new ProviderPageRequest(20)));

        Assert.True(result.IsSuccess);
        var track = Assert.Single(result.RequireValue().Items);
        Assert.Equal("Communicate", track.Title);
        Assert.Equal(TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(43), track.Duration);
        Assert.Equal("Era, One", track.AlbumTitle);
    }

    [Fact]
    public async Task Search_IgnoresUnsupportedLinksAndKeepsDistinctSourceVersions()
    {
        var payload = """
            {"name":"Artist","tab":{"name":"Album","gid":"g-1"},"tracks":[
              {"name":{"title":"Same Song"},"track_length":"1:00","available_length":"Full","links":[{"url":"https://www.youtube.com/watch?v=ignore"},{"url":"https://pillows.su/f/version-b"}]},
              {"name":{"title":"Same Song"},"track_length":"1:00","available_length":"Full","links":[{"url":"https://pixeldrain.com/u/version-a"},{"url":"https://example.invalid/no"}]}
            ]}
            """;
        using var client = Client(request => request.RequestUri!.AbsolutePath switch
        {
            "/artists.csv" => Response(Csv("Artist", "artist"), "\"index-1\"", "text/csv"),
            "/sh/artist/" => Response(payload, "\"tracker-1\"", "application/json"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        var result = await Metadata(client).SearchTracksAsync(Context(), new("Artist Same Song", new(20)));

        Assert.True(result.IsSuccess);
        var tracks = result.RequireValue().Items;
        Assert.Equal(2, tracks.Count);
        Assert.Equal(2, tracks.Select(item => item.Id.Value).Distinct(StringComparer.Ordinal).Count());
        Assert.All(tracks, item => Assert.StartsWith("ag1.", item.Id.Value, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Lookup_RefetchesExactGidAndSurvivesRowAndLinkReordering()
    {
        var initial = TrackerPayload("Artist", "g-1", grouped: "First", flat: "Second", reverseLinks: false);
        var reordered = TrackerPayload("Artist", "g-1", grouped: "Second", flat: "First", reverseLinks: true);
        var exactCalls = 0;
        using var client = Client(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/artists.csv")
                return Response(Csv("Artist", "artist"), "\"index-1\"", "text/csv");
            if (request.RequestUri.AbsolutePath == "/sh/artist/")
                return Response(initial, "\"tracker-1\"", "application/json");
            if (request.RequestUri.AbsolutePath == "/sh/artist/gid/g-1")
            {
                exactCalls++;
                return Response(reordered, "\"tracker-2\"", "application/json");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var metadata = Metadata(client);
        var search = await metadata.SearchTracksAsync(Context(), new("Artist First", new(20)));
        var expected = Assert.Single(search.RequireValue().Items);

        var lookup = await metadata.GetTrackAsync(Context(), new(expected.Id));

        Assert.True(lookup.IsSuccess);
        Assert.Equal(expected.Id.Value, lookup.RequireValue().Id.Value);
        Assert.Equal(1, exactCalls);
    }

    [Fact]
    public async Task Lookup_DoesNotSubstituteAStillNamedTrackWhenItsSourceDisappears()
    {
        var initial = TrackerPayload("Artist", "g-1", grouped: "Same Song", flat: null, sourceId: "original");
        var removed = TrackerPayload("Artist", "g-1", grouped: "Same Song", flat: null, sourceId: "replacement");
        using var client = Client(request => request.RequestUri!.AbsolutePath switch
        {
            "/artists.csv" => Response(Csv("Artist", "artist"), null, "text/csv"),
            "/sh/artist/" => Response(initial, null, "application/json"),
            "/sh/artist/gid/g-1" => Response(removed, null, "application/json"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        var metadata = Metadata(client);
        var search = await metadata.SearchTracksAsync(Context(), new("Artist Same Song", new(20)));
        var oldId = Assert.Single(search.RequireValue().Items).Id;

        var lookup = await metadata.GetTrackAsync(Context(), new(oldId));

        Assert.False(lookup.IsSuccess);
        Assert.Equal(ProviderErrorKind.NotFound, lookup.Error!.Kind);
    }

    [Fact]
    public async Task Search_UsesEtagAfterBoundedCacheExpires()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        var conditionalIndex = false;
        var conditionalTracker = false;
        using var client = Client(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/artists.csv")
            {
                if (request.Headers.IfNoneMatch.Count > 0)
                {
                    conditionalIndex = true;
                    return new HttpResponseMessage(HttpStatusCode.NotModified);
                }
                return Response(Csv("Artist", "artist"), "\"index-1\"", "text/csv");
            }
            if (request.Headers.IfNoneMatch.Count > 0)
            {
                conditionalTracker = true;
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }
            return Response(TrackerPayload("Artist", "g-1", grouped: "Song", flat: null), "\"tracker-1\"", "application/json");
        });
        var metadata = Metadata(client, clock);
        var first = Assert.Single((await metadata.SearchTracksAsync(Context(), new("Artist Song", new(20)))).RequireValue().Items);

        clock.Advance(TimeSpan.FromMinutes(16));
        var second = await metadata.SearchTracksAsync(Context(), new("Artist Song", new(20)));

        Assert.Equal(first.Id, Assert.Single(second.RequireValue().Items).Id);
        Assert.True(conditionalIndex);
        Assert.True(conditionalTracker);
    }

    [Fact]
    public async Task StreamFactory_ForwardsRangeAndFallsBackFromHeadToGet()
    {
        var requests = new List<(HttpMethod Method, string? Range)>();
        using var client = Client(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/artists.csv")
                return Response(Csv("Artist", "artist"), null, "text/csv");
            if (request.RequestUri.AbsolutePath == "/sh/artist/")
                return Response(TrackerPayload("Artist", "g-1", grouped: "Song", flat: null), null, "application/json");
            if (request.RequestUri.AbsolutePath == "/sh/artist/gid/g-1")
                return Response(TrackerPayload("Artist", "g-1", grouped: "Song", flat: null), null, "application/json");
            requests.Add((request.Method, request.Headers.Range?.ToString()));
            if (request.Method == HttpMethod.Head)
                return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
            var response = Response("audio-bytes", null, "audio/mpeg");
            response.StatusCode = HttpStatusCode.PartialContent;
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(4, 14, 15);
            return response;
        });
        var metadata = Metadata(client);
        var search = await metadata.SearchTracksAsync(Context(), new("Artist Song", new(20)));
        var id = Assert.Single(search.RequireValue().Items).Id;
        var streaming = new ArtistGridStreamingCapabilityAdapter(new ArtistGridCatalog(client), client);
        var lease = (await streaming.GetStreamLeaseAsync(Context(), new(id))).RequireValue();
        using var outbound = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri);
        outbound.Headers.Range = new RangeHeaderValue(4, 14);
        using var response = await lease.ProtectedResponseFactory!(outbound, CancellationToken.None);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        using var head = new HttpRequestMessage(HttpMethod.Head, lease.ProtectedSourceUri);
        using var headResponse = await lease.ProtectedResponseFactory!(head, CancellationToken.None);

        Assert.Equal(HttpStatusCode.PartialContent, headResponse.StatusCode);
        Assert.Equal(
            new[]
            {
                (HttpMethod.Get, (string?)"bytes=4-14"),
                (HttpMethod.Head, (string?)null),
                (HttpMethod.Get, (string?)null)
            },
            requests);
    }

    [Theory]
    [InlineData("http://127.0.0.1/secret")]
    [InlineData("http://api.pillows.su/file")]
    [InlineData("https://api.pillows.su:8443/file")]
    public async Task StreamFactory_RejectsUnsafeRedirect(string redirect)
    {
        using var client = Client(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/artists.csv")
                return Response(Csv("Artist", "artist"), null, "text/csv");
            if (request.RequestUri.AbsolutePath == "/sh/artist/gid/g-1")
                return Response(TrackerPayload("Artist", "g-1", grouped: "Song", flat: null), null, "application/json");
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri(redirect);
            return response;
        });
        var streaming = new ArtistGridStreamingCapabilityAdapter(new ArtistGridCatalog(client), client);
        var id = new ProviderExternalResourceId(ArtistGridConstantsForTests.ProviderId,
            ProviderResourceKind.Track,
            ArtistGridConstantsForTests.TrackId("artist", "g-1", "https://pillows.su/f/song"));
        var lease = (await streaming.GetStreamLeaseAsync(Context(), new(id))).RequireValue();
        using var outbound = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri);

        await Assert.ThrowsAsync<HttpRequestException>(() => lease.ProtectedResponseFactory!(outbound, CancellationToken.None));
    }

    [Fact]
    public void Registration_DefaultsToManualAccountFreeMetadataAndStreaming()
    {
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        var catalog = new ArtistGridCatalog(client);
        var registration = ArtistGridProviderRegistration.CreateRegistration(
            new ArtistGridMetadataCapabilityAdapter(catalog),
            new ArtistGridStreamingCapabilityAdapter(catalog, client));

        var validated = ProviderRegistrationValidator.Validate(registration);
        Assert.Equal(ProviderUseMode.ManualOnly, validated.Descriptor.DefaultUseMode);
        Assert.All(validated.Descriptor.Capabilities, capability =>
            Assert.Equal(ProviderAccountRequirement.None, capability.AccountRequirement));
    }

    private static ArtistGridMetadataCapabilityAdapter Metadata(HttpClient client, TimeProvider? clock = null)
    {
        var catalog = new ArtistGridCatalog(client, clock);
        return new ArtistGridMetadataCapabilityAdapter(catalog);
    }

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> handler) =>
        new(new StubHandler(handler)) { Timeout = TimeSpan.FromSeconds(5) };

    private static ProviderExecutionContext Context() => new(
        new ProviderActorContext(ProviderActorKind.PublicRead, null),
        ArtistGridConstantsForTests.ProviderId,
        account: null,
        new ProviderExecutionPolicy(
            new ProviderQualityPolicy(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true),
            ProviderExplicitContentPolicy.Allow,
            allowFallback: true,
            allowSharedAccount: false,
            allowManagedDownloads: false),
        "artistgrid-test",
        "artistgrid-correlation",
        DateTimeOffset.UtcNow.AddMinutes(5),
        CancellationToken.None);

    private static HttpResponseMessage Response(string body, string? etag, string mediaType)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, mediaType)
        };
        if (etag != null) response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        return response;
    }

    private static string Csv(string name, string tracker, string? credit = null) =>
        $"name,url,credit,links_work,updated,best\r\n{Quote(name)},{Quote(tracker)},{Quote(credit ?? name)},true,2026-01-01,true\r\n";

    private static string Quote(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static string TrackerPayload(
        string artist,
        string gid,
        string grouped,
        string? flat,
        bool reverseLinks = false,
        string sourceId = "song")
    {
        var groupedTrack = Track(grouped, sourceId, reverseLinks);
        var flatTrack = flat == null ? string.Empty : ",\"tracks\":[" + Track(flat, sourceId + "-flat", reverseLinks) + "]";
        return $"{{\"name\":\"{artist}\",\"tab\":{{\"name\":\"Era, One\",\"slug\":\"era-one\",\"gid\":\"{gid}\"}},\"eras\":[{{\"name\":\"Era, One\",\"tracks\":[{groupedTrack}]}}]{flatTrack}}}";
    }

    private static string Track(string title, string sourceId, bool reverseLinks)
    {
        var links = new[]
        {
            $"{{\"url\":\"https://pillows.su/f/{sourceId}\",\"text\":\"pillows\"}}",
            $"{{\"url\":\"https://www.youtube.com/watch?v={sourceId}\",\"text\":\"youtube\"}}"
        };
        if (reverseLinks) Array.Reverse(links);
        return $"{{\"name\":{{\"raw\":\"{title}\",\"title\":\"{title}\",\"credits\":[\"Artist\"]}},\"track_length\":\"3:43\",\"available_length\":\"Full\",\"links\":[{string.Join(',', links)}],\"quality\":\"mp3\"}}";
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }

    private static class ArtistGridConstantsForTests
    {
        public const string ProviderId = "artistgrid";

        public static string TrackId(string tracker, string gid, string source) =>
            "ag1." + Convert.ToBase64String(Encoding.UTF8.GetBytes(tracker)).TrimEnd('=').Replace('+', '-').Replace('/', '_') +
            "." + Convert.ToBase64String(Encoding.UTF8.GetBytes(gid)).TrimEnd('=').Replace('+', '-').Replace('/', '_') +
            "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
    }
}

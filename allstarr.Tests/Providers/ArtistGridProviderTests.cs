using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.ArtistGrid;

namespace allstarr.Tests.Providers;

public sealed partial class ArtistGridProviderTests
{
    [Theory]
    [InlineData("ogg", "audio/ogg", "ogg", "unknown")]
    [InlineData("flac", "audio/flac", "flac", "flac")]
    [InlineData("wav", "audio/wav", "wav", "unknown")]
    [InlineData("aac", "audio/aac", "aac", "aac")]
    [InlineData("m4a", "audio/mp4", "mp4", "unknown")]
    [InlineData("MP3", "audio/mpeg", "mp3", "mp3")]
    public async Task DirectAudioWithoutQualityPreservesMediaFormat(string extension, string mime, string container, string codec)
    {
        var url = $"https://files.example/song.{extension}?fixture=1";
        using var client = SourceClient(url, _ => Response("audio", null, mime));
        var streaming = new ArtistGridStreamingCapabilityAdapter(new ArtistGridCatalog(client), client);
        var lease = (await streaming.GetStreamLeaseAsync(Context(), new(SourceId(url)))).RequireValue();
        Assert.Equal(new ProviderMediaFormat(mime, container, codec), lease.Media);
        var probe = (await streaming.ProbeStreamAsync(Context(), new(SourceId(url)))).RequireValue();
        Assert.Equal(lease.Media, probe.Media);
    }

    [Theory]
    [InlineData("https://pillows.su/f/song.mp3", "https://api.pillows.su/api/download/song.mp3")]
    [InlineData("https://drive.google.com/file/d/song.mp3", "https://fuck-unvaulted.artistgrid.cx/gd/song.mp3")]
    [InlineData("https://music.froste.lol/song/abc012", "https://music.froste.lol/song/abc012/download")]
    [InlineData("https://music.froste.lol/song/abc012/download", "https://music.froste.lol/song/abc012/download")]
    [InlineData("https://files.example/song.flac", "https://files.example/song.flac")]
    [InlineData("https://files.example/song.MP3?token=fixture", "https://files.example/song.MP3?token=fixture")]
    [InlineData("https://files.example/song.m4a", "https://files.example/song.m4a")]
    [InlineData("https://files.example/song.aac", "https://files.example/song.aac")]
    [InlineData("https://files.example/song.wav", "https://files.example/song.wav")]
    [InlineData("https://files.example/song.ogg", "https://files.example/song.ogg")]
    public async Task StreamFactory_ResolvesAudioLinksAndPreservesRangesAndHeadFallback(string url, string endpoint)
    {
        var requests = new List<(HttpMethod Method, string? Range)>();
        using var client = SourceClient(url, request =>
        {
            Assert.Equal(endpoint, request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            Assert.Null(request.Headers.Referrer);
            Assert.False(request.Headers.Contains("Cookie"));
            requests.Add((request.Method, request.Headers.Range?.ToString()));
            if (request.Method == HttpMethod.Head) return new(HttpStatusCode.MethodNotAllowed);
            var response = Response("audio", null, "audio/flac");
            response.StatusCode = HttpStatusCode.PartialContent;
            if (request.Headers.Range?.ToString() == "bytes=0-0")
            {
                response.Content = new StringContent("a", Encoding.UTF8, "audio/flac");
                response.Content.Headers.ContentRange = new(0, 0, 1000);
            }
            return response;
        });
        var streaming = new ArtistGridStreamingCapabilityAdapter(new ArtistGridCatalog(client), client);
        var lease = (await streaming.GetStreamLeaseAsync(Context(), new(SourceId(url)))).RequireValue();
        Assert.Equal(endpoint, lease.ProtectedSourceUri!.AbsoluteUri);
        using var outbound = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri);
        outbound.Headers.Range = new RangeHeaderValue(0, 3);
        outbound.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "fixture");
        outbound.Headers.Add("Cookie", "fixture=value");
        using var response = await lease.ProtectedResponseFactory!(outbound, CancellationToken.None);
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.True((await streaming.ProbeStreamAsync(Context(), new(SourceId(url)))).RequireValue().Available);
        Assert.Equal(new[] { (HttpMethod.Get, (string?)"bytes=0-3"), (HttpMethod.Get, (string?)"bytes=0-0") }, requests);
    }

    [Theory]
    [InlineData("http://files.example/song.mp3")]
    [InlineData("https://127.0.0.1/song.mp3")]
    [InlineData("https://8.8.8.8/song.mp3")]
    [InlineData("https://[::1]/song.mp3")]
    [InlineData("https://100.64.0.1/song.mp3")]
    [InlineData("https://localhost/song.mp3")]
    [InlineData("https://music.local/song.mp3")]
    [InlineData("https://intranet/song.mp3")]
    [InlineData("https://files.example:8443/song.mp3")]
    [InlineData("https://user@files.example/song.mp3")]
    [InlineData("https://files.example/song.mp4")]
    [InlineData("https://files.example/song.opus")]
    [InlineData("https://files.example/song.mp3.html")]
    [InlineData("https://files.example/page?name=song.mp3")]
    [InlineData("https://files.example/page")]
    [InlineData("https://music.froste.lol.evil.example/song/abc012")]
    [InlineData("https://music.froste.lol:8443/song/abc012")]
    [InlineData("https://user@music.froste.lol/song/abc012")]
    [InlineData("https://music.froste.lol/song/not-hex")]
    [InlineData("https://music.froste.lol/song/ABC012")]
    [InlineData("https://music.froste.lol/song/abc012/other")]
    public async Task Search_RejectsUnsafeOrUnsupportedAudioLinks(string url)
    {
        using var client = SourceClient(url, _ => throw new InvalidOperationException("Unexpected media request."));
        var result = await Metadata(client).SearchTracksAsync(Context(), new("Artist Song", new(20)));
        Assert.Empty(result.RequireValue().Items);
        var lease = await new ArtistGridStreamingCapabilityAdapter(new ArtistGridCatalog(client), client)
            .GetStreamLeaseAsync(Context(), new(SourceId(url)));
        Assert.False(lease.IsSuccess);
        Assert.Equal(ProviderErrorKind.NotFound, lease.Error!.Kind);
    }

    [Theory]
    [InlineData("https://music.froste.lol/song/abc012", "/media/song.flac", true)]
    [InlineData("https://files.example/song.mp3", "/media/song.mp3?fixture=1", true)]
    [InlineData("https://music.froste.lol/song/abc012", "https://files.example/song.mp3", false)]
    [InlineData("https://music.froste.lol/song/abc012", "https://music.froste.lol.evil.example/file", false)]
    [InlineData("https://music.froste.lol/song/abc012", "http://music.froste.lol/file", false)]
    [InlineData("https://music.froste.lol/song/abc012", "https://music.froste.lol:8443/file", false)]
    [InlineData("https://music.froste.lol/song/abc012", "https://user@music.froste.lol/file", false)]
    [InlineData("https://files.example/song.mp3", "https://other.example/song.mp3", false)]
    [InlineData("https://files.example/song.mp3", "https://127.0.0.1/song.mp3", false)]
    public async Task StreamFactory_RestrictsAudioRedirectsToTheOriginalHost(string url, string target, bool allowed)
    {
        var calls = 0;
        using var client = SourceClient(url, request =>
        {
            calls++;
            Assert.Equal(new Uri(url).Host, request.RequestUri!.Host);
            Assert.Equal("bytes=1-3", request.Headers.Range?.ToString());
            if (calls > 1) return Response("audio", null, "application/octet-stream");
            var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
            redirect.Headers.Location = new Uri(target, UriKind.RelativeOrAbsolute);
            return redirect;
        });
        var lease = (await new ArtistGridStreamingCapabilityAdapter(new ArtistGridCatalog(client), client)
            .GetStreamLeaseAsync(Context(), new(SourceId(url)))).RequireValue();
        using var request = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri);
        request.Headers.Range = new RangeHeaderValue(1, 3);
        if (allowed)
        {
            using var response = await lease.ProtectedResponseFactory!(request, CancellationToken.None);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(2, calls);
        }
        else
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => lease.ProtectedResponseFactory!(request, CancellationToken.None));
            Assert.Equal(1, calls);
        }
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("application/xhtml+xml")]
    [InlineData("application/json")]
    [InlineData(null)]
    public async Task StreamFactory_RejectsNonAudioContent(string? mediaType)
    {
        using var client = SourceClient("https://files.example/song.mp3", _ =>
        {
            var response = Response("<html>fixture</html>", null, "text/html");
            response.Content.Headers.ContentType = mediaType == null ? null : new(mediaType);
            return response;
        });
        var streaming = new ArtistGridStreamingCapabilityAdapter(new ArtistGridCatalog(client), client);
        var id = SourceId("https://files.example/song.mp3");
        var lease = (await streaming.GetStreamLeaseAsync(Context(), new(id))).RequireValue();
        using var request = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri);
        await Assert.ThrowsAsync<HttpRequestException>(() => lease.ProtectedResponseFactory!(request, CancellationToken.None));
        Assert.False((await streaming.ProbeStreamAsync(Context(), new(id))).IsSuccess);
    }

    [Fact]
    public async Task StreamFactory_RejectsOversizedAudio()
    {
        const string url = "https://music.froste.lol/song/abc012";
        using var client = SourceClient(url, _ =>
        {
            var response = Response("audio", null, "audio/mpeg");
            response.Content.Headers.ContentLength = 2L * 1024 * 1024 * 1024;
            return response;
        });
        var lease = (await new ArtistGridStreamingCapabilityAdapter(new ArtistGridCatalog(client), client)
            .GetStreamLeaseAsync(Context(), new(SourceId(url)))).RequireValue();
        using var request = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri);
        await Assert.ThrowsAsync<HttpRequestException>(() => lease.ProtectedResponseFactory!(request, CancellationToken.None));
    }

    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.0.1")]
    [InlineData("100.64.0.1")]
    [InlineData("::1")]
    [InlineData("fc00::1")]
    [InlineData("fe80::1")]
    [InlineData("::ffff:10.0.0.1")]
    public async Task Transport_RejectsPrivateDnsAnswersBeforeConnecting(string address)
    {
        var connected = false;
        await Assert.ThrowsAsync<HttpRequestException>(async () => await ArtistGridHttpTransport.ConnectAsync(
            "files.example", 443, CancellationToken.None,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Parse(address) }),
            (_, _, _) => { connected = true; return ValueTask.FromResult<Stream>(new MemoryStream()); }));
        Assert.False(connected);
    }

    [Fact]
    public async Task Transport_ConnectsToValidatedDnsAnswerWithoutResolvingAgain()
    {
        var resolutions = 0;
        using var stream = await ArtistGridHttpTransport.ConnectAsync("files.example", 443, CancellationToken.None,
            (host, _) =>
            {
                Assert.Equal("files.example", host);
                resolutions++;
                return Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") });
            },
            (address, port, _) =>
            {
                Assert.Equal(IPAddress.Parse("8.8.8.8"), address);
                Assert.Equal(443, port);
                return ValueTask.FromResult<Stream>(new MemoryStream());
            });
        Assert.Equal(1, resolutions);
    }

    [Fact]
    public async Task AudioContent_BoundsUnknownLengthBodiesAndDisposesTheSource()
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes("12345"));
        using (var content = new ArtistGridAudioContent(new UnknownLengthContent(stream), 4))
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => content.ReadAsByteArrayAsync());
        }
        Assert.False(stream.CanRead);
    }

    [Fact]
    public async Task Transport_RejectsEmptyDnsAnswers()
    {
        await Assert.ThrowsAsync<HttpRequestException>(async () => await ArtistGridHttpTransport.ConnectAsync(
            "files.example", 443, CancellationToken.None,
            (_, _) => Task.FromResult(Array.Empty<IPAddress>()),
            (_, _, _) => throw new InvalidOperationException("Unexpected connection.")));
    }

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

    private static ProviderExternalResourceId SourceId(string url) => new("artistgrid", ProviderResourceKind.Track,
        ArtistGridConstantsForTests.TrackId("artist", "g-1", url));

    private static HttpClient SourceClient(string url, Func<HttpRequestMessage, HttpResponseMessage> media) =>
        Client(request => request.RequestUri!.Host switch
        {
            "artists.artistgrid.cx" => Response(Csv("Artist", "artist"), null, "text/csv"),
            "trackerapi.artistgrid.cx" => Response(JsonSerializer.Serialize(new
            {
                name = "Artist",
                tab = new { name = "Album", gid = "g-1" },
                tracks = new[] { new { name = new { title = "Song" }, available_length = "Full", links = new[] { new { url } } } }
            }), null, "application/json"),
            _ => media(request)
        });

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

    private sealed class UnknownLengthContent(Stream stream) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream target, TransportContext? context) => stream.CopyToAsync(target);
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(stream);
        protected override void Dispose(bool disposing)
        {
            if (disposing) stream.Dispose();
            base.Dispose(disposing);
        }
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

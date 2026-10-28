using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Music;
using allstarr.Core.Providers.ArtistGrid;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using allstarr.Core.Settings;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData("Jellyfin", "Items", "json", "Communicate Ariana Grande")]
    [InlineData("Subsonic", "search3", "json", "Communicate Ariana Grande")]
    [InlineData("Subsonic", "search3", "xml", "Communicate Ariana Grande")]
    [InlineData("Subsonic", "search2", "json", "Communicate Ariana Grande")]
    [InlineData("Subsonic", "search2", "xml", "Communicate Ariana Grande")]
    [InlineData("Jellyfin", "Items", "json", "Communicate")]
    [InlineData("Subsonic", "search3", "json", "Communicate")]
    [InlineData("Subsonic", "search3", "xml", "Communicate")]
    public Task ArtistGridAlternateTitleSurvivesClientSearchAndSerializedCache(
        string backend, string method, string format, string query) =>
        AssertArtistGridAliasSearchAsync(backend, method, format, query, ProviderUseMode.ManualOnly);

    [Theory]
    [InlineData("Jellyfin", "Items", "json")]
    [InlineData("Subsonic", "search3", "json")]
    [InlineData("Subsonic", "search3", "xml")]
    public Task ArtistGridOffRemainsExcludedFromClientSearchAndSerializedCache(string backend, string method, string format) =>
        AssertArtistGridAliasSearchAsync(backend, method, format, "Communicate Ariana Grande", ProviderUseMode.Off);

    private static async Task AssertArtistGridAliasSearchAsync(
        string backend, string method, string format, string query, ProviderUseMode mode)
    {
        var user = Guid.CreateVersion7();
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var cache = new TestMemoryApplicationCache();
        var providerRequests = new List<string>();
        var capabilityCalls = 0;
        var observed = new List<List<ArtistGridAliasRow>>();
        string[]? firstIds = null;
        string[]? firstArtwork = null;
        for (var cycle = 0; cycle < 2; cycle++)
        {
            using var catalogClient = new HttpClient(new StubHttpMessageHandler(request =>
            {
                Assert.Equal(0, cycle);
                providerRequests.Add(request.RequestUri!.AbsolutePath);
                return request.RequestUri.AbsolutePath switch
                {
                    "/artists.csv" => Json(200, "name,url,best\nAriana Grande,ariana-grande,false"),
                    "/sh/ariana-grande/" => Json(200, File.ReadAllText(Path.Combine(
                        AppContext.BaseDirectory, "Fixtures", "ArtistGrid", "communicate.json"))),
                    _ => throw new InvalidOperationException("Unexpected catalog fixture request.")
                };
            }));
            var catalog = new ArtistGridCatalog(catalogClient, clock);
            var unrelated = ArtistGridAliasCompetitors(() => capabilityCalls++);
            var registration = ArtistGridProviderRegistration.CreateRegistration(
                new ArtistGridMetadataCapabilityAdapter(catalog), new ArtistGridStreamingCapabilityAdapter(catalog, catalogClient));
            var registry = new ProviderRegistry([registration, ArtistGridAliasRegistration(unrelated.Object)]);
            var policies = ArtistGridAliasPolicies(user, mode);
            var router = ArtistGridAliasRouter(registry, policies.Object);
            var gateway = new ProtocolProviderGateway(router, registry, Mock.Of<IHttpClientFactory>(MockBehavior.Strict),
                applicationCache: cache, effectivePolicies: policies.Object, timeProvider: clock, audioVerifier: FixtureAudioVerifier.Instance);
            using var host = new ProtocolFactory(backend, request => ArtistGridAliasNative(request, backend, method, format),
                services =>
                {
                    services.AddSingleton<IStartupFilter>(new ArtworkPrincipalStartupFilter(new AllstarrPrincipal(
                        user, backend.ToLowerInvariant(), "primary", backend == "Jellyfin" ? "user-1" : "fixture", "Listener", false)));
                    services.RemoveAll<IEffectiveProviderPolicyResolver>();
                    services.AddSingleton(policies.Object);
                    services.RemoveAll<IProtocolProviderGateway>();
                    services.AddSingleton<IProtocolProviderGateway>(gateway);
                    services.RemoveAll<MusicSearchService>();
                    services.AddSingleton(new MusicSearchService(gateway));
                }, new Dictionary<string, string?> { [$"{backend}:EnableExternalPlaylists"] = "false" });
            using var client = host.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            var encodedQuery = Uri.EscapeDataString(query);
            var path = backend == "Jellyfin"
                ? $"/Items?SearchTerm={encodedQuery}&IncludeItemTypes=Audio&Recursive=true&Limit=50&UserId=user-1&api_key=fixture-key"
                : $"/rest/{method}?u=fixture&p=secret&v=1.16.1&c=fixture&query={encodedQuery}&songCount=50&albumCount=0&artistCount=0&f={format}";
            using var response = await client.GetAsync(path);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.DoesNotContain("AlternateTitles", body, StringComparison.OrdinalIgnoreCase);
            if (backend == "Jellyfin")
            {
                using var payload = JsonDocument.Parse(body);
                Assert.Equal(mode == ProviderUseMode.Off ? 47 : 51,
                    payload.RootElement.GetProperty("TotalRecordCount").GetInt32());
            }
            var rows = ArtistGridAliasRows(body, backend, method, format);
            Assert.Equal(mode == ProviderUseMode.Off ? 47 : backend == "Jellyfin" ? 50 : 51, rows.Count);
            observed.Add(rows);
            var native = Assert.Single(rows, row => row.Id == "native-sentinel");
            Assert.Equal("Communicate", native.Title);
            Assert.Equal(200, native.Duration);
            var tracks = rows.Where(row => row.Id.StartsWith("ext-artistgrid-", StringComparison.Ordinal)).ToArray();
            if (cycle == 0)
            {
                firstIds = tracks.Select(row => row.Id).ToArray();
                firstArtwork = tracks.Select(row => row.Artwork).ToArray();
            }
            else
            {
                Assert.Equal(firstIds, tracks.Select(row => row.Id));
                Assert.Equal(firstArtwork, tracks.Select(row => row.Artwork));
            }
            unrelated.Verify(item => item.GetTrackAsync(It.IsAny<ProviderExecutionContext>(),
                It.IsAny<ProviderTrackLookupRequest>()), Times.Never);
        }
        Assert.Equal(mode == ProviderUseMode.Off ? [] : new[] { "/artists.csv", "/sh/ariana-grande/" }, providerRequests);
        Assert.Equal(1, capabilityCalls);
        Assert.Equal(2, observed.Count);
        foreach (var rows in observed)
        {
            var tracks = rows.Where(row => row.Id.StartsWith("ext-artistgrid-", StringComparison.Ordinal)).ToArray();
            if (mode == ProviderUseMode.Off)
            {
                Assert.Empty(tracks);
                continue;
            }
            Assert.Equal(4, tracks.Length);
            Assert.Equal(4, tracks.Select(row => row.Id).Distinct(StringComparer.Ordinal).Count());
            Assert.Contains(tracks, row => row.Title.Contains("Diva Shit [V1]", StringComparison.Ordinal) && row.Duration == 156);
            Assert.Equal(3, tracks.Count(row => row.Duration == 115));
            var laterVersions = tracks.Where(row => !row.Title.Contains("[V1]", StringComparison.Ordinal)).ToArray();
            Assert.Contains("⭐", laterVersions[0].Title, StringComparison.Ordinal);
            Assert.Contains("[V3]", laterVersions[0].Title, StringComparison.Ordinal);
            Assert.Contains("[V2]", laterVersions[1].Title, StringComparison.Ordinal);
            Assert.Contains("[V4]", laterVersions[2].Title, StringComparison.Ordinal);
            Assert.Single(tracks, row => row.Title.Contains('⭐'));
            if (query.Contains("Ariana Grande", StringComparison.Ordinal)) Assert.Equal(laterVersions[0], tracks[0]);
            Assert.All(tracks, row => Assert.NotEmpty(row.Artwork));
            Assert.True(rows.FindIndex(row => row.Title.Contains("Diva Shit [V1]", StringComparison.Ordinal)) <
                rows.FindIndex(row => row.Id.Contains("competitor-", StringComparison.Ordinal)));
        }
    }

    private static Mock<IProviderMetadataCapability> ArtistGridAliasCompetitors(Action called)
    {
        var capability = new Mock<IProviderMetadataCapability>(MockBehavior.Strict);
        capability.SetupGet(item => item.ProviderId).Returns("deezer");
        capability.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Metadata);
        capability.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .Callback(called).ReturnsAsync(ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("deezer",
                Enumerable.Range(0, 46).Select(index => new ProviderTrackMetadata(
                    new("deezer", ProviderResourceKind.Track, $"competitor-{index}"), "Communicate (Live)",
                    [new("Ariana Grande")], duration: TimeSpan.FromSeconds(115))).ToArray())));
        return capability;
    }

    private static ProviderRegistration ArtistGridAliasRegistration(IProviderMetadataCapability metadata) => new(
        new(metadata.ProviderId, "Fixture source", "Fixture metadata and streaming", ProviderOrigin.BuiltIn, "1", "1",
            [new(ProviderCapabilityKind.Metadata, ProviderCapabilitySupportState.Supported, ProviderAccountRequirement.None,
                "1", ["searchTracks", "getTrack"]), new(ProviderCapabilityKind.Streaming, ProviderCapabilitySupportState.Supported,
                ProviderAccountRequirement.None, "1", ["getStreamLease"])], new()),
        [metadata, Mock.Of<IProviderStreamingCapability>(item => item.ProviderId == metadata.ProviderId &&
            item.Capability == ProviderCapabilityKind.Streaming, MockBehavior.Strict)]);

    private static Mock<IEffectiveProviderPolicyResolver> ArtistGridAliasPolicies(Guid user, ProviderUseMode mode)
    {
        var policies = new Mock<IEffectiveProviderPolicyResolver>(MockBehavior.Strict);
        policies.Setup(item => item.ResolveForUserAsync(user, It.IsAny<CancellationToken>())).ReturnsAsync(
            new EffectiveProviderPolicySnapshot(new Dictionary<ProviderCapabilityKind, ImmutableArray<string>>
            {
                [ProviderCapabilityKind.Metadata] = ["deezer", "artistgrid"],
                [ProviderCapabilityKind.Streaming] = ["deezer", "artistgrid"]
            }.ToImmutableDictionary(), ImmutableHashSet<string>.Empty, AudioQualityPolicy.DefaultStep, 0.07)
            {
                UserId = user,
                ProviderModes = ImmutableDictionary<string, ProviderUseMode>.Empty.Add("artistgrid", mode)
            });
        return policies;
    }

    private static ProviderRouter ArtistGridAliasRouter(ProviderRegistry registry, IEffectiveProviderPolicyResolver policies)
    {
        var health = new Mock<IProviderRouteHealthSource>(MockBehavior.Strict);
        health.Setup(item => item.Get(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<ProviderCapabilityKind>()))
            .Returns(new ProviderRouteHealthSnapshot(ProviderRouteHealthState.Healthy, false));
        return new(registry, Mock.Of<IProviderRouteAccountResolver>(MockBehavior.Strict), health.Object,
            Mock.Of<IProviderRouteSidecarSource>(MockBehavior.Strict), Mock.Of<ITrackIdentityService>(MockBehavior.Strict),
            effectivePolicies: policies);
    }

    private const string ArtistGridAliasNativeJson = """
        {"Id":"native-sentinel","Type":"Audio","Name":"Communicate","Artists":["Ariana Grande"],"RunTimeTicks":2000000000,"UnknownFutureField":{"Keep":"native"}}
        """;
    private const string ArtistGridAliasNativeSubsonicJson = """
        {"id":"native-sentinel","title":"Communicate","artist":"Ariana Grande","duration":200,"unknownFutureField":{"keep":"native"},"isExternal":false}
        """;

    private static HttpResponseMessage ArtistGridAliasNative(HttpRequestMessage request, string backend, string method, string format)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path == "/Users/Me") return Json(200, "{\"Id\":\"user-1\"}");
        if (path.Contains("ping", StringComparison.Ordinal))
            return Json(200, "{\"subsonic-response\":{\"status\":\"ok\",\"version\":\"1.16.1\"}}");
        if (backend == "Jellyfin" && path == "/Items")
            return Json(200, $"{{\"Items\":[{ArtistGridAliasNativeJson}],\"TotalRecordCount\":1}}");
        if (backend == "Subsonic" && path.Contains(method, StringComparison.Ordinal))
            return format == "json"
                ? Json(200, $"{{\"subsonic-response\":{{\"status\":\"ok\",\"searchResult{method[^1]}\":{{\"song\":[{ArtistGridAliasNativeSubsonicJson}]}}}}}}")
                : new(HttpStatusCode.OK)
                {
                    Content = new StringContent($"<subsonic-response xmlns=\"http://subsonic.org/restapi\" status=\"ok\"><searchResult{method[^1]}><song id=\"native-sentinel\" title=\"Communicate\" artist=\"Ariana Grande\" duration=\"200\" custom=\"native\" isExternal=\"false\" /></searchResult{method[^1]}></subsonic-response>",
                    System.Text.Encoding.UTF8, "application/xml")
                };
        throw new InvalidOperationException("Unexpected native search fixture request.");
    }

    private static List<ArtistGridAliasRow> ArtistGridAliasRows(string body, string backend, string method, string format)
    {
        if (format == "xml")
        {
            var rows = XDocument.Parse(body).Descendants().Where(item => item.Name.LocalName == "song").ToArray();
            var native = Assert.Single(rows, item => item.Attribute("id")?.Value == "native-sentinel");
            Assert.Equal("native", native.Attribute("custom")?.Value);
            Assert.Equal(6, native.Attributes().Count(attribute => !attribute.IsNamespaceDeclaration));
            Assert.Equal("false", native.Attribute("isExternal")?.Value);
            return rows.Select(item => new ArtistGridAliasRow(item.Attribute("id")!.Value, item.Attribute("title")!.Value,
                int.Parse(item.Attribute("duration")!.Value), item.Attribute("coverArt")?.Value ?? "")).ToList();
        }
        using var payload = JsonDocument.Parse(body);
        var items = backend == "Jellyfin" ? payload.RootElement.GetProperty("Items") : payload.RootElement
            .GetProperty("subsonic-response").GetProperty($"searchResult{method[^1]}").GetProperty("song");
        var id = backend == "Jellyfin" ? "Id" : "id";
        var nativeJson = Assert.Single(items.EnumerateArray(), item => item.GetProperty(id).GetString() == "native-sentinel");
        Assert.Equal(backend == "Jellyfin" ? ArtistGridAliasNativeJson : ArtistGridAliasNativeSubsonicJson, nativeJson.GetRawText());
        return items.EnumerateArray().Select(item => new ArtistGridAliasRow(item.GetProperty(id).GetString()!,
            item.GetProperty(backend == "Jellyfin" ? "Name" : "title").GetString()!,
            backend == "Jellyfin" ? (int)(item.GetProperty("RunTimeTicks").GetInt64() / TimeSpan.TicksPerSecond)
                : item.GetProperty("duration").GetInt32(),
            backend == "Jellyfin" ? item.TryGetProperty("ImageTags", out var tags) && tags.TryGetProperty("Primary", out var primary)
                ? primary.GetString() ?? "" : ""
                : item.TryGetProperty("coverArt", out var art) ? art.GetString() ?? "" : "")).ToList();
    }

    private sealed record ArtistGridAliasRow(string Id, string Title, int Duration, string Artwork);
}

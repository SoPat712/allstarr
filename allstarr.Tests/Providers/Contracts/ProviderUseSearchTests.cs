using System.Collections.Immutable;
using System.Net;
using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Providers.ArtistGrid;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using allstarr.Core.Settings;
using Moq;

namespace allstarr.Tests;

public sealed class ProviderUseSearchTests
{
    [Theory]
    [InlineData(ProtocolKind.Jellyfin, ProviderUseMode.ManualOnly)]
    [InlineData(ProtocolKind.Subsonic, ProviderUseMode.ManualOnly)]
    [InlineData(ProtocolKind.Jellyfin, ProviderUseMode.Off)]
    [InlineData(ProtocolKind.Subsonic, ProviderUseMode.Off)]
    public async Task ArtistGridTrackSearchUsesManualSelectionForBothClientProtocols(
        ProtocolKind protocol, ProviderUseMode mode)
    {
        var requests = new List<string>();
        using var client = new HttpClient(new SearchFixtureHandler(request =>
        {
            var uri = request.RequestUri!.AbsoluteUri;
            requests.Add(uri);
            return uri switch
            {
                "https://artists.artistgrid.cx/artists.csv" => FixtureResponse("""
                    name,url,credit,links_work,updated,best
                    "Ariana Grande","ariana-grande","Tracker maintainer",true,2026-01-01,true
                    "Another Artist","another-artist","Another Artist",true,2026-01-01,true
                    """, "text/csv"),
                "https://trackerapi.artistgrid.cx/sh/ariana-grande/" => FixtureResponse(
                    File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ArtistGrid", "communicate.json")),
                    "application/json"),
                _ => throw new InvalidOperationException($"Unexpected fixture request: {uri}")
            };
        }));
        var catalog = new ArtistGridCatalog(client);
        var registry = new ProviderRegistry([ArtistGridProviderRegistration.CreateRegistration(
            new ArtistGridMetadataCapabilityAdapter(catalog),
            new ArtistGridStreamingCapabilityAdapter(catalog, client))]);
        var user = Guid.CreateVersion7();
        var policy = new EffectiveProviderPolicySnapshot(
            ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
            ImmutableHashSet<string>.Empty, "High", 0.07)
        { ProviderModes = ImmutableDictionary<string, ProviderUseMode>.Empty.Add("artistgrid", mode) };
        var policies = new Mock<IEffectiveProviderPolicyResolver>(MockBehavior.Strict);
        policies.Setup(item => item.ResolveForUserAsync(user, It.IsAny<CancellationToken>())).ReturnsAsync(policy);
        var health = new Mock<IProviderRouteHealthSource>();
        health.Setup(item => item.Get(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<ProviderCapabilityKind>()))
            .Returns(new ProviderRouteHealthSnapshot(ProviderRouteHealthState.Healthy, false));
        var router = new ProviderRouter(registry, Mock.Of<IProviderRouteAccountResolver>(), health.Object,
            Mock.Of<IProviderRouteSidecarSource>(), new Mock<ITrackIdentityService>(MockBehavior.Strict).Object,
            effectivePolicies: policies.Object);
        var gateway = new ProtocolProviderGateway(router, registry, Mock.Of<IHttpClientFactory>(MockBehavior.Strict),
            effectivePolicies: policies.Object, audioVerifier: FixtureAudioVerifier.Instance);
        var context = new ProtocolExecutionContext(protocol, "fixture", "listener",
            new AllstarrPrincipal(user, protocol.ToString().ToLowerInvariant(), "fixture", "listener", "Listener", false),
            "search", DateTimeOffset.UtcNow.AddMinutes(1), default);

        Assert.Empty(await gateway.SearchAutomaticSongsAsync(context, "communicate ariana grande", 20));
        Assert.Empty(requests);

        var result = await gateway.SearchAsync(context, "communicate ariana grande", 20, 0, 0);

        Assert.Empty(result.Albums);
        Assert.Empty(result.Artists);
        if (mode == ProviderUseMode.Off)
        {
            Assert.Empty(result.Songs);
            Assert.Empty(requests);
        }
        else
        {
            Assert.Equal(4, result.Songs.Count);
            Assert.Equal(4, result.Songs.Select(song => song.ExternalId).Distinct().Count());
            Assert.All(result.Songs, song =>
            {
                Assert.Equal("artistgrid", song.ExternalProvider);
                Assert.Equal("Ariana Grande", song.Artist);
                Assert.Equal("Positions", song.Album);
                Assert.StartsWith("ag1.", song.ExternalId, StringComparison.Ordinal);
            });
            Assert.Equal(3, result.Songs.Count(song => song.PreciseDuration == TimeSpan.FromSeconds(115)));
            Assert.Contains(result.Songs, song => song.Title == "Diva Shit [V1]");
            Assert.Contains(result.Songs, song => song.Title == "Communicate [V3] [OG File]");
            Assert.Equal(new[]
            {
                "https://artists.artistgrid.cx/artists.csv",
                "https://trackerapi.artistgrid.cx/sh/ariana-grande/"
            }, requests);
        }

        var requestCount = requests.Count;
        Assert.Empty(await gateway.SearchAutomaticSongsAsync(context, "communicate ariana grande", 20));
        Assert.Equal(requestCount, requests.Count);
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin, ProviderUseMode.ManualOnly)]
    [InlineData(ProtocolKind.Subsonic, ProviderUseMode.ManualOnly)]
    [InlineData(ProtocolKind.Jellyfin, ProviderUseMode.Off)]
    [InlineData(ProtocolKind.Subsonic, ProviderUseMode.Off)]
    public async Task PersonSearchIncludesManualSourcesAndHidesOffSources(ProtocolKind protocol, ProviderUseMode mode)
    {
        var user = Guid.CreateVersion7();
        var policy = new EffectiveProviderPolicySnapshot(
            ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
            ImmutableHashSet<string>.Empty, "High", 0.07)
        { ProviderModes = ImmutableDictionary<string, ProviderUseMode>.Empty.Add("source", mode) };
        var policies = new Mock<IEffectiveProviderPolicyResolver>(MockBehavior.Strict);
        policies.Setup(item => item.ResolveForUserAsync(user, It.IsAny<CancellationToken>())).ReturnsAsync(policy);
        var metadata = new Mock<IProviderMetadataCapability>(MockBehavior.Strict);
        metadata.SetupGet(item => item.ProviderId).Returns("source");
        metadata.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Metadata);
        metadata.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("source",
                [new(new("source", ProviderResourceKind.Track, "song"), "Song", [new("Artist")])])));
        metadata.Setup(item => item.SearchAlbumsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderAlbumMetadata>>.Success(new("source",
                [new(new("source", ProviderResourceKind.Album, "album"), "Album", [new("Artist")])])));
        metadata.Setup(item => item.SearchArtistsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderArtistMetadata>>.Success(new("source",
                [new(new("source", ProviderResourceKind.Artist, "artist"), "Artist")])));
        var streaming = new Mock<IProviderStreamingCapability>(MockBehavior.Strict);
        streaming.SetupGet(item => item.ProviderId).Returns("source");
        streaming.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Streaming);
        var descriptor = new ProviderDescriptor("source", "Source", "Search source", ProviderOrigin.BuiltIn,
            "1", "1", [
                new(ProviderCapabilityKind.Metadata, ProviderCapabilitySupportState.Supported,
                    ProviderAccountRequirement.None, "1", ["searchTracks", "getTrack", "searchAlbums", "searchArtists"]),
                new(ProviderCapabilityKind.Streaming, ProviderCapabilitySupportState.Supported,
                    ProviderAccountRequirement.None, "1", ["getStreamLease"])
            ], new());
        var registry = new ProviderRegistry([new(descriptor, [metadata.Object, streaming.Object])]);
        var health = new Mock<IProviderRouteHealthSource>();
        health.Setup(item => item.Get(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<ProviderCapabilityKind>()))
            .Returns(new ProviderRouteHealthSnapshot(ProviderRouteHealthState.Healthy, false));
        var router = new ProviderRouter(registry, Mock.Of<IProviderRouteAccountResolver>(), health.Object,
            Mock.Of<IProviderRouteSidecarSource>(), new Mock<ITrackIdentityService>(MockBehavior.Strict).Object,
            effectivePolicies: policies.Object);
        var gateway = new ProtocolProviderGateway(router, registry, Mock.Of<IHttpClientFactory>(MockBehavior.Strict),
            effectivePolicies: policies.Object, audioVerifier: FixtureAudioVerifier.Instance);
        var context = new ProtocolExecutionContext(protocol, "fixture", "listener",
            new AllstarrPrincipal(user, protocol.ToString().ToLowerInvariant(), "fixture", "listener", "Listener", false),
            "search", DateTimeOffset.UtcNow.AddMinutes(1), default);

        Assert.Empty(await gateway.SearchAutomaticSongsAsync(context, "Song", 20));
        metadata.Verify(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(),
            It.IsAny<ProviderMetadataSearchRequest>()), Times.Never);
        var result = await gateway.SearchAsync(context, "Song", 20, 20, 20);
        var matches = await gateway.SearchPlayableSongsAsync(context, "Song", 20);
        if (mode == ProviderUseMode.Off)
        {
            Assert.Empty(result.Songs);
            Assert.Empty(result.Albums);
            Assert.Empty(result.Artists);
            Assert.Empty(matches);
            metadata.Verify(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(),
                It.IsAny<ProviderMetadataSearchRequest>()), Times.Never);
        }
        else
        {
            Assert.Equal("source", Assert.Single(result.Songs).ExternalProvider);
            Assert.Equal("source", Assert.Single(result.Albums).ExternalProvider);
            Assert.Equal("source", Assert.Single(result.Artists).ExternalProvider);
            Assert.Equal("source", Assert.Single(matches).ExternalProvider);
        }
    }

    private static HttpResponseMessage FixtureResponse(string body, string mediaType) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, mediaType)
    };

    private sealed class SearchFixtureHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}

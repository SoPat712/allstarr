using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Models.Search;
using allstarr.Services.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolProviderStreamingGatewayTests
{
    [Fact]
    public async Task CachedAlbumSearch_UsesMemoryForItemIdLookupsWithoutProviderCalls()
    {
        var capability = SearchRankingCapability("deezer");
        var registry = MetadataRegistry(capability);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(item => item.PlanAsync<IProviderMetadataCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => MetadataPlan(request, registry, capability));
        var snapshot = System.Text.Json.JsonSerializer.Serialize(new
        {
            Result = new SearchResult
            {
                Albums = [new Album { Id = "ext-deezer-album-release", ExternalProvider = "deezer",
                ExternalId = "release", Title = "Release", Artist = "Artist" }]
            },
            Durations = Array.Empty<TimeSpan?>(),
            TrackIds = Array.Empty<ProviderExternalResourceId>()
        });
        var cache = new Mock<IApplicationCache>(MockBehavior.Strict);
        cache.SetupGet(item => item.IsEnabled).Returns(true);
        var entries = new Dictionary<string, string>();
        cache.Setup(item => item.GetStringAsync(It.IsAny<string>()))
            .ReturnsAsync((string key) => key.StartsWith("search:v3:", StringComparison.Ordinal) ? snapshot : entries.GetValueOrDefault(key));
        cache.Setup(item => item.SetStringAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan?>()))
            .ReturnsAsync((string key, string value, TimeSpan? expiry) => { entries[key] = value; return true; });
        var identities = new Mock<ITrackIdentityService>(MockBehavior.Strict);
        identities.Setup(item => item.GetStoredMetadataAsync(It.IsAny<ProviderActorContext>(),
            It.IsAny<ProviderExternalResourceId>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        var gateway = new ProtocolProviderGateway(router.Object, registry, new HttpClientFactory(),
            applicationCache: cache.Object, identities: identities.Object, audioVerifier: FixtureAudioVerifier.Instance);

        Mock.Get(capability).Setup(item => item.GetAlbumAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderAlbumLookupRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderAlbumMetadata>.Success(new(new("deezer", ProviderResourceKind.Album, "release"),
                "Release", [new("Artist")], tracks: [new(new("deezer", ProviderResourceKind.Track, "track"), "Song", [new("Artist")], isExplicit: false),
                    new(new("deezer", ProviderResourceKind.Track, "explicit"), "Explicit song", [new("Artist")], isExplicit: true)])));
        identities.Setup(item => item.ResolveManyAsync(It.IsAny<IReadOnlyList<TrackIdentityLookup>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TrackIdentityLookup> lookups, CancellationToken _) => new TrackIdentityResolution?[lookups.Count]);
        var context = SearchRankingContext(ProtocolKind.Jellyfin);
        context = context with
        {
            Policy = new allstarr.Core.Settings.EffectiveProviderPolicySnapshot(
            System.Collections.Immutable.ImmutableDictionary<ProviderCapabilityKind, System.Collections.Immutable.ImmutableArray<string>>.Empty,
            System.Collections.Immutable.ImmutableHashSet<string>.Empty, AudioQualityPolicy.DefaultStep, 0.07)
            { UserId = context.RequireActor().EffectiveUserId, Preferences = new("CleanOnly") }
        };
        var result = await gateway.SearchAsync(context, "Release", 0, 10, 0);

        Assert.Single(result.Albums);
        Assert.Equal("ext-deezer-album-release", (await gateway.GetStoredAlbumAsync(context, "deezer", "release"))!.Id);
        Assert.Null(await gateway.GetStoredAlbumAsync(SearchRankingContext(ProtocolKind.Jellyfin), "deezer", "release"));
        for (var attempt = 0; attempt < 2; attempt++)
            Assert.Single((await gateway.GetAlbumAsync(context, "deezer", "release"))!.Songs);
        var relaxed = context with { Policy = context.Policy! with { Preferences = new("All") } };
        Assert.Equal(2, (await gateway.GetAlbumAsync(relaxed, "deezer", "release"))!.Songs.Count);
        Mock.Get(capability).Verify(item => item.GetAlbumAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderAlbumLookupRequest>()), Times.Once);
        Mock.Get(capability).Verify(item => item.SearchAlbumsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()), Times.Never);
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin, ProviderUseMode.Automatic, true)]
    [InlineData(ProtocolKind.Subsonic, ProviderUseMode.Automatic, true)]
    [InlineData(ProtocolKind.Jellyfin, ProviderUseMode.Off, true)]
    [InlineData(ProtocolKind.Subsonic, ProviderUseMode.Off, true)]
    [InlineData(ProtocolKind.Jellyfin, ProviderUseMode.ManualOnly, true)]
    [InlineData(ProtocolKind.Subsonic, ProviderUseMode.ManualOnly, true)]
    [InlineData(ProtocolKind.Jellyfin, ProviderUseMode.Automatic, false)]
    public async Task AlbumTracks_UseKnownDisplaySourceWithoutTrackLookups(ProtocolKind protocol, ProviderUseMode mode, bool displayAliases)
    {
        var metadata = SearchRankingCapability("deezer");
        Mock.Get(metadata).Setup(item => item.GetAlbumAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderAlbumLookupRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderAlbumMetadata>.Success(new(new("deezer", ProviderResourceKind.Album, "album"),
                "Album", [new("Artist")], tracks: [new(new("deezer", ProviderResourceKind.Track, "song"), "Song", [new("Artist")],
                    alternateTitles: ["Original alias"])])));
        var registry = MetadataRegistry(metadata);
        var router = new Mock<IProviderRouter>();
        router.Setup(item => item.PlanAsync<IProviderMetadataCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => MetadataPlan(request, registry, metadata));
        var identities = new Mock<ITrackIdentityService>();
        const string publicId = "ext-deezer-song-song";
        var recordingId = Guid.NewGuid();
        identities.Setup(item => item.ResolveManyAsync(It.IsAny<IReadOnlyList<TrackIdentityLookup>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TrackIdentityResolution?[] { new(recordingId, Guid.NewGuid(), new("deezer", ProviderResourceKind.Track, "song"),
                ProviderIdentityScope.Catalog, null, ProviderIdentityVerification.Verified, "odesli-link", 1) { PublicId = publicId } });
        identities.Setup(item => item.GetStoredSongsAsync(It.IsAny<ProviderActorContext>(), publicId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Song[] { new() { Id = publicId, CanonicalRecordingId = recordingId, Title = "Song", Artist = "Artist",
                ExternalProvider = "apple-musickit", ExternalId = "preferred", Album = "Preferred album", AlbumId = "ext-apple-download-album-release",
                AlternateTitles = displayAliases ? ["Preferred alias"] : [] } });
        var gateway = new ProtocolProviderGateway(router.Object, registry, new HttpClientFactory(), identities: identities.Object, audioVerifier: FixtureAudioVerifier.Instance);

        var song = Assert.Single((await gateway.GetAlbumAsync(DisplayPolicyContext(protocol, "apple-musickit", mode), "deezer", "album"))!.Songs);

        Assert.Equal(publicId, song.Id);
        if (mode == ProviderUseMode.Automatic)
        {
            Assert.Equal("apple-download", song.ExternalProvider);
            Assert.Equal("Preferred album", song.Album);
            Assert.Equal("ext-apple-download-album-release", song.AlbumId);
            Assert.Equal(displayAliases ? new[] { "Preferred alias" } : [], song.AlternateTitles);
        }
        else
        {
            Assert.Equal("deezer", song.ExternalProvider);
            Assert.Equal("song", song.ExternalId);
            Assert.Equal("Song", song.Title);
            Assert.Null(song.AlbumId);
            Assert.Equal(new[] { "Original alias" }, song.AlternateTitles);
        }
        Mock.Get(metadata).Verify(item => item.GetTrackAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderTrackLookupRequest>()), Times.Never);
    }

    [Fact]
    public async Task SearchAlternateTitlesSurviveSerializationAndIgnoreTheOldPageNamespace()
    {
        var cache = new TestMemoryApplicationCache();
        var capability = Mock.Get(SearchRankingCapability("deezer"));
        var calls = 0;
        var failProvider = false;
        capability.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .ReturnsAsync(() =>
            {
                Assert.False(failProvider);
                calls++;
                return ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("deezer",
                    [new(new("deezer", ProviderResourceKind.Track, "original"), "Renamed [V1]", [new("Daft Punk")],
                        duration: TimeSpan.FromSeconds(156), alternateTitles: ["Get Lucky"])]));
            });
        var registry = MetadataRegistry(capability.Object);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(item => item.PlanAsync<IProviderMetadataCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => MetadataPlan(request, registry, capability.Object));
        router.Setup(item => item.PlanAsync<IProviderStreamingCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => Plan(request, registry,
                registry.GetRequiredCapability<IProviderStreamingCapability>("deezer", ProviderCapabilityKind.Streaming)));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Providers:MetadataOrder"] = "deezer",
            ["Providers:StreamingOrder"] = "deezer"
        }).Build();
        ProtocolProviderGateway NewGateway() => new(router.Object, registry, new HttpClientFactory(), configuration,
            applicationCache: cache);
        var context = SearchRankingContext(ProtocolKind.Jellyfin);

        var cold = Assert.Single((await NewGateway().SearchAsync(context, "Get Lucky Daft Punk", 10, 0, 0)).Songs);
        Assert.Equal(new[] { "Get Lucky" }, cold.AlternateTitles);
        var key = Assert.Single(cache.GetKeysByPattern("search:v3:*"));
        var oldKey = key.Replace("search:v3:", "search:v2:", StringComparison.Ordinal);
        var snapshot = JsonNode.Parse((await cache.GetStringAsync(key))!)!;
        Assert.True(snapshot["Result"]!["Songs"]![0]!.AsObject().Remove("AlternateTitles"));
        await cache.SetStringAsync(oldKey, snapshot.ToJsonString(), TimeSpan.FromMinutes(5));
        await cache.DeleteAsync(key);

        var upgraded = Assert.Single((await NewGateway().SearchAsync(context, "Get Lucky Daft Punk", 10, 0, 0)).Songs);
        Assert.Equal(2, calls);
        failProvider = true;
        var warm = Assert.Single((await NewGateway().SearchAsync(context, "Get Lucky Daft Punk", 10, 0, 0)).Songs);

        Assert.Equal(2, calls);
        Assert.Equal(cold.Id, upgraded.Id);
        Assert.Equal(cold.Id, warm.Id);
        Assert.Equal(cold.Title, warm.Title);
        Assert.Equal(cold.PreciseDuration, warm.PreciseDuration);
        Assert.Equal(cold.AlternateTitles, warm.AlternateTitles);
        Assert.Equal(SearchRelevanceRanker.ScoreSong("Get Lucky Daft Punk", "Get Lucky", "Daft Punk", durationSeconds: 156),
            SearchRelevanceRanker.ScoreSong("Get Lucky Daft Punk", warm));
        Assert.True(await cache.ExistsAsync(oldKey));
        Assert.Equal(ApplicationCacheCategory.SearchResults, ApplicationCachePolicyRegistry.Classify(oldKey));
        Assert.Equal(ApplicationCachePolicyRegistry.Resolve(key).FreshFor, ApplicationCachePolicyRegistry.Resolve(oldKey).FreshFor);
        capability.Verify(item => item.GetTrackAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderTrackLookupRequest>()), Times.Never);
    }

    [Fact]
    public async Task DetailMetadataCache_SeparatesAccountsAndRevisions()
    {
        var metadata = SearchRankingCapability("deezer");
        Mock.Get(metadata).Setup(item => item.GetTrackAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderTrackLookupRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderTrackMetadata>.Success(new(new("deezer", ProviderResourceKind.Track, "song"),
                "Song", [new("Artist", new("deezer", ProviderResourceKind.Artist, "artist"))],
                albumId: new("deezer", ProviderResourceKind.Album, "album"), albumTitle: "Album")));
        var registry = MetadataRegistry(metadata);
        var accountId = Guid.NewGuid();
        long revision = 1;
        var router = new Mock<IProviderRouter>();
        router.Setup(item => item.PlanAsync<IProviderMetadataCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) =>
            {
                var plan = MetadataPlan(request, registry, metadata);
                var candidate = plan.Candidates[0];
                return plan with
                {
                    Candidates = [candidate with { Context = new(request.Actor, "deezer",
                    new(accountId, "deezer", ProviderAccountScope.Shared, revision), request.Policy,
                    request.OperationId, request.CorrelationId, request.Deadline, request.CancellationToken) }]
                };
            });
        using var cache = new MemoryApplicationCache(new allstarr.Core.Operations.SystemPlatformClock());
        var gateway = new ProtocolProviderGateway(router.Object, registry, new HttpClientFactory(), applicationCache: cache, audioVerifier: FixtureAudioVerifier.Instance);
        var context = SearchRankingContext(ProtocolKind.Jellyfin);
        for (var scope = 0; scope < 3; scope++)
        {
            Assert.NotNull(await gateway.GetSongAsync(context, "deezer", "song"));
            Assert.NotNull(await gateway.GetSongAsync(context, "deezer", "song"));
            if (scope == 0) revision++;
            else accountId = Guid.NewGuid();
        }
        Mock.Get(metadata).Verify(item => item.GetTrackAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderTrackLookupRequest>()), Times.Exactly(3));
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin, false)]
    [InlineData(ProtocolKind.Subsonic, false)]
    [InlineData(ProtocolKind.Jellyfin, true)]
    [InlineData(ProtocolKind.Subsonic, true)]
    public async Task Search_KnownRecordingUsesReturnedDisplaySourcesWithoutExtraLookups(ProtocolKind protocol, bool manual)
    {
        const string publicId = "ext-soundcloud-song-first";
        var capability = SearchRankingCapability("soundcloud", ("first", "Song", "Artist"));
        var albumSource = SearchRankingCapability("deezer", ("second", "Song", "Artist"));
        Mock.Get(albumSource).Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("deezer",
                [new(new("deezer", ProviderResourceKind.Track, "second"), "Song", [new("Artist")],
                    albumTitle: "Real album", albumId: new("deezer", ProviderResourceKind.Album, "album"), year: 2024)])));
        var registry = MetadataRegistry(capability, albumSource);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(item => item.PlanAsync<IProviderMetadataCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => MetadataPlan(request, registry, capability, albumSource));
        router.Setup(item => item.PlanAsync<IProviderStreamingCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => Plan(request, registry,
                registry.GetRequiredCapability<IProviderStreamingCapability>("soundcloud", ProviderCapabilityKind.Streaming),
                registry.GetRequiredCapability<IProviderStreamingCapability>("deezer", ProviderCapabilityKind.Streaming)));
        var id = Guid.CreateVersion7();
        var identities = new Mock<ITrackIdentityService>();
        identities.Setup(item => item.ResolveManyAsync(It.IsAny<IReadOnlyList<TrackIdentityLookup>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TrackIdentityLookup> lookups, CancellationToken _) => lookups.Select(lookup =>
                (TrackIdentityResolution?)new TrackIdentityResolution(id, Guid.CreateVersion7(), lookup.ExternalId,
                    ProviderIdentityScope.Catalog, null, ProviderIdentityVerification.Verified, "odesli-link", 1)
                { PublicId = publicId }).ToArray());
        var gateway = new ProtocolProviderGateway(router.Object, registry, new HttpClientFactory(), identities: identities.Object, audioVerifier: FixtureAudioVerifier.Instance);

        var context = manual ? DisplayPolicyContext(protocol, "soundcloud", ProviderUseMode.ManualOnly) : SearchRankingContext(protocol);
        var result = await gateway.SearchAsync(context, "Song", 10, 0, 0);

        Assert.Equal(manual ? 2 : 1, result.Songs.Count);
        if (manual)
        {
            var chosen = Assert.Single(result.Songs, item => item.ExternalProvider == "soundcloud");
            Assert.Equal("ext-soundcloud-song-first", chosen.Id);
            Assert.Null(chosen.CanonicalRecordingId);
            Assert.Equal("Song", chosen.Title);
            Assert.Null(chosen.AlbumId);
            identities.Verify(item => item.ResolveManyAsync(It.Is<IReadOnlyList<TrackIdentityLookup>>(lookups =>
                lookups.Any(lookup => lookup.ExternalId.ProviderId == "soundcloud")), It.IsAny<CancellationToken>()), Times.Never);
        }
        var song = Assert.Single(result.Songs, item => item.ExternalProvider == "deezer");
        Assert.Equal(publicId, song.Id);
        Assert.Equal("deezer", song.ExternalProvider);
        Assert.Equal("Real album", song.Album);
        Assert.Equal("ext-deezer-album-album", song.AlbumId);
        Assert.Equal(2024, song.Year);
        identities.Verify(item => item.GetStoredSongsAsync(It.IsAny<ProviderActorContext>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
        Mock.Get(albumSource).Verify(item => item.GetTrackAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderTrackLookupRequest>()), Times.Never);
        Mock.Get(capability).Verify(item => item.GetTrackAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderTrackLookupRequest>()), Times.Never);
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin, "Daft Punk Get Lucky")]
    [InlineData(ProtocolKind.Subsonic, "Daft Punk Get Lucky")]
    [InlineData(ProtocolKind.Jellyfin, "Get Lucky")]
    [InlineData(ProtocolKind.Subsonic, "Get Lucky")]
    public async Task SearchRanksAllFetchedRowsBeforeApplyingTheLimit(ProtocolKind protocol, string query)
    {
        var upload = SearchRankingCapability("soundcloud",
            ("upload", "Daft Punk - Get Lucky #nightcore #bassboosted", "999333"));
        var originals = SearchRankingCapability("deezer",
            ("irrelevant", "Unrelated", "Someone Else"),
            ("original", "Get Lucky", "Daft Punk"));
        var (gateway, router) = SearchRankingGateway("soundcloud,deezer", "soundcloud,deezer", upload, originals);

        var result = await gateway.SearchAsync(SearchRankingContext(protocol), query, 1, 0, 0);

        Assert.Equal("original", Assert.Single(result.Songs).ExternalId);
        foreach (var capability in new[] { upload, originals })
        {
            Mock.Get(capability).Verify(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(),
                It.IsAny<ProviderMetadataSearchRequest>()), Times.Once);
            Mock.Get(capability).Verify(item => item.SearchAlbumsAsync(It.IsAny<ProviderExecutionContext>(),
                It.IsAny<ProviderMetadataSearchRequest>()), Times.Never);
            Mock.Get(capability).Verify(item => item.SearchArtistsAsync(It.IsAny<ProviderExecutionContext>(),
                It.IsAny<ProviderMetadataSearchRequest>()), Times.Never);
        }
        router.Verify(item => item.PlanAsync<IProviderMetadataCapability>(It.IsAny<ProviderRouteRequest>()), Times.Once);
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin, "deezer,soundcloud", "deezer")]
    [InlineData(ProtocolKind.Subsonic, "deezer,soundcloud", "deezer")]
    [InlineData(ProtocolKind.Jellyfin, "soundcloud,deezer", "soundcloud")]
    [InlineData(ProtocolKind.Subsonic, "soundcloud,deezer", "soundcloud")]
    public async Task SearchTiesUseMetadataOrderInsteadOfStreamingOrder(
        ProtocolKind protocol, string metadataOrder, string expectedProvider)
    {
        var first = SearchRankingCapability("soundcloud", ("s", "Get Lucky", "Daft Punk"));
        var second = SearchRankingCapability("deezer", ("d", "Get Lucky", "Daft Punk"));
        var streamingOrder = expectedProvider == "deezer" ? "soundcloud,deezer" : "deezer,soundcloud";
        var (gateway, _) = SearchRankingGateway(metadataOrder, streamingOrder, first, second);

        var result = await gateway.SearchAsync(SearchRankingContext(protocol), "Daft Punk Get Lucky", 2, 0, 0);

        Assert.Equal(expectedProvider, result.Songs[0].ExternalProvider);
        Assert.Equal(2, result.Songs.Count);
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin, "Espresso (Mixed)", null)]
    [InlineData(ProtocolKind.Subsonic, "Espresso [Mixed]", null)]
    [InlineData(ProtocolKind.Jellyfin, "Espresso", "Summer DJ Mix")]
    [InlineData(ProtocolKind.Subsonic, "Espresso", "Mixed by Someone")]
    [InlineData(ProtocolKind.Jellyfin, "Espresso", "Continuous Mix")]
    public async Task SearchRanksContinuousMixExcerptsAfterTheOriginalWithoutHidingThem(
        ProtocolKind protocol, string title, string? album)
    {
        var mixes = new Mock<IProviderMetadataCapability>(MockBehavior.Strict);
        mixes.SetupGet(item => item.ProviderId).Returns("qobuz");
        mixes.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Metadata);
        mixes.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("qobuz",
                [new(new("qobuz", ProviderResourceKind.Track, "mix"), title,
                    [new("Sabrina Carpenter")], albumTitle: album)])));
        var originals = SearchRankingCapability("deezer", ("original", "Espresso", "Sabrina Carpenter"));
        var (gateway, _) = SearchRankingGateway("qobuz,deezer", "qobuz,deezer", mixes.Object, originals);

        var result = await gateway.SearchAsync(SearchRankingContext(protocol), "Espresso", 2, 0, 0);

        Assert.Equal(new[] { "original", "mix" }, result.Songs.Select(song => song.ExternalId));
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin)]
    [InlineData(ProtocolKind.Subsonic)]
    public async Task SharedIsrcDoesNotCollapseAMixOverTheOriginal(ProtocolKind protocol)
    {
        var capabilities = new[] { "qobuz", "deezer" }.Select(provider =>
        {
            var capability = Mock.Get(SearchRankingCapability(provider));
            capability.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
                .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new(provider,
                    [new(new(provider, ProviderResourceKind.Track, provider), "Espresso", [new("Sabrina Carpenter")],
                        albumTitle: provider == "qobuz" ? "DJ Mix" : null, isrc: "USUM72403305")])));
            return capability.Object;
        }).ToArray();
        var (gateway, _) = SearchRankingGateway("qobuz,deezer", "qobuz,deezer", capabilities);

        var result = await gateway.SearchAsync(SearchRankingContext(protocol), "Espresso", 2, 0, 0);

        Assert.Equal(new[] { "deezer", "qobuz" }, result.Songs.Select(song => song.ExternalProvider));
    }

    private static IProviderMetadataCapability SearchRankingCapability(
        string provider, params (string Id, string Title, string Artist)[] tracks)
    {
        var capability = new Mock<IProviderMetadataCapability>(MockBehavior.Strict);
        capability.SetupGet(item => item.ProviderId).Returns(provider);
        capability.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Metadata);
        capability.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new(provider,
                tracks.Select(track => new ProviderTrackMetadata(new(provider, ProviderResourceKind.Track, track.Id),
                    track.Title, [new(track.Artist)])).ToArray())));
        return capability.Object;
    }

    private static ProtocolExecutionContext SearchRankingContext(ProtocolKind protocol) => new(
        protocol, "backend", "principal",
        new(Guid.NewGuid(), protocol.ToString().ToLowerInvariant(), "backend", "principal", "User", false),
        "search-ranking", DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);

    private static (ProtocolProviderGateway Gateway, Mock<IProviderRouter> Router) SearchRankingGateway(
        string metadataOrder, string streamingOrder, params IProviderMetadataCapability[] capabilities)
    {
        var registry = MetadataRegistry(capabilities);
        var router = new Mock<IProviderRouter>();
        router.Setup(item => item.PlanAsync<IProviderMetadataCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => MetadataPlan(request, registry, capabilities));
        router.Setup(item => item.PlanAsync<IProviderStreamingCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => Plan(request, registry,
                capabilities.Select(capability => registry.GetRequiredCapability<IProviderStreamingCapability>(
                    capability.ProviderId, ProviderCapabilityKind.Streaming)).ToArray()));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Providers:MetadataOrder"] = metadataOrder,
            ["Providers:StreamingOrder"] = streamingOrder
        }).Build();
        return (new ProtocolProviderGateway(router.Object, registry, new HttpClientFactory(), configuration, audioVerifier: FixtureAudioVerifier.Instance), router);
    }
}

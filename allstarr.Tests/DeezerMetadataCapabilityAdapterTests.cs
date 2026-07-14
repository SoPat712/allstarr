using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Deezer;
using allstarr.Core.Matching;
using allstarr.Core.Routing;
using allstarr.Models.Domain;
using allstarr.Services;
using Moq;

namespace allstarr.Tests;

public sealed class DeezerMetadataCapabilityAdapterTests
{
    [Fact]
    public async Task SearchTracks_MapsLegacyResultsToTypedProviderIds()
    {
        var legacy = new Mock<IConcreteMetadataService>(MockBehavior.Strict);
        legacy.Setup(item => item.SearchSongsAsync(
                "daft punk",
                2,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new Song
                {
                    Id = "ext-deezer-3135556",
                    ExternalId = "3135556",
                    ExternalProvider = "deezer",
                    Title = "Harder Better Faster Stronger",
                    Artist = "Daft Punk",
                    Artists = ["Daft Punk"],
                    ArtistIds = ["27"],
                    Album = "Discovery",
                    AlbumId = "302127",
                    Duration = 224,
                    Isrc = "GBDUW0000059",
                    CoverArtUrlLarge = "https://cdn.example.invalid/cover.jpg"
                }
            ]);
        var adapter = new DeezerMetadataCapabilityAdapter(legacy.Object);

        var outcome = await adapter.SearchTracksAsync(
            Context(),
            new ProviderMetadataSearchRequest(
                "daft punk",
                new ProviderPageRequest(limit: 2)));

        Assert.True(outcome.IsSuccess);
        var page = outcome.RequireValue();
        Assert.Equal("deezer", page.ProviderId);
        var track = Assert.Single(page.Items);
        Assert.Equal("3135556", track.Id.Value);
        Assert.Equal(ProviderResourceKind.Track, track.Id.ResourceKind);
        Assert.Equal("27", Assert.Single(track.Artists).ArtistId!.Value);
        Assert.Equal("302127", track.AlbumId!.Value);
        Assert.Equal(TimeSpan.FromSeconds(224), track.Duration);
        Assert.Equal("https://cdn.example.invalid/cover.jpg", track.Artwork!.PublicUri!.AbsoluteUri);
    }

    [Fact]
    public async Task CursorThatLegacyProviderCannotHonor_ReturnsTypedNotSupported()
    {
        var legacy = new Mock<IConcreteMetadataService>(MockBehavior.Strict);
        var adapter = new DeezerMetadataCapabilityAdapter(legacy.Object);

        var outcome = await adapter.SearchTracksAsync(
            Context(),
            new ProviderMetadataSearchRequest(
                "query",
                new ProviderPageRequest(cursor: "next-page")));

        Assert.False(outcome.IsSuccess);
        Assert.Equal(ProviderErrorKind.NotSupported, outcome.Error!.Kind);
        legacy.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MissingLookup_ReturnsTypedNotFound()
    {
        var legacy = new Mock<IConcreteMetadataService>(MockBehavior.Strict);
        legacy.Setup(item => item.GetSongAsync(
                "deezer",
                "missing",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Song?)null);
        var adapter = new DeezerMetadataCapabilityAdapter(legacy.Object);

        var outcome = await adapter.GetTrackAsync(
            Context(),
            new ProviderTrackLookupRequest(new ProviderExternalResourceId(
                "deezer",
                ProviderResourceKind.Track,
                "missing")));

        Assert.False(outcome.IsSuccess);
        Assert.Equal(ProviderErrorKind.NotFound, outcome.Error!.Kind);
    }

    [Fact]
    public async Task ProviderException_CannotEscapeThroughOutcomeText()
    {
        var legacy = new Mock<IConcreteMetadataService>(MockBehavior.Strict);
        legacy.Setup(item => item.FindSongByIsrcAsync(
                "GBDUW0000059",
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(
                "Authorization: Bearer secret-token https://signed.example.invalid/media"));
        var adapter = new DeezerMetadataCapabilityAdapter(legacy.Object);

        var outcome = await adapter.LookupByIsrcAsync(
            Context(),
            new ProviderIsrcLookupRequest("GBDUW0000059"));

        Assert.False(outcome.IsSuccess);
        Assert.Equal(ProviderErrorKind.TransientFailure, outcome.Error!.Kind);
        Assert.DoesNotContain("secret-token", outcome.Error.SafeMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("signed.example", outcome.Error.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void BuiltInRegistration_IsAtomicAndResolvesTypedImplementation()
    {
        var adapter = new DeezerMetadataCapabilityAdapter(
            new Mock<IConcreteMetadataService>(MockBehavior.Strict).Object);
        var registry = new ProviderRegistry(
            [DeezerMetadataCapabilityAdapter.CreateRegistration(adapter)]);

        var descriptor = registry.GetRequired("deezer");
        var resolved = registry.GetRequiredCapability<IProviderMetadataCapability>(
            "deezer",
            ProviderCapabilityKind.Metadata);

        Assert.Equal(ProviderOrigin.BuiltIn, descriptor.Origin);
        Assert.Same(adapter, resolved);
        Assert.Single(registry.FindByCapability(ProviderCapabilityKind.Metadata));
    }

    [Fact]
    public async Task ProviderRouter_RoutesAndExecutesTheRealBuiltInAdapter()
    {
        var legacy = new Mock<IConcreteMetadataService>(MockBehavior.Strict);
        legacy.Setup(item => item.SearchSongsAsync(
                "route me",
                1,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new Song
                {
                    Id = "ext-deezer-route-result",
                    ExternalId = "route-result",
                    ExternalProvider = "deezer",
                    Title = "Routed result",
                    Artist = "Artist",
                    Artists = ["Artist"],
                    ArtistIds = ["artist-result"],
                    Album = "Album",
                    AlbumId = "album-result",
                    Duration = 180,
                    Isrc = "USRC17607839"
                }
            ]);
        var adapter = new DeezerMetadataCapabilityAdapter(legacy.Object);
        var registry = new ProviderRegistry(
            [DeezerMetadataCapabilityAdapter.CreateRegistration(adapter)]);
        var router = new ProviderRouter(
            registry,
            new Mock<IProviderRouteAccountResolver>(MockBehavior.Strict).Object,
            new Mock<IProviderRouteHealthSource>(MockBehavior.Strict).Object,
            new Mock<IProviderRouteSidecarSource>(MockBehavior.Strict).Object,
            new Mock<ITrackIdentityService>(MockBehavior.Strict).Object);
        var context = Context();
        var route = new ProviderRouteRequest(
            ProviderCapabilityKind.Metadata,
            context.Actor,
            context.Policy,
            "route-deezer-metadata",
            "route-deezer-fixture",
            DateTimeOffset.UtcNow.AddMinutes(1),
            providerPriority: ["deezer"]);

        var plan = await router.PlanAsync<IProviderMetadataCapability>(route);
        var candidate = Assert.Single(plan.Candidates);
        var outcome = await candidate.Implementation.SearchTracksAsync(
            candidate.Context,
            new ProviderMetadataSearchRequest("route me", new ProviderPageRequest(1)));

        Assert.Same(adapter, candidate.Implementation);
        Assert.Equal("deezer", plan.Decision.SelectedProviderId);
        Assert.Equal("route-result", Assert.Single(outcome.RequireValue().Items).Id.Value);
    }

    private static ProviderExecutionContext Context()
    {
        var actor = new ProviderActorContext(
            Guid.CreateVersion7(),
            ProviderActorKind.User,
            Guid.CreateVersion7(),
            new ProviderBackendPrincipal("jellyfin", "fixture", "fixture-user"));
        return new ProviderExecutionContext(
            actor,
            "deezer",
            account: null,
            library: null,
            new ProviderExecutionPolicy(
                new ProviderQualityPolicy(
                    ProviderAudioQuality.Any,
                    ProviderAudioQuality.HighResolution,
                    allowTranscode: true),
                ProviderExplicitContentPolicy.Allow,
                allowFallback: true,
                allowSharedAccount: false,
                allowManagedDownloads: false,
                allowedProviderIds: ["deezer"]),
            operationId: "metadata-search",
            correlationId: "metadata-search-fixture",
            deadline: DateTimeOffset.UtcNow.AddMinutes(1),
            cancellationToken: CancellationToken.None);
    }
}

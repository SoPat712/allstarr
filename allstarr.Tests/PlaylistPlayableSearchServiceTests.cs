using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Models.Settings;
using allstarr.Services.Spotify;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed class PlaylistPlayableSearchServiceTests
{
    [Fact]
    public async Task Automatic_search_scores_all_playable_providers_and_selects_the_best()
    {
        var tenant = Guid.CreateVersion7();
        var user = Guid.CreateVersion7();
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming))
            .Returns(["apple-download", "deezer"]);
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Download))
            .Returns(["apple-download", "deezer"]);
        gateway.Setup(item => item.SearchPlayableSongsAsync(
                It.IsAny<ProtocolExecutionContext>(), "Feels Calvin Harris", 60))
            .ReturnsAsync(
                [
                    new Song
                    {
                        ExternalProvider = "deezer",
                        ExternalId = "alternate",
                        Title = "Feels",
                        Artist = "Calvin Harris",
                        Album = "Funk Wav Bounces Vol. 1",
                        Duration = 223
                    },
                    new Song
                    {
                        ExternalProvider = "apple-download",
                        ExternalId = "best",
                        Title = "Feels",
                        Artist = "Calvin Harris",
                        Album = "Funk Wav Bounces Vol. 1",
                        Duration = 223
                    }
                ]);
        var service = new PlaylistPlayableSearchService(
            gateway.Object,
            new TrackMatchDecisionEngine(),
            null!,
            new IdentityOptions(),
            Options.Create(new JellyfinSettings()),
            NullLogger<PlaylistPlayableSearchService>.Instance);
        var context = Context(tenant, user);
        var scope = new TrackMatchScope(
            tenant, user, "main", "music", Guid.CreateVersion7(), 2, 1);

        var result = await service.MatchAsync(
            context,
            new ExternalTrackMatchSnapshot(
                "source", "spotify", "source-track", "Feels",
                "Calvin Harris", "Funk Wav Bounces Vol. 1", null, 223_000, null, null, null),
            scope,
            [],
            null,
            CancellationToken.None);

        Assert.Equal(TrackMatchReviewState.Accepted, result.Decision.State);
        Assert.Equal("best", result.SelectedExternal!.ExternalId);
        Assert.Equal(2, result.RoutableExternalCandidates.Count);
    }

    [Fact]
    public async Task Cached_routes_try_the_next_provider_before_searching_again()
    {
        var tenant = Guid.CreateVersion7();
        var user = Guid.CreateVersion7();
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming))
            .Returns(["apple-download", "deezer"]);
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Download))
            .Returns(["apple-download", "deezer"]);
        gateway.Setup(item => item.GetSongAsync(
                It.IsAny<ProtocolExecutionContext>(), "applemusic", "cached-track"))
            .ReturnsAsync((Song?)null);
        gateway.Setup(item => item.GetSongAsync(
                It.IsAny<ProtocolExecutionContext>(), "deezer", "fallback-track"))
            .ReturnsAsync(new Song
            {
                ExternalProvider = "deezer",
                ExternalId = "fallback-track",
                Title = "Hit 'Em Up",
                Artist = "2Pac, Outlawz",
                Album = "Greatest Hits",
                Duration = 313
            });
        var service = new PlaylistPlayableSearchService(
            gateway.Object,
            new TrackMatchDecisionEngine(),
            null!,
            new IdentityOptions(),
            Options.Create(new JellyfinSettings()),
            NullLogger<PlaylistPlayableSearchService>.Instance);
        var context = Context(tenant, user);
        var scope = new TrackMatchScope(
            tenant, user, "main", "music", Guid.CreateVersion7(), 2, 1);
        var canonical = Guid.CreateVersion7();

        var result = await service.ReuseAsync(
            context,
            new ExternalTrackMatchSnapshot(
                "source", "spotify", "source-track", "Hit 'Em Up - Single Version",
                "2Pac, Outlawz", "Greatest Hits", null, 313_000, null, null, null),
            scope,
            [
                new ProviderTrackIdentityRecord
                {
                    TenantId = tenant,
                    CanonicalRecordingId = canonical,
                    ProviderId = "applemusic",
                    ResourceKind = ProviderResourceKind.Track,
                    ExternalId = "cached-track",
                    Verification = ProviderIdentityVerification.Verified
                },
                new ProviderTrackIdentityRecord
                {
                    TenantId = tenant,
                    CanonicalRecordingId = canonical,
                    ProviderId = "deezer",
                    ResourceKind = ProviderResourceKind.Track,
                    ExternalId = "fallback-track",
                    Verification = ProviderIdentityVerification.Verified
                }
            ],
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(TrackMatchReviewState.Accepted, result.Decision.State);
        Assert.Equal("fallback-track", result.SelectedExternal!.ExternalId);
        gateway.Verify(item => item.GetSongAsync(
            It.IsAny<ProtocolExecutionContext>(), "applemusic", "cached-track"), Times.Once);
        gateway.Verify(item => item.GetSongAsync(
            It.IsAny<ProtocolExecutionContext>(), "deezer", "fallback-track"), Times.Once);
        gateway.Verify(item => item.SearchPlayableSongsAsync(
            It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task Unique_suggestion_is_used_but_keeps_review_warning()
    {
        var tenant = Guid.CreateVersion7();
        var user = Guid.CreateVersion7();
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming))
            .Returns(["apple-download"]);
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Download))
            .Returns(["apple-download"]);
        gateway.Setup(item => item.SearchPlayableSongsAsync(
                It.IsAny<ProtocolExecutionContext>(), "Feels Calvin Harris", 60))
            .ReturnsAsync(
                [
                    new Song
                    {
                        ExternalProvider = "apple-download",
                        ExternalId = "tentative",
                        Title = "Feels (Live)",
                        Artist = "Calvin Harris",
                        Album = "Funk Wav Bounces Vol. 1",
                        Duration = 223
                    }
                ]);
        var service = new PlaylistPlayableSearchService(
            gateway.Object,
            new TrackMatchDecisionEngine(),
            null!,
            new IdentityOptions(),
            Options.Create(new JellyfinSettings()),
            NullLogger<PlaylistPlayableSearchService>.Instance);

        var result = await service.MatchAsync(
            Context(tenant, user),
            new ExternalTrackMatchSnapshot(
                "source", "spotify", "source-track", "Feels",
                "Calvin Harris", "Funk Wav Bounces Vol. 1", null, 223_000, null, null, null),
            new TrackMatchScope(
                tenant, user, "main", "music", Guid.CreateVersion7(), 2, 1),
            [],
            null,
            CancellationToken.None);

        Assert.Equal(TrackMatchReviewState.Suggested, result.Decision.State);
        Assert.InRange(
            result.Decision.Confidence,
            result.Decision.SuggestThreshold,
            result.Decision.AcceptThreshold - 0.0001);
        Assert.True(result.Decision.RequiresReview);
        Assert.Contains("below_accept_threshold_review", result.Decision.Warnings);
        Assert.Equal("tentative", result.SelectedExternal!.ExternalId);
    }

    [Fact]
    public async Task EquivalentProviderEditionsAreOneCandidateWithFallbackRoutes()
    {
        var tenant = Guid.CreateVersion7();
        var user = Guid.CreateVersion7();
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming))
            .Returns(["deezer"]);
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Download))
            .Returns(["apple-download"]);
        gateway.Setup(item => item.SearchPlayableSongsAsync(
                It.IsAny<ProtocolExecutionContext>(),
                "Serenade No. 13 in G Major Wiener Philharmoniker",
                60))
            .ReturnsAsync(
            [
                new Song
                {
                    ExternalProvider = "apple-download",
                    ExternalId = "apple-serenade",
                    Title = "Serenade No. 13 in G Major",
                    Artist = "Wiener Philharmoniker",
                    Album = "Mozart: Eine kleine Nachtmusik",
                    Duration = 400
                },
                new Song
                {
                    ExternalProvider = "deezer",
                    ExternalId = "deezer-serenade",
                    Title = "Serenade No. 13 in G Major: Serenade No. 13 in G Major",
                    Artist = "Wiener Philharmoniker",
                    Album = "Mozart: Eine kleine Nachtmusik",
                    Duration = 401
                }
            ]);
        var service = new PlaylistPlayableSearchService(
            gateway.Object,
            new TrackMatchDecisionEngine(),
            null!,
            new IdentityOptions(),
            Options.Create(new JellyfinSettings()),
            NullLogger<PlaylistPlayableSearchService>.Instance);

        var result = await service.MatchAsync(
            Context(tenant, user),
            new ExternalTrackMatchSnapshot(
                "source",
                "spotify",
                "source-track",
                "Serenade No. 13 in G Major",
                "Wiener Philharmoniker",
                "Mozart: Eine kleine Nachtmusik",
                null,
                400_000,
                null,
                null,
                null),
            new TrackMatchScope(
                tenant, user, "main", "music", Guid.CreateVersion7(), 2, 1),
            [],
            null,
            CancellationToken.None);

        Assert.Equal(TrackMatchReviewState.Accepted, result.Decision.State);
        Assert.Single(result.Decision.Candidates);
        Assert.Equal(2, result.RoutableExternalCandidates.Count);
    }

    private static ProtocolExecutionContext Context(Guid tenant, Guid user) => new(
        ProtocolKind.Jellyfin,
        "main",
        "principal",
        new AllstarrPrincipal(
            tenant, user, "jellyfin", "main", "principal", "Owner", false),
        "playable-search-test",
        DateTimeOffset.UtcNow.AddMinutes(1),
        CancellationToken.None,
        libraryScopeId: "music");
}

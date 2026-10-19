using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Core.Settings;
using System.Collections.Immutable;
using allstarr.Models.Domain;
using allstarr.Models.Settings;
using allstarr.Services.Spotify;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed class TrackMatchPlayableSearchTests
{
    [Fact]
    public void Provider_eligibility_comes_from_streaming_routes()
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming))
            .Returns(["jellyfin", "stream-extension"]);
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Download))
            .Returns(["download-extension"]);
        var service = new TrackMatchPlayableSearch(
            gateway.Object,
            new TrackMatchDecisionEngine(),
            NullLogger<TrackMatchPlayableSearch>.Instance);

        Assert.True(service.CanUseProvider("stream-extension"));
        Assert.False(service.CanUseProvider("download_extension"));
        Assert.False(service.CanUseProvider("musicbrainz"));
        Assert.False(service.CanUseProvider("metadata-extension"));
    }

    [Fact]
    public async Task Provider_eligibility_uses_the_household_snapshot()
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming))
            .Returns(["qobuz", "deezer"]);
        var policies = new Mock<IEffectiveProviderPolicyResolver>();
        policies.Setup(item => item.ResolveAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EffectiveProviderPolicySnapshot(
                new Dictionary<ProviderCapabilityKind, ImmutableArray<string>>
                {
                    [ProviderCapabilityKind.Streaming] = ["qobuz", "deezer"]
                }.ToImmutableDictionary(),
                new[] { "qobuz" }.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase),
                AudioQualityPolicy.DefaultStep,
                0.07));
        var service = new TrackMatchPlayableSearch(
            gateway.Object,
            new TrackMatchDecisionEngine(),
            NullLogger<TrackMatchPlayableSearch>.Instance,
            policies.Object);

        Assert.False(await service.CanUseProviderAsync("qobuz"));
        Assert.True(await service.CanUseProviderAsync("deezer"));
    }

    [Fact]
    public async Task Automatic_search_scores_all_playable_providers_and_selects_the_best()
    {
        var user = Guid.CreateVersion7();
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming))
            .Returns(["apple-download", "deezer"]);
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Download))
            .Returns(["apple-download", "deezer"]);
        gateway.Setup(item => item.SearchPlayableSongsAsync(
                It.IsAny<ProtocolExecutionContext>(), "Feels Calvin Harris", 15))
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
        var service = new TrackMatchPlayableSearch(
            gateway.Object,
            new TrackMatchDecisionEngine(),
            NullLogger<TrackMatchPlayableSearch>.Instance);
        var context = Context(user);
        var scope = new TrackMatchScope(
            user, "main", Guid.CreateVersion7(), 2, 1,
            new HashSet<string>(StringComparer.Ordinal));

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
    public async Task AutomaticSearchUsesConfiguredProviderPriorityWindows()
    {
        var user = Guid.CreateVersion7();
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming))
            .Returns(["apple-download", "deezer", "qobuz", "fourth"]);
        gateway.Setup(item => item.SearchPlayableSongsAsync(
                It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 15))
            .ReturnsAsync(
            [
                new Song
                {
                    ExternalProvider = "apple-download",
                    ExternalId = "preferred",
                    Title = "Window Song",
                    Artist = "The Artist",
                    Duration = 244
                },
                new Song
                {
                    ExternalProvider = "deezer",
                    ExternalId = "raw-best",
                    Title = "Window Song",
                    Artist = "The Artist",
                    Duration = 240
                }
            ]);
        var service = new TrackMatchPlayableSearch(
            gateway.Object,
            new TrackMatchDecisionEngine(),
            NullLogger<TrackMatchPlayableSearch>.Instance);
        var result = await service.MatchAsync(
            Context(user),
            new ExternalTrackMatchSnapshot(
                "source", "spotify", "source-track", "Window Song",
                "The Artist", null, null, 240_000, null, null, null),
            new TrackMatchScope(
                user, "main", Guid.CreateVersion7(), 2, 1,
                new HashSet<string>(StringComparer.Ordinal) { "music" }),
            [],
            null,
            CancellationToken.None);

        var selected = Assert.Single(result.RoutableExternalCandidates);
        Assert.Equal("preferred", selected.ExternalId);
        Assert.Equal("preferred", result.SelectedExternal!.ExternalId);
        Assert.Contains("accepted_route_priority_selected", result.Decision.Reasons);
        Assert.True(result.Decision.Candidates[0].Confidence < result.Decision.Candidates[1].Confidence);
    }

    [Fact]
    public async Task AppleCatalogResultsMatchTheRegisteredAppleProviderOrder()
    {
        var user = Guid.CreateVersion7();
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming))
            .Returns(["apple-musickit", "deezer"]);
        gateway.Setup(item => item.SearchPlayableSongsAsync(
                It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 15))
            .ReturnsAsync(
            [
                new Song
                {
                    ExternalProvider = "apple-download",
                    ExternalId = "apple-track",
                    Title = "Window Song",
                    Artist = "The Artist",
                    Duration = 240
                },
                new Song
                {
                    ExternalProvider = "deezer",
                    ExternalId = "deezer-track",
                    Title = "Window Song",
                    Artist = "The Artist",
                    Duration = 240
                }
            ]);
        var service = new TrackMatchPlayableSearch(
            gateway.Object,
            new TrackMatchDecisionEngine(),
            NullLogger<TrackMatchPlayableSearch>.Instance);

        var result = await service.MatchAsync(
            Context(user),
            new ExternalTrackMatchSnapshot(
                "source", "spotify", "source-track", "Window Song",
                "The Artist", null, null, 240_000, null, null, null),
            new TrackMatchScope(
                user, "main", Guid.CreateVersion7(), 2, 1,
                new HashSet<string>(StringComparer.Ordinal) { "music" }),
            [],
            null,
            CancellationToken.None);

        Assert.Equal("apple-track", result.SelectedExternal!.ExternalId);
        Assert.Contains(result.RoutableExternalCandidates, item => item.ExternalId == "apple-track");
        Assert.True(service.CanUseProvider("apple-download"));
    }

    [Fact]
    public async Task Concise_search_query_keeps_full_title_scoring()
    {
        var user = Guid.CreateVersion7();
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming))
            .Returns(["jellyfin"]);
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Download))
            .Returns([]);
        gateway.Setup(item => item.SearchPlayableSongsAsync(
                It.IsAny<ProtocolExecutionContext>(), "Link Up Metro Boomin", 15))
            .ReturnsAsync(
            [
                new Song
                {
                    ExternalProvider = "jellyfin",
                    ExternalId = "base-edition",
                    Title = "Link Up",
                    Artist = "Metro Boomin, Don Toliver, Wizkid, BEAM, Toian",
                    Album = "Spider-Man: Across the Spider-Verse",
                    Duration = 195
                }
            ]);
        var service = new TrackMatchPlayableSearch(
            gateway.Object,
            new TrackMatchDecisionEngine(),
            NullLogger<TrackMatchPlayableSearch>.Instance);

        var result = await service.MatchAsync(
            Context(user),
            new ExternalTrackMatchSnapshot(
                "source",
                "spotify",
                "source-track",
                "Link Up (Spider-Verse Remix)",
                "Metro Boomin, Don Toliver, Wizkid, BEAM, Toian",
                "Spider-Man: Across the Spider-Verse",
                null,
                195_000,
                null,
                null,
                null),
            new TrackMatchScope(
                user, "main", Guid.CreateVersion7(), 2, 1,
                new HashSet<string>(StringComparer.Ordinal) { "music" }),
            [],
            null,
            CancellationToken.None);

        Assert.Equal(TrackMatchReviewState.Unresolved, result.Decision.State);
        Assert.Null(result.SelectedExternal);
        Assert.Empty(result.RoutableExternalCandidates);
        Assert.Contains(
            "semantic_version_mismatch",
            Assert.Single(result.Decision.Candidates).Warnings);
        gateway.Verify(item => item.SearchPlayableSongsAsync(
            It.IsAny<ProtocolExecutionContext>(), "Link Up Metro Boomin", 15), Times.Once);
    }

    [Fact]
    public async Task Cached_routes_try_the_next_provider_before_searching_again()
    {
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
        var service = new TrackMatchPlayableSearch(
            gateway.Object,
            new TrackMatchDecisionEngine(),
            NullLogger<TrackMatchPlayableSearch>.Instance);
        var context = Context(user);
        var scope = new TrackMatchScope(
            user, "main", Guid.CreateVersion7(), 2, 1,
            new HashSet<string>(StringComparer.Ordinal) { "music" });
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
                    CanonicalRecordingId = canonical,
                    ProviderId = "applemusic",
                    ResourceKind = ProviderResourceKind.Track,
                    ExternalId = "cached-track",
                    Verification = ProviderIdentityVerification.Verified
                },
                new ProviderTrackIdentityRecord
                {
                    CanonicalRecordingId = canonical,
                    ProviderId = "deezer",
                    ResourceKind = ProviderResourceKind.Track,
                    ExternalId = "fallback-track",
                    Verification = ProviderIdentityVerification.Verified
                }
            ],
            [],
            null,
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
    public async Task CachedLowerPriorityRouteIsNotReusedBeforeAnUntriedHigherProvider()
    {
        var user = Guid.CreateVersion7();
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming))
            .Returns(["apple-musickit", "deezer"]);
        gateway.Setup(item => item.GetSongAsync(
                It.IsAny<ProtocolExecutionContext>(), "deezer", "cached-track"))
            .ReturnsAsync(new Song
            {
                ExternalProvider = "deezer",
                ExternalId = "cached-track",
                Title = "Window Song",
                Artist = "The Artist",
                Duration = 240
            });
        var service = new TrackMatchPlayableSearch(
            gateway.Object,
            new TrackMatchDecisionEngine(),
            NullLogger<TrackMatchPlayableSearch>.Instance);

        var result = await service.ReuseAsync(
            Context(user),
            new ExternalTrackMatchSnapshot(
                "source", "spotify", "source-track", "Window Song",
                "The Artist", null, null, 240_000, null, null, null),
            new TrackMatchScope(
                user, "main", Guid.CreateVersion7(), 2, 1,
                new HashSet<string>(StringComparer.Ordinal) { "music" }),
            [
                new ProviderTrackIdentityRecord
                {
                    CanonicalRecordingId = Guid.CreateVersion7(),
                    ProviderId = "deezer",
                    ResourceKind = ProviderResourceKind.Track,
                    ExternalId = "cached-track",
                    Verification = ProviderIdentityVerification.Verified
                }
            ],
            [],
            null,
            CancellationToken.None);

        Assert.Null(result);
        gateway.Verify(item => item.GetSongAsync(
            It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task CachedRouteUsesCurrentScoringAndLocalPriority()
    {
        var user = Guid.CreateVersion7();
        var scope = new TrackMatchScope(user, "main", Guid.CreateVersion7(), 2, 1,
            new HashSet<string>(StringComparer.Ordinal) { "music" });
        var source = new ExternalTrackMatchSnapshot("source", "spotify", "source-track", "Sunroof",
            "Nicky Youre, hey daisy", "Sunroof", null, 163_025, null, null, null);
        var local = new LocalTrackMatchCandidate(Guid.CreateVersion7(), user, "main", "music",
            "local-track", null, "Sunroof", "Nicky Youre, dazy", "Sunroof", null, 163_025, null, null, null);
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming)).Returns(["apple-download"]);
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "apple-download", "cached"))
            .ReturnsAsync(new Song
            {
                ExternalProvider = "apple-download",
                ExternalId = "cached",
                Title = "Sunroof",
                Artist = "Nicky Youre & hey daisy",
                Album = "Sunroof (Remixes) - EP",
                Duration = 163
            });
        var service = new TrackMatchPlayableSearch(gateway.Object, new TrackMatchDecisionEngine(), NullLogger<TrackMatchPlayableSearch>.Instance);
        var routes = new[] { new ProviderTrackIdentityRecord
        {
            CanonicalRecordingId = Guid.CreateVersion7(), ProviderId = "apple-download",
            ExternalId = "cached", ResourceKind = ProviderResourceKind.Track, Verification = ProviderIdentityVerification.Verified
        } };

        var result = await service.ReuseAsync(Context(user), source, scope, routes, [local], null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(TrackMatchReviewState.Accepted, result.Decision.State);
        Assert.Equal(local.LibraryTrackId, result.Decision.SelectedLibraryTrackId);
        Assert.Null(result.SelectedExternal);
        Assert.Equal(2, result.Decision.Candidates.Count);
        Assert.All(result.Decision.Candidates, candidate => Assert.True(candidate.Confidence < 1));

        var rejection = new ScopedTrackMatchOverride(user, "spotify", "source-track", null,
            new HashSet<Guid> { local.LibraryTrackId });
        var rejectedLocal = await service.ReuseAsync(Context(user), source, scope, routes, [local], rejection, CancellationToken.None);
        Assert.Equal("cached", rejectedLocal!.SelectedExternal!.ExternalId);

        var changedRecording = await service.ReuseAsync(Context(user), source with { Title = "Sunroof (Live)" },
            scope, routes, [], null, CancellationToken.None);
        Assert.Null(changedRecording);
    }

    [Fact]
    public async Task Tentative_routes_are_freshly_searched_instead_of_reused()
    {
        var user = Guid.CreateVersion7();
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming))
            .Returns(["deezer"]);
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Download))
            .Returns([]);
        var service = new TrackMatchPlayableSearch(
            gateway.Object,
            new TrackMatchDecisionEngine(),
            NullLogger<TrackMatchPlayableSearch>.Instance);

        var result = await service.ReuseAsync(
            Context(user),
            new ExternalTrackMatchSnapshot(
                "source", "spotify", "source-track", "Winter Wind",
                "Frédéric Chopin", null, null, 225_000, null, null, null),
            new TrackMatchScope(
                user, "main", Guid.CreateVersion7(), 2, 1,
                new HashSet<string>(StringComparer.Ordinal) { "music" }),
            [
                new ProviderTrackIdentityRecord
                {
                    CanonicalRecordingId = Guid.CreateVersion7(),
                    ProviderId = "deezer",
                    ResourceKind = ProviderResourceKind.Track,
                    ExternalId = "stale-suggestion",
                    Verification = ProviderIdentityVerification.Verified,
                    VerificationMethod = "automatic-suggestion"
                }
            ],
            [],
            null,
            CancellationToken.None);

        Assert.Null(result);
        gateway.Verify(item => item.GetSongAsync(
            It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UniqueSpellingSuggestionIsUsableButADifferentVersionNeedsAManualChoice(bool differentVersion)
    {
        var user = Guid.CreateVersion7();
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming))
            .Returns(["apple-download"]);
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Download))
            .Returns(["apple-download"]);
        gateway.Setup(item => item.SearchPlayableSongsAsync(
                It.IsAny<ProtocolExecutionContext>(), "Feels Calvin Harris", 15))
            .ReturnsAsync(
                [
                    new Song
                    {
                        ExternalProvider = "apple-download",
                        ExternalId = "tentative",
                        Title = differentVersion ? "Feels (Live)" : "Fiels",
                        Artist = "Calvin Harris",
                        Album = "Funk Wav Bounces Vol. 1",
                        Duration = 223
                    }
                ]);
        var service = new TrackMatchPlayableSearch(
            gateway.Object,
            new TrackMatchDecisionEngine(),
            NullLogger<TrackMatchPlayableSearch>.Instance);

        var result = await service.MatchAsync(
            Context(user),
            new ExternalTrackMatchSnapshot(
                "source", "spotify", "source-track", "Feels",
                "Calvin Harris", "Funk Wav Bounces Vol. 1", null, 223_000, null, null, null),
            new TrackMatchScope(
                user, "main", Guid.CreateVersion7(), 2, 1,
                new HashSet<string>(StringComparer.Ordinal) { "music" }),
            [],
            null,
            CancellationToken.None);

        Assert.Equal(differentVersion ? TrackMatchReviewState.Unresolved : TrackMatchReviewState.Suggested, result.Decision.State);
        Assert.InRange(
            result.Decision.Confidence,
            result.Decision.SuggestThreshold,
            result.Decision.AcceptThreshold - 0.0001);
        Assert.True(result.Decision.RequiresReview);
        Assert.Contains("below_accept_threshold_review", result.Decision.Warnings);
        if (differentVersion)
        {
            Assert.Null(result.SelectedExternal);
            Assert.Contains("semantic_version_mismatch_review", result.Decision.Warnings);
        }
        else Assert.Equal("tentative", result.SelectedExternal!.ExternalId);
    }

    [Fact]
    public async Task DownloadOnlyProviderEditionIsExcludedFromFallbackRoutes()
    {
        var user = Guid.CreateVersion7();
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming))
            .Returns(["deezer"]);
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Download))
            .Returns(["apple-download"]);
        gateway.Setup(item => item.SearchPlayableSongsAsync(
                It.IsAny<ProtocolExecutionContext>(),
                "Serenade No. 13 in G Major Wiener Philharmoniker",
                15))
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
        var service = new TrackMatchPlayableSearch(
            gateway.Object,
            new TrackMatchDecisionEngine(),
            NullLogger<TrackMatchPlayableSearch>.Instance);

        var result = await service.MatchAsync(
            Context(user),
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
                user, "main", Guid.CreateVersion7(), 2, 1,
                new HashSet<string>(StringComparer.Ordinal) { "music" }),
            [],
            null,
            CancellationToken.None);

        Assert.Equal(TrackMatchReviewState.Accepted, result.Decision.State);
        Assert.Single(result.Decision.Candidates);
        Assert.Single(result.RoutableExternalCandidates);
        Assert.Equal("deezer", result.RoutableExternalCandidates[0].ExternalProvider);
    }

    [Fact]
    public async Task Accepted_primary_query_skips_broader_searches()
    {
        var user = Guid.CreateVersion7();
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming))
            .Returns(["apple-download"]);
        gateway.Setup(item => item.SearchPlayableSongsAsync(
                It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 15))
            .ReturnsAsync([new Song
            {
                ExternalProvider = "apple-download", ExternalId = "1440638659", Title = "Crush",
                Artist = "Selena Gomez & The Scene", Album = "Kiss & Tell", Duration = 199
            }]);
        var service = new TrackMatchPlayableSearch(
            gateway.Object, new TrackMatchDecisionEngine(), NullLogger<TrackMatchPlayableSearch>.Instance);

        var result = await service.MatchAsync(
            Context(user),
            new ExternalTrackMatchSnapshot(
                "source", "spotify", "source-track", "Crush", "Selena Gomez & The Scene",
                "Kiss & Tell", null, 199_000, null, null, null),
            new TrackMatchScope(user, "main", Guid.CreateVersion7(), 2, 1,
                new HashSet<string>(StringComparer.Ordinal) { "music" }),
            [], null, CancellationToken.None);

        Assert.Equal(TrackMatchReviewState.Accepted, result.Decision.State);
        Assert.Equal("1440638659", result.SelectedExternal!.ExternalId);
        gateway.Verify(item => item.SearchPlayableSongsAsync(
            It.IsAny<ProtocolExecutionContext>(), "Crush Selena Gomez & The Scene", 15), Times.Once);
        gateway.Verify(item => item.SearchPlayableSongsAsync(
            It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<int>()), Times.Once);
        Assert.Single(result.Decision.Candidates);
    }

    [Fact]
    public async Task Pinned_primary_match_skips_broader_searches()
    {
        var user = Guid.CreateVersion7();
        var pinnedId = Guid.CreateVersion7();
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming)).Returns(["deezer"]);
        gateway.Setup(item => item.SearchPlayableSongsAsync(
                It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 15))
            .ReturnsAsync([]);
        var service = new TrackMatchPlayableSearch(
            gateway.Object, new TrackMatchDecisionEngine(), NullLogger<TrackMatchPlayableSearch>.Instance);

        var result = await service.MatchAsync(Context(user),
            new ExternalTrackMatchSnapshot("source", "spotify", "source-track", "Crush",
                "Selena Gomez & The Scene", "Kiss & Tell", null, 199_000, null, null, null),
            new TrackMatchScope(user, "main", Guid.CreateVersion7(), 2, 1,
                new HashSet<string>(StringComparer.Ordinal) { "music" }),
            [new LocalTrackMatchCandidate(pinnedId, user, "main", "music", "local-track", null,
                "Crush", "Selena Gomez & The Scene", "Kiss & Tell", null, 199_000, null, null, null)],
            new ScopedTrackMatchOverride(user, "spotify", "source-track", pinnedId), CancellationToken.None);

        Assert.Equal(TrackMatchReviewState.Pinned, result.Decision.State);
        Assert.Equal(pinnedId, result.Decision.SelectedLibraryTrackId);
        gateway.Verify(item => item.SearchPlayableSongsAsync(
            It.IsAny<ProtocolExecutionContext>(), "Crush Selena Gomez & The Scene", 15), Times.Once);
        gateway.Verify(item => item.SearchPlayableSongsAsync(
            It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public async Task Unresolved_primary_query_expands_search_and_keeps_existing_candidates()
    {
        var user = Guid.CreateVersion7();
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming)).Returns(["deezer"]);
        var queries = new List<string>();
        gateway.Setup(item => item.SearchPlayableSongsAsync(
                It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 15))
            .Returns((ProtocolExecutionContext _, string query, int _) =>
            {
                queries.Add(query);
                Assert.Equal("Crush Selena Gomez & The Scene", queries[0]);
                IReadOnlyList<Song> songs = query == "Crush Kiss & Tell"
                    ? [new Song { ExternalProvider = "deezer", ExternalId = "match", Title = "Crush",
                        Artist = "Selena Gomez & The Scene", Album = "Kiss & Tell", Duration = 199 }]
                    : query == "Crush Selena Gomez & The Scene"
                        ? [new Song { ExternalProvider = "deezer", ExternalId = "other", Title = "Another Song",
                            Artist = "Someone Else", Duration = 300 }]
                        : [];
                return Task.FromResult(songs);
            });
        var service = new TrackMatchPlayableSearch(
            gateway.Object, new TrackMatchDecisionEngine(), NullLogger<TrackMatchPlayableSearch>.Instance);

        var result = await service.MatchAsync(
            Context(user),
            new ExternalTrackMatchSnapshot("source", "spotify", "source-track", "Crush",
                "Selena Gomez & The Scene", "Kiss & Tell", null, 199_000, null, null, null),
            new TrackMatchScope(user, "main", Guid.CreateVersion7(), 2, 1,
                new HashSet<string>(StringComparer.Ordinal) { "music" }),
            [], null, CancellationToken.None);

        Assert.Equal(TrackMatchReviewState.Accepted, result.Decision.State);
        Assert.Equal("match", result.SelectedExternal!.ExternalId);
        Assert.Equal(2, result.Decision.Candidates.Count);
        Assert.Equal(new[] { "Crush Selena Gomez & The Scene", "Crush Kiss & Tell", "Crush",
            "Selena Gomez & The Scene", "Kiss & Tell" }, queries);
    }

    private static ProtocolExecutionContext Context(Guid user) => new(
        ProtocolKind.Jellyfin,
        "main",
        "principal",
        new AllstarrPrincipal(
            user, "jellyfin", "main", "principal", "Owner", false),
        "playable-search-test",
        DateTimeOffset.UtcNow.AddMinutes(1),
        CancellationToken.None);
}

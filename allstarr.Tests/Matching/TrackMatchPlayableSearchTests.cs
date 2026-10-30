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
    [Theory]
    [InlineData("apple-download", "deezer", false, false)]
    [InlineData("deezer", "apple-download", false, true)]
    [InlineData(null, "apple-download", false, true)]
    [InlineData("deezer", "apple-download", true, false)]
    [InlineData(null, "deezer", true, false)]
    public async Task MatchingSettlesIncompleteSearchOnlyWhenProviderPriorityAllowsIt(
        string? selectedProvider, string failedProvider, bool allowIncomplete, bool throws)
    {
        var user = Guid.CreateVersion7();
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming)).Returns(["apple-download", "deezer"]);
        IReadOnlyList<Song> songs = selectedProvider == null ? [] :
            [new Song { ExternalProvider = selectedProvider, ExternalId = "original", Title = "Track", Artist = "Artist", Duration = 210 }];
        gateway.Setup(item => item.SearchMatchingSongsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 15))
            .ReturnsAsync(new MatchingSongSearchResult(songs, [failedProvider]));
        var service = new TrackMatchPlayableSearch(gateway.Object, new(), NullLogger<TrackMatchPlayableSearch>.Instance);
        var search = service.MatchAsync(Context(user),
            new ExternalTrackMatchSnapshot("source", "spotify", "source", "Track", "Artist", null, null, 210_000, null, null, null),
            new TrackMatchScope(user, "main", null, 2, 1, new HashSet<string>()), [], null, default,
            allowIncompleteSearch: allowIncomplete);

        if (throws) await Assert.ThrowsAsync<ProtocolProviderUnavailableException>(() => search);
        else
        {
            var result = await search;
            Assert.Equal(selectedProvider == null ? TrackMatchReviewState.Unresolved : TrackMatchReviewState.Accepted, result.Decision.State);
            Assert.Equal(selectedProvider, result.SelectedExternal?.ExternalProvider);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MatchingKeepsCompletedFallbackSongsWhenAnotherFallbackFails(bool transportFailure)
    {
        var user = Guid.CreateVersion7();
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming)).Returns(["apple-download", "deezer"]);
        gateway.Setup(item => item.SearchMatchingSongsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 15))
            .ReturnsAsync(new MatchingSongSearchResult([], []));
        var failedQuery = gateway.Setup(item => item.SearchMatchingSongsAsync(It.IsAny<ProtocolExecutionContext>(), "Track Album", 15));
        if (transportFailure) failedQuery.ThrowsAsync(new ProtocolProviderUnavailableException(new(ProviderErrorKind.TransientFailure)));
        else failedQuery.ReturnsAsync(new MatchingSongSearchResult([], ["deezer"]));
        gateway.Setup(item => item.SearchMatchingSongsAsync(It.IsAny<ProtocolExecutionContext>(), "Track", 15))
            .ReturnsAsync(new MatchingSongSearchResult(
                [new Song { ExternalProvider = "apple-download", ExternalId = "original", Title = "Track", Artist = "Artist", Duration = 210 }], []));
        var service = new TrackMatchPlayableSearch(gateway.Object, new(), NullLogger<TrackMatchPlayableSearch>.Instance);
        var source = new ExternalTrackMatchSnapshot("source", "spotify", "source", "Track", "Artist", "Album", null, 210_000, null, null, null);
        var scope = new TrackMatchScope(user, "main", null, 2, 1, new HashSet<string>());

        if (transportFailure)
            await Assert.ThrowsAsync<ProtocolProviderUnavailableException>(() =>
                service.MatchAsync(Context(user), source, scope, [], null, default));
        else
            Assert.Equal("original", (await service.MatchAsync(Context(user), source, scope, [], null, default)).SelectedExternal!.ExternalId);
        var result = await service.MatchAsync(Context(user), source, scope, [], null, default, allowIncompleteSearch: true);
        Assert.Equal("original", result.SelectedExternal!.ExternalId);
        Assert.Equal(TrackMatchReviewState.Accepted, result.Decision.State);
    }

    [Fact]
    public async Task MatchingDoesNotSwallowQueryCancellation()
    {
        var user = Guid.CreateVersion7();
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming)).Returns(["deezer"]);
        gateway.Setup(item => item.SearchMatchingSongsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 15))
            .ThrowsAsync(new OperationCanceledException());
        var service = new TrackMatchPlayableSearch(gateway.Object, new(), NullLogger<TrackMatchPlayableSearch>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.MatchAsync(Context(user),
            new ExternalTrackMatchSnapshot("source", "spotify", "source", "Track", "Artist", null, null, 210_000, null, null, null),
            new TrackMatchScope(user, "main", null, 2, 1, new HashSet<string>()), [], null, default, allowIncompleteSearch: true));
    }

    [Fact]
    public async Task AcceptedLocalCandidateCanSettleDespiteIncompleteProviders()
    {
        var user = Guid.CreateVersion7();
        var id = Guid.CreateVersion7();
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming)).Returns(["apple-download", "deezer"]);
        gateway.Setup(item => item.SearchMatchingSongsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 15))
            .ReturnsAsync(new MatchingSongSearchResult([], ["apple-download", "deezer"]));
        var service = new TrackMatchPlayableSearch(gateway.Object, new(), NullLogger<TrackMatchPlayableSearch>.Instance);

        var result = await service.MatchAsync(Context(user),
            new ExternalTrackMatchSnapshot("source", "spotify", "source", "Track", "Artist", null, null, 210_000, null, null, null),
            new TrackMatchScope(user, "main", null, 2, 1, new HashSet<string> { "music" }),
            [new LocalTrackMatchCandidate(id, user, "main", "music", "local", null, "Track", "Artist", null, null, 210_000, null, null, null)],
            null, default);

        Assert.Equal(TrackMatchReviewState.Accepted, result.Decision.State);
        Assert.Equal(id, result.Decision.SelectedLibraryTrackId);
    }

    [Theory]
    [InlineData("All", "original")]
    [InlineData("CleanOnly", "clean")]
    public async Task MessyUsesTheDecisionOwnersEffectivePreferences(string filter, string expected)
    {
        var user = Guid.CreateVersion7();
        var policy = new EffectiveProviderPolicySnapshot(
            ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
            ImmutableHashSet<string>.Empty, "lossless", .07)
        { Preferences = new ListeningPreferences(ExplicitFilter: filter) };
        var policies = new Mock<IEffectiveProviderPolicyResolver>(MockBehavior.Strict);
        policies.Setup(item => item.ResolveForUserAsync(user, It.IsAny<CancellationToken>())).ReturnsAsync(policy);
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming)).Returns(["apple-download"]);
        gateway.Setup(item => item.SearchMatchingSongsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 15))
            .ReturnsAsync(new MatchingSongSearchResult([
                new Song { ExternalProvider = "apple-download", ExternalId = "clean", Title = "Messy", Artist = "Lola Young", Album = "This Wasn't Meant for You Anyway", Duration = 284, ExplicitContentLyrics = 3, Isrc = "GBUM72402815" },
                new Song { ExternalProvider = "apple-download", ExternalId = "original", Title = "Messy", Artist = "Lola Young", Album = "This Wasn't Meant for You Anyway", Duration = 284, ExplicitContentLyrics = 1, Isrc = "GBUM72401610" }
            ], []));
        var service = new TrackMatchPlayableSearch(gateway.Object, new(),
            NullLogger<TrackMatchPlayableSearch>.Instance, policies.Object);
        var source = new ExternalTrackMatchSnapshot("source", "listenbrainz", "messy", "Messy", "Lola Young",
            "This Wasn't Meant for You Anyway", null, 284068, null, null, null);

        var result = await service.MatchAsync(Context(user), source,
            new TrackMatchScope(user, "main", null, 2, 1, new HashSet<string>()), [], null, default);

        Assert.Equal(TrackMatchReviewState.Accepted, result.Decision.State);
        Assert.Equal(expected, result.SelectedExternal!.ExternalId);
        policies.Verify(item => item.ResolveForUserAsync(user, It.IsAny<CancellationToken>()), Times.AtLeastOnce());
        policies.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("Feel Good Inc.", "Gorillaz", "Demon Days", "feel good inc Gorillaz")]
    [InlineData("Moth to a Flame", "Swedish House Mafia & The Weeknd", "Dawn FM (Alternate World)", "moth to a flame Swedish House Mafia")]
    [InlineData("Danza kuduro", "Don Omar feat. Lucenzo", null, "danza kuduro Don Omar")]
    [InlineData("With You", "Chris Brown", null, "With You Chris Brown")]
    [InlineData("Let Me Love You (feat. Justin Bieber)", "DJ Snake", null, "Let Me Love You DJ Snake")]
    public async Task AutomaticSearchRecallsTracksUsingTitleAndPrimaryArtist(
        string title, string artist, string? album, string successfulQuery)
    {
        var user = Guid.CreateVersion7();
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming)).Returns(["deezer"]);
        var queries = new List<string>();
        gateway.Setup(item => item.SearchMatchingSongsAsync(
                It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 15))
            .Returns((ProtocolExecutionContext _, string query, int _) =>
            {
                queries.Add(query);
                IReadOnlyList<Song> songs = query == successfulQuery
                    ? [new Song { ExternalProvider = "deezer", ExternalId = "original", Title = title, Artist = artist, Album = album ?? string.Empty, Duration = 210 }]
                    : [];
                return Task.FromResult(new MatchingSongSearchResult(songs, []));
            });
        var service = new TrackMatchPlayableSearch(gateway.Object, new(),
            NullLogger<TrackMatchPlayableSearch>.Instance);

        var result = await service.MatchAsync(Context(user),
            new ExternalTrackMatchSnapshot("source", "spotify", "source-track", title, artist,
                album, null, 210_000, null, null, null),
            new TrackMatchScope(user, "main", null, 2, 1, new HashSet<string>()),
            [], null, CancellationToken.None);

        Assert.Contains(successfulQuery, queries);
        Assert.Contains(result.ExternalCandidates.Values, song => song.ExternalId == "original");
        Assert.Equal(queries.Count, queries.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.InRange(queries.Count, 1, 5);
    }

    [Fact]
    public async Task TitleOnlyFallbackRecallsAProviderThatCreditsTheFeatureInItsTitle()
    {
        var user = Guid.CreateVersion7();
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming)).Returns(["deezer"]);
        gateway.Setup(item => item.SearchMatchingSongsAsync(
                It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 15)).ReturnsAsync(new MatchingSongSearchResult([], []));
        gateway.Setup(item => item.SearchMatchingSongsAsync(
                It.IsAny<ProtocolExecutionContext>(), "Let Me Love You", 15))
            .ReturnsAsync(new MatchingSongSearchResult([new Song { ExternalProvider = "deezer", ExternalId = "original", Title = "Let Me Love You (feat. Justin Bieber)", Artist = "DJ Snake", Duration = 205 }], []));
        var service = new TrackMatchPlayableSearch(gateway.Object, new(),
            NullLogger<TrackMatchPlayableSearch>.Instance);

        var result = await service.MatchAsync(Context(user),
            new ExternalTrackMatchSnapshot("source", "spotify", "source-track", "Let Me Love You", "DJ Snake",
                null, null, 205_000, null, null, null),
            new TrackMatchScope(user, "main", null, 2, 1, new HashSet<string>()),
            [], null, CancellationToken.None);

        Assert.Contains(result.ExternalCandidates.Values, song => song.ExternalId == "original");
    }

    [Fact]
    public async Task AutomaticMatchingCarriesArtistGridBestVersionIntoScores()
    {
        var user = Guid.CreateVersion7();
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming)).Returns(["artistgrid"]);
        gateway.Setup(item => item.SearchMatchingSongsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 15))
            .ReturnsAsync(new MatchingSongSearchResult([new Song
            {
                ExternalProvider = "artistgrid", ExternalId = "best", Title = "Song", Artist = "Artist",
                Duration = 180, IsBestVersion = true
            }], []));
        var service = new TrackMatchPlayableSearch(gateway.Object, new(), NullLogger<TrackMatchPlayableSearch>.Instance);
        var source = new ExternalTrackMatchSnapshot("source", "spotify", "source", "Song", "Artist", null, null, 180000, null, null, null);
        var scope = new TrackMatchScope(user, "main", null, 2, 1, new HashSet<string>());
        var result = await service.MatchAsync(Context(user), source, scope, [], null, CancellationToken.None);
        var score = Assert.Single(result.Decision.Candidates);
        Assert.True(score.IsBestVersion);
        Assert.Contains("ArtistGrid best version +5", score.Reasons);
        Assert.Equal(1, score.Confidence);
    }

    [Fact]
    public async Task AutomaticMatchingFiltersManualOnlyCandidatesUsingTheListenersPolicy()
    {
        var user = Guid.CreateVersion7();
        var policy = new EffectiveProviderPolicySnapshot(
            ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
            ImmutableHashSet<string>.Empty, "lossless", 0)
        { ProviderModes = ImmutableDictionary<string, ProviderUseMode>.Empty.Add("manual", ProviderUseMode.ManualOnly) };
        var policies = new Mock<IEffectiveProviderPolicyResolver>(MockBehavior.Strict);
        policies.Setup(item => item.ResolveForUserAsync(user, It.IsAny<CancellationToken>())).ReturnsAsync(policy);
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming)).Returns(["manual", "deezer"]);
        gateway.Setup(item => item.SearchMatchingSongsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 15))
            .ReturnsAsync(new MatchingSongSearchResult([
                new Song { ExternalProvider = "manual", ExternalId = "demo", Title = "Song", Artist = "Artist", Duration = 180 },
                new Song { ExternalProvider = "deezer", ExternalId = "released", Title = "Song", Artist = "Artist", Duration = 180 }
            ], []));
        var service = new TrackMatchPlayableSearch(gateway.Object, new(),
            NullLogger<TrackMatchPlayableSearch>.Instance, policies.Object);
        var source = new ExternalTrackMatchSnapshot("source", "spotify", "source", "Song", "Artist", null, null, 180000, null, null, null);
        var scope = new TrackMatchScope(user, "main", Guid.NewGuid(), 2, 1, new HashSet<string>());
        var result = await service.MatchAsync(Context(user), source, scope, [], null, CancellationToken.None);
        Assert.Equal("deezer", result.SelectedExternal!.ExternalProvider);
        Assert.DoesNotContain(result.ExternalCandidates.Values, item => item.ExternalProvider == "manual");
        gateway.Invocations.Clear();
        var manualResult = await service.MatchAsync(Context(user), source with { ProviderId = "manual" },
            scope, [], null, CancellationToken.None);
        Assert.Null(manualResult.SelectedExternal);
        gateway.Verify(item => item.SearchMatchingSongsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<int>()), Times.Never);
    }
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
        gateway.Setup(item => item.SearchMatchingSongsAsync(
                It.IsAny<ProtocolExecutionContext>(), "Feels Calvin Harris", 15))
            .ReturnsAsync(new MatchingSongSearchResult(
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
                ], []));
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
    public async Task AutomaticSearchDoesNotRouteAConflictingArtistFromAnEquivalentTitleGroup()
    {
        var user = Guid.CreateVersion7();
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming))
            .Returns(["deezer", "qobuz"]);
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Download))
            .Returns(["deezer", "qobuz"]);
        gateway.Setup(item => item.SearchMatchingSongsAsync(
                It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 15))
            .ReturnsAsync(new MatchingSongSearchResult(
                [
                    new Song
                    {
                        ExternalProvider = "deezer",
                        ExternalId = "79590714",
                        Title = "Boom Clap",
                        Artist = "Charli xcx",
                        Album = "Boom Clap",
                        Duration = 169,
                        Isrc = "GBAHS1400160"
                    },
                    new Song
                    {
                        ExternalProvider = "deezer",
                        ExternalId = "80564534",
                        Title = "Boom Clap",
                        Artist = "Jazzy K",
                        Album = "Boom Clap",
                        Duration = 169,
                        Isrc = "USA2P1408144"
                    },
                    new Song
                    {
                        ExternalProvider = "qobuz",
                        ExternalId = "live-version",
                        Title = "Boom Clap (Live)",
                        Artist = "Charli xcx",
                        Album = "Boom Clap",
                        Duration = 169,
                        Isrc = "US-LIVE-BOOMCLAP"
                    }
                ], []));
        var service = new TrackMatchPlayableSearch(
            gateway.Object,
            new TrackMatchDecisionEngine(),
            NullLogger<TrackMatchPlayableSearch>.Instance);

        var result = await service.MatchAsync(
            Context(user),
            new ExternalTrackMatchSnapshot(
                "source", "spotify", "source-track", "Boom Clap",
                "Charli xcx", "SUCKER", null, 169_866, null, null, false),
            new TrackMatchScope(user, "main", Guid.CreateVersion7(), 2, 1,
                new HashSet<string>(StringComparer.Ordinal)),
            [], null, CancellationToken.None);

        Assert.Equal(TrackMatchReviewState.Accepted, result.Decision.State);
        Assert.Equal("79590714", result.SelectedExternal!.ExternalId);
        Assert.Single(result.RoutableExternalCandidates);
        Assert.Equal("79590714", result.RoutableExternalCandidates[0].ExternalId);
        var cover = Assert.Single(result.Decision.Candidates,
            candidate => candidate.ProviderTrackIds?.GetValueOrDefault("deezer") == "80564534");
        Assert.True(cover.Confidence < result.Decision.AcceptThreshold);
        var semanticAlternate = Assert.Single(result.Decision.Candidates,
            candidate => candidate.ProviderTrackIds?.GetValueOrDefault("qobuz") == "live-version");
        Assert.Contains("semantic_version_mismatch", semanticAlternate.Warnings);
    }

    [Fact]
    public async Task AutomaticSearchDoesNotRouteAnEquivalentAlternateWithAConflictingIsrc()
    {
        var user = Guid.CreateVersion7();
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming))
            .Returns(["deezer", "qobuz"]);
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Download))
            .Returns(["deezer", "qobuz"]);
        gateway.Setup(item => item.SearchMatchingSongsAsync(
                It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 15))
            .ReturnsAsync(new MatchingSongSearchResult(
                [
                    new Song
                    {
                        ExternalProvider = "deezer",
                        ExternalId = "79590714",
                        Title = "Boom Clap",
                        Artist = "Charli xcx",
                        Album = "SUCKER",
                        Duration = 169,
                        Isrc = "GBAHS1400160"
                    },
                    new Song
                    {
                        ExternalProvider = "qobuz",
                        ExternalId = "fixture-conflicting-isrc",
                        Title = "Boom Clap",
                        Artist = "Charli xcx",
                        Album = "SUCKER",
                        Duration = 169,
                        Isrc = "USA2P1408144"
                    }
                ], []));
        var service = new TrackMatchPlayableSearch(
            gateway.Object,
            new TrackMatchDecisionEngine(),
            NullLogger<TrackMatchPlayableSearch>.Instance);

        var result = await service.MatchAsync(
            Context(user),
            new ExternalTrackMatchSnapshot(
                "source", "spotify", "source-track", "Boom Clap",
                "Charli xcx", "SUCKER", null, 169_866, "GBAHS1400160", null, false),
            new TrackMatchScope(user, "main", Guid.CreateVersion7(), 2, 1,
                new HashSet<string>(StringComparer.Ordinal)),
            [], null, CancellationToken.None);

        Assert.Equal(TrackMatchReviewState.Accepted, result.Decision.State);
        Assert.Equal("79590714", result.SelectedExternal!.ExternalId);
        Assert.Single(result.RoutableExternalCandidates);
        Assert.Equal("79590714", result.RoutableExternalCandidates[0].ExternalId);
        var conflicting = Assert.Single(result.Decision.Candidates,
            candidate => candidate.ProviderTrackIds?.GetValueOrDefault("qobuz") == "fixture-conflicting-isrc");
        Assert.Equal(0, conflicting.Confidence);
        Assert.Contains("isrc_conflict", conflicting.Reasons);
    }

    [Fact]
    public async Task AutomaticSearchUsesConfiguredProviderPriorityWindows()
    {
        var user = Guid.CreateVersion7();
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming))
            .Returns(["apple-download", "deezer", "qobuz", "fourth"]);
        gateway.Setup(item => item.SearchMatchingSongsAsync(
                It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 15))
            .ReturnsAsync(new MatchingSongSearchResult(
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
            ], []));
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
        gateway.Setup(item => item.SearchMatchingSongsAsync(
                It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 15))
            .ReturnsAsync(new MatchingSongSearchResult(
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
            ], []));
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
        gateway.Setup(item => item.SearchMatchingSongsAsync(
                It.IsAny<ProtocolExecutionContext>(), "Link Up Metro Boomin", 15))
            .ReturnsAsync(new MatchingSongSearchResult(
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
            ], []));
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
        gateway.Verify(item => item.SearchMatchingSongsAsync(
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
        gateway.Verify(item => item.SearchMatchingSongsAsync(
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
        gateway.Setup(item => item.SearchMatchingSongsAsync(
                It.IsAny<ProtocolExecutionContext>(), "Feels Calvin Harris", 15))
            .ReturnsAsync(new MatchingSongSearchResult(
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
                ], []));
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
        gateway.Setup(item => item.SearchMatchingSongsAsync(
                It.IsAny<ProtocolExecutionContext>(),
                "Serenade No. 13 in G Major Wiener Philharmoniker",
                15))
            .ReturnsAsync(new MatchingSongSearchResult(
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
            ], []));
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
        gateway.Setup(item => item.SearchMatchingSongsAsync(
                It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 15))
            .ReturnsAsync(new MatchingSongSearchResult([new Song
            {
                ExternalProvider = "apple-download", ExternalId = "1440638659", Title = "Crush",
                Artist = "Selena Gomez & The Scene", Album = "Kiss & Tell", Duration = 199
            }], []));
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
        gateway.Verify(item => item.SearchMatchingSongsAsync(
            It.IsAny<ProtocolExecutionContext>(), "Crush Selena Gomez & The Scene", 15), Times.Once);
        gateway.Verify(item => item.SearchMatchingSongsAsync(
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
        gateway.Setup(item => item.SearchMatchingSongsAsync(
                It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 15))
            .ReturnsAsync(new MatchingSongSearchResult([], []));
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
        gateway.Verify(item => item.SearchMatchingSongsAsync(
            It.IsAny<ProtocolExecutionContext>(), "Crush Selena Gomez & The Scene", 15), Times.Once);
        gateway.Verify(item => item.SearchMatchingSongsAsync(
            It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public async Task Unresolved_primary_query_expands_search_and_keeps_existing_candidates()
    {
        var user = Guid.CreateVersion7();
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming)).Returns(["deezer"]);
        var queries = new List<string>();
        gateway.Setup(item => item.SearchMatchingSongsAsync(
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
                return Task.FromResult(new MatchingSongSearchResult(songs, []));
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
        Assert.Equal(new[] { "Crush Selena Gomez & The Scene", "crush Selena Gomez", "Crush Kiss & Tell",
            "Crush", "Kiss & Tell" }, queries);
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

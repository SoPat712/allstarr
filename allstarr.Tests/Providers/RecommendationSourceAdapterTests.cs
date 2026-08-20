using allstarr.Core.Capabilities;
using allstarr.Core.Intelligence;
using allstarr.Core.Storage;
using allstarr.Services.Recommendations;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace allstarr.Tests;

public sealed class RecommendationSourceAdapterTests
{
    [Fact]
    public async Task JellyfinInstantMix_PropagatesExactScopeAndExplainableSignals()
    {
        var client = new FakeClient { Items = [Item("backend:item-2", .82, "same-artist")] };
        var result = await new JellyfinInstantMixRecommendationProvider(client).RecommendAsync(Request(optedIn: true));

        Assert.Equal(RecommendationProviderState.Succeeded, result.State);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("backend:item-2", candidate.TrackKey);
        Assert.Equal("jellyfin-instant-mix", candidate.Source);
        Assert.Equal("same-artist", Assert.Single(candidate.Signals).Code);
        Assert.Equal(Request().Scope, client.LastQuery!.Scope);
    }

    [Fact]
    public async Task EverySourceDefensivelyRejectsMissingOptInWithoutCallingTransport()
    {
        var jellyfin = new FakeClient();
        var lastFm = new FakeClient { IsConfigured = true };
        var listenBrainz = new FakeClient { IsConfigured = true };
        var audioMuse = new FakeClient { IsAvailable = true };
        var local = new FakeClient();
        IRecommendationProvider[] providers =
        [
            new JellyfinInstantMixRecommendationProvider(jellyfin), new LastFmRecommendationProvider(lastFm),
            new ListenBrainzRecommendationProvider(listenBrainz), new AudioMuseRecommendationProvider(audioMuse),
            new LocalRuleRecommendationProvider(local)
        ];

        foreach (var provider in providers)
        {
            var result = await provider.RecommendAsync(Request(optedIn: false));
            Assert.Equal(RecommendationProviderState.Disabled, result.State);
            Assert.Equal("recommendation_opt_in_required", result.SafeErrorCode);
        }
        Assert.Equal(0, jellyfin.Calls + lastFm.Calls + listenBrainz.Calls + audioMuse.Calls + local.Calls);
    }

    [Fact]
    public async Task OptionalExternalSourcesReportTruthfulUnsupportedStateWhenUnconfigured()
    {
        var lastFm = await new LastFmRecommendationProvider(new FakeClient()).RecommendAsync(Request(true));
        var listenBrainz = await new ListenBrainzRecommendationProvider(new FakeClient()).RecommendAsync(Request(true));
        var audioMuse = await new AudioMuseRecommendationProvider(new FakeClient()).RecommendAsync(Request(true));

        Assert.Equal(RecommendationProviderState.Unsupported, lastFm.State);
        Assert.Equal("lastfm_recommendations_not_configured", lastFm.SafeErrorCode);
        Assert.Equal(RecommendationProviderState.Unsupported, listenBrainz.State);
        Assert.Equal("listenbrainz_recommendations_not_configured", listenBrainz.SafeErrorCode);
        Assert.Equal(RecommendationProviderState.Unsupported, audioMuse.State);
        Assert.Equal("audiomuse_unavailable", audioMuse.SafeErrorCode);
    }

    [Fact]
    public async Task TransportFailureIsRedactedAndTypedWithoutLeakingSecretOrUrl()
    {
        var client = new FakeClient { IsConfigured = true, Failure = new IOException("token-secret at https://signed.invalid/path") };
        var result = await new LastFmRecommendationProvider(client).RecommendAsync(Request(true));
        var serialized = System.Text.Json.JsonSerializer.Serialize(result);

        Assert.Equal(RecommendationProviderState.Degraded, result.State);
        Assert.Equal("lastfm_temporarily_unavailable", result.SafeErrorCode);
        Assert.DoesNotContain("token-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("signed.invalid", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnauthorizedTransportHasTypedSafeFailure()
    {
        var client = new FakeClient { IsConfigured = true, Failure = new UnauthorizedAccessException("raw credential") };
        var result = await new ListenBrainzRecommendationProvider(client).RecommendAsync(Request(true));

        Assert.Equal(RecommendationProviderState.Unauthorized, result.State);
        Assert.Equal("listenbrainz_account_unauthorized", result.SafeErrorCode);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task BackendWithoutInstantMixReportsUnsupportedInsteadOfEmptySuccess()
    {
        var result = await new JellyfinInstantMixRecommendationProvider(new FakeClient
        { Failure = new NotSupportedException("backend version details") }).RecommendAsync(Request(true));
        Assert.Equal(RecommendationProviderState.Unsupported, result.State);
        Assert.Equal("jellyfin_instant_mix_unsupported", result.SafeErrorCode);
    }

    [Fact]
    public async Task LocalRulesAreBoundedAndMalformedCandidatesDegradeSafely()
    {
        var client = new FakeClient { Items = [Item("one", .8, "genre-affinity"), Item("two", .7, "frequent-artist")] };
        var request = Request(true) with { Limit = 1 };
        var result = await new LocalRuleRecommendationProvider(client).RecommendAsync(request);
        Assert.Single(result.Candidates);

        client.Items = [new("", .9, [new("unsafe", .5, "bad")])];
        result = await new LocalRuleRecommendationProvider(client).RecommendAsync(Request(true));
        Assert.Equal(RecommendationProviderState.Degraded, result.State);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task CrossScopeProfileIsRejectedBeforeAnySourceCall()
    {
        var client = new FakeClient();
        var request = Request(true);
        request = request with { Profile = request.Profile with { OwnerUserId = Guid.CreateVersion7() } };

        await Assert.ThrowsAsync<ArgumentException>(() => new JellyfinInstantMixRecommendationProvider(client).RecommendAsync(request));
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task LastFmConcreteClientUsesTopHistoryAsSeedsThenReturnsSimilarRecordingIdentity()
    {
        var handler = new QueueHandler(
            """{"toptracks":{"track":[{"name":"Seed","artist":{"name":"Artist"}}]}}""",
            """{"similartracks":{"track":[{"name":"Future","artist":{"name":"Other"},"mbid":"11111111-1111-1111-1111-111111111111","match":"0.91"}]}}""");
        var client = new LastFmRecommendationClient(new HttpClient(handler),
            new SecretAccessor("""{"apiKey":"protected","username":"listener"}"""));

        var result = await client.GetSimilarTracksAsync(Query(), default);

        var item = Assert.Single(result);
        Assert.Equal("musicbrainz:11111111-1111-1111-1111-111111111111", item.TrackKey);
        Assert.Equal("Future", item.Identity!.Title);
        Assert.Equal(2, handler.Calls);
        Assert.All(handler.Requests, request => Assert.Equal("ws.audioscrobbler.com", request.Host));
    }

    [Fact]
    public async Task ListenBrainzConcreteClientReturnsCollaborativeFilteringMbids()
    {
        var handler = new QueueHandler("""{"payload":{"mbids":["22222222-2222-2222-2222-222222222222"]}}""");
        var client = new ListenBrainzRecommendationClient(new HttpClient(handler),
            new SecretAccessor("""{"token":"protected","username":"listener","baseUrl":"https://koito.example/apis/listenbrainz/1"}"""));

        var item = Assert.Single(await client.GetRecommendationsAsync(
            Query(), ListenBrainzDiscoveryKind.CollaborativeFiltering, default));

        Assert.Equal("22222222-2222-2222-2222-222222222222", item.Identity!.MusicBrainzRecordingId);
        Assert.Equal("koito.example", handler.Requests.Single().Host);
        Assert.Contains("/cf/recommendation/user/", handler.Requests.Single().AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListenBrainzConcreteClientReturnsLatestWeeklyPlaylistAndMonthlyTopTracks()
    {
        var handler = new QueueHandler(
            """{"count":1,"offset":0,"playlist_count":1,"playlists":[{"playlist":{"date":"2026-07-20T00:00:00Z","identifier":"https://listenbrainz.org/playlist/33333333-3333-3333-3333-333333333333","extension":{"https://musicbrainz.org/doc/jspf#playlist":{"additional_metadata":{"algorithm_metadata":{"source_patch":"weekly-exploration"}}}}}}]}""",
            """{"playlist":{"track":[{"title":"New Song","creator":"New Artist","album":"New Album","identifier":["https://musicbrainz.org/recording/44444444-4444-4444-4444-444444444444"]}]}}""",
            """{"payload":{"recordings":[{"track_name":"Favorite Song","artist_name":"Favorite Artist","release_name":"Favorite Album","recording_mbid":"55555555-5555-5555-5555-555555555555"}]}}""");
        var client = new ListenBrainzRecommendationClient(new HttpClient(handler),
            new SecretAccessor("""{"token":"protected","username":"listener"}"""));

        var weekly = Assert.Single(await client.GetRecommendationsAsync(
            Query(), ListenBrainzDiscoveryKind.WeeklyExploration, default));
        var top = Assert.Single(await client.GetRecommendationsAsync(
            Query(), ListenBrainzDiscoveryKind.TopRecordings, default));

        Assert.Equal("New Song", weekly.Identity!.Title);
        Assert.Equal("44444444-4444-4444-4444-444444444444", weekly.Identity.MusicBrainzRecordingId);
        Assert.Contains(weekly.Signals, signal => signal.Code == "listenbrainz-weekly-exploration");
        Assert.Equal("Favorite Song", top.Identity!.Title);
        Assert.Contains(top.Signals, signal => signal.Code == "listenbrainz-top-recordings");
        Assert.Equal(3, handler.Calls);
        Assert.Contains(handler.Requests, request => request.AbsolutePath.Contains("/playlists/createdfor", StringComparison.Ordinal));
        Assert.Contains(handler.Requests, request => request.AbsolutePath.Contains("/stats/user/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(ListenBrainzDiscoveryKind.WeeklyExploration, "listenbrainz-weekly-exploration")]
    [InlineData(ListenBrainzDiscoveryKind.WeeklyJams, "listenbrainz-weekly-jams")]
    [InlineData(ListenBrainzDiscoveryKind.TopRecordings, "listenbrainz-top-recordings")]
    public async Task ListenBrainzVariantsStayInsideTheCurrentRecommendationProvider(
        ListenBrainzDiscoveryKind kind, string providerId)
    {
        var client = new FakeClient { IsConfigured = true, Items = [Item("musicbrainz:track", .8, providerId)] };

        var result = await new ListenBrainzRecommendationProvider(client, kind, providerId)
            .RecommendAsync(Request(true));

        Assert.Equal(RecommendationProviderState.Succeeded, result.State);
        Assert.Equal(providerId, Assert.Single(result.Candidates).Source);
        Assert.Equal(kind, client.LastListenBrainzKind);
    }

    [Fact]
    public async Task AudioMuseConcreteClientUsesTypedCapabilityAndPreservesIdentity()
    {
        var accounts = new SecretAccessor("""{"token":"protected"}""");
        var catalog = new FakeClient
        {
            BackendIdentities = new Dictionary<string, RecommendationTrackIdentity>(StringComparer.Ordinal)
            {
                ["audio-333"] = new("audiomuse-ai", Title: "Future Song", Artist: "Future Artist", BackendItemId: "audio-333"),
                ["seed"] = new("audiomuse-ai", Title: "Seed", Artist: "Artist", BackendItemId: "seed"),
                ["audio-start"] = new("audiomuse-ai", Title: "Start", Artist: "Artist", BackendItemId: "audio-start"),
                ["audio-bridge"] = new("audiomuse-ai", Title: "Bridge", Artist: "Artist", BackendItemId: "audio-bridge"),
                ["audio-end"] = new("audiomuse-ai", Title: "End", Artist: "Artist", BackendItemId: "audio-end")
            }
        };
        var unavailable = new AudioMuseRecommendationClient(new ProviderRegistry([]), accounts, catalog);
        Assert.False(unavailable.IsAvailable);

        var capability = new IntelligenceCapability();
        var descriptor = new ProviderDescriptor("audiomuse-ai", "AudioMuse-AI", "External intelligence service",
            ProviderOrigin.Extension, "1", "1.0",
            [new(ProviderCapabilityKind.Intelligence, ProviderCapabilitySupportState.Supported,
                ProviderAccountRequirement.Required, "1.0", ["startAnalysis", "getAnalysisProgress",
                    "getClusters", "recommend", "search", "findPath", "blend", "getMap"],
                [ProviderAccountScope.User])],
            new ProviderPermissionDescriptor(), entryPoint: "index.js");
        var registry = new ProviderRegistry([new ProviderRegistration(descriptor, [capability])]);
        var missingAccount = new AudioMuseRecommendationClient(
            registry, new SecretAccessor("{}", configured: false), catalog);
        var missingReadiness = await new AudioMuseRecommendationProvider(missingAccount)
            .GetReadinessAsync(Query().Scope);
        Assert.Equal(RecommendationProviderReadinessState.Unconfigured, missingReadiness.State);
        Assert.Equal("audiomuse_scoped_account_missing", missingReadiness.SafeReasonCode);

        var configured = new AudioMuseRecommendationClient(registry, accounts, catalog);
        var item = Assert.Single(await configured.RecommendAsync(Query(), default));
        Assert.Equal("audio-333", item.Identity!.BackendItemId);
        Assert.NotNull(item.ProviderAccountId);
        Assert.Equal("account:1", item.SourceRevision);
        Assert.Equal(["seed"], capability.Seeds);
        Assert.Equal("audio-333", Assert.Single(await configured.FindSimilarAsync(
            Query().Scope, ["audio-start"], 10, default)).TrackKey);
        Assert.Equal(["audio-start", "audio-bridge", "audio-end"],
            (await configured.FindPathAsync(Query().Scope, "audio-start", "audio-end", 10, default))
            .Tracks.Select(value => value.TrackKey));
        Assert.Equal("audio-333", Assert.Single(await configured.BlendAsync(
            Query().Scope, ["audio-start"], ["audio-end"], 10, default)).TrackKey);
        var map = await configured.GetMapAsync(Query().Scope, new ProviderPageRequest(10, "map-cursor"), default);
        Assert.True(map.IsPartial);
        Assert.Equal("audio-333", Assert.Single(map.Items).Identity.BackendItemId);
        Assert.Equal("next", map.NextCursor);
        Assert.Equal(10, capability.LastMapPage!.Limit);
        Assert.Equal("map-cursor", capability.LastMapPage.Cursor);
        Assert.Equal(Query().Scope, catalog.LastResolvedScope);
        var analysis = await configured.StartAnalysisAsync(
            Query().Scope, false, "analysis-1", default);
        Assert.Equal("job-1", analysis.JobId);
        Assert.Equal(ProviderAnalysisState.Queued, analysis.State);
        Assert.Equal("analysis-1", capability.AnalysisIdempotencyKey);
        var restarted = new AudioMuseRecommendationClient(registry, accounts, catalog);
        var progress = await restarted.GetAnalysisProgressAsync(Query().Scope, analysis.JobId, default);
        Assert.Equal(ProviderAnalysisState.Running, progress.State);
        Assert.Equal(5, progress.Completed);
        Assert.Equal("job-1", capability.ProgressJobId);
        Assert.Equal("cluster-1", Assert.Single(await configured.GetClustersAsync(
            Query().Scope, 10, default)).Id);
        Assert.Equal("audio-333", Assert.Single(await configured.SearchAsync(
            Query().Scope, "future", false, 10, default)).TrackKey);
        var callsBeforeRejectedSeed = capability.CallCount;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => configured.BlendAsync(
            Query().Scope, ["other-user-track"], [], 10, default));
        Assert.Equal(callsBeforeRejectedSeed, capability.CallCount);

        _ = await restarted.RecommendAsync(Query(), default);
        var samples = new double[100];
        for (var index = 0; index < samples.Length; index++)
        {
            var started = Stopwatch.GetTimestamp();
            _ = await restarted.RecommendAsync(Query(), default);
            samples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        Array.Sort(samples);
        var p95 = samples[94];
        Assert.InRange(p95, 0, 100);
    }

    [Fact]
    public async Task ReadinessDistinguishesUnknownProtocolAndExactScopedAccountState()
    {
        var jellyfin = new JellyfinInstantMixRecommendationProvider(new FakeClient());
        var lastClient = new FakeClient { IsConfigured = false };
        var service = new RecommendationProviderStatusService(
            [jellyfin, new LastFmRecommendationProvider(lastClient)]);
        var scope = Request(true).Scope;
        var statuses = await service.ListAsync(scope);
        Assert.Equal(RecommendationProviderReadinessState.Ready, statuses.Single(x => x.ProviderId == "jellyfin-instant-mix").State);
        Assert.Equal(RecommendationProviderReadinessState.Unconfigured, statuses.Single(x => x.ProviderId == "lastfm").State);
    }

    private static RecommendationSourceItem Item(string key, double score, string signal) =>
        new(key, score, [new(signal, .8, $"Matched by {signal}")]);

    private static RecommendationRequest Request(bool optedIn = true)
    {
        var tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var owner = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var scope = new IntelligenceScope(tenant, owner, "jellyfin", "backend-a", "music");
        var profile = new ListeningProfile(tenant, owner, "backend-a", "music", 10, 1, 2,
            new Dictionary<string, double> { ["rock"] = .7 }, DateTimeOffset.UtcNow.AddDays(-7), DateTimeOffset.UtcNow);
        return new(scope, Guid.CreateVersion7(), profile, ["backend:seed"], 20, "recommendation-run", optedIn, default);
    }

    private static ScopedRecommendationQuery Query()
    { var request = Request(true); return new(request.Scope, request.Profile, request.SeedTrackKeys, request.Limit); }

    private sealed class FakeClient : IJellyfinInstantMixClient, ILastFmRecommendationClient,
        IListenBrainzRecommendationClient, IAudioMuseRecommendationClient, ILocalRecommendationCatalog
    {
        public bool IsConfigured { get; set; }
        public bool IsAvailable { get; set; }
        public int Calls { get; private set; }
        public ScopedRecommendationQuery? LastQuery { get; private set; }
        public ListenBrainzDiscoveryKind? LastListenBrainzKind { get; private set; }
        public IReadOnlyList<RecommendationSourceItem> Items { get; set; } = [];
        public Exception? Failure { get; set; }
        public IReadOnlyDictionary<string, RecommendationTrackIdentity> BackendIdentities { get; set; } =
            new Dictionary<string, RecommendationTrackIdentity>(StringComparer.Ordinal);
        public IntelligenceScope? LastResolvedScope { get; private set; }
        private Task<IReadOnlyList<RecommendationSourceItem>> Call(ScopedRecommendationQuery query)
        { Calls++; LastQuery = query; return Failure is null ? Task.FromResult(Items) : Task.FromException<IReadOnlyList<RecommendationSourceItem>>(Failure); }
        public Task<IReadOnlyList<RecommendationSourceItem>> GetInstantMixAsync(ScopedRecommendationQuery query, CancellationToken token) => Call(query);
        public Task<IReadOnlyList<RecommendationSourceItem>> GetSimilarTracksAsync(ScopedRecommendationQuery query, CancellationToken token) => Call(query);
        public Task<RecommendationProviderReadiness> GetReadinessAsync(IntelligenceScope scope, CancellationToken token) => Task.FromResult(new RecommendationProviderReadiness("fake", IsConfigured ? RecommendationProviderReadinessState.Ready : RecommendationProviderReadinessState.Unconfigured));
        public Task<IReadOnlyList<RecommendationSourceItem>> GetRecommendationsAsync(ScopedRecommendationQuery query,
            ListenBrainzDiscoveryKind kind, CancellationToken token)
        { LastListenBrainzKind = kind; return Call(query); }
        public Task<IReadOnlyList<RecommendationSourceItem>> RecommendAsync(ScopedRecommendationQuery query, CancellationToken token) => Call(query);
        public Task<bool> CheckHealthAsync(IntelligenceScope scope, CancellationToken token) => Task.FromResult(IsAvailable);
        public Task<IReadOnlyList<RecommendationSourceItem>> FindSimilarAsync(IntelligenceScope scope, IReadOnlyList<string> seedTrackIds, int limit, CancellationToken token) => throw new NotSupportedException();
        public Task<ProviderAnalysisProgress> StartAnalysisAsync(IntelligenceScope scope, bool rebuild, string idempotencyKey, CancellationToken token) => throw new NotSupportedException();
        public Task<ProviderAnalysisProgress> GetAnalysisProgressAsync(IntelligenceScope scope, string jobId, CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<AudioMuseCluster>> GetClustersAsync(IntelligenceScope scope, int limit, CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<RecommendationSourceItem>> SearchAsync(IntelligenceScope scope, string query, bool includeLyrics, int limit, CancellationToken token) => throw new NotSupportedException();
        public Task<AudioMusePathResult> FindPathAsync(IntelligenceScope scope, string startTrackId, string endTrackId, int limit, CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<RecommendationSourceItem>> BlendAsync(IntelligenceScope scope, IReadOnlyList<string> positiveSeedTrackIds, IReadOnlyList<string> negativeSeedTrackIds, int limit, CancellationToken token) => throw new NotSupportedException();
        public Task<AudioMuseMapPage> GetMapAsync(IntelligenceScope scope, ProviderPageRequest page, CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<RecommendationSourceItem>> FindRelatedAsync(ScopedRecommendationQuery query, CancellationToken token) => Call(query);
        public Task<bool> HasCoverageAsync(IntelligenceScope scope, bool requireMusicBrainz, CancellationToken token) => Task.FromResult(true);
        public Task<IReadOnlyList<string>> ResolveTrackKeysAsync(
            IntelligenceScope scope, IReadOnlyList<string> trackKeys, CancellationToken token)
        {
            LastResolvedScope = scope;
            return Task.FromResult<IReadOnlyList<string>>(trackKeys.Select(item =>
                    item.StartsWith("backend:", StringComparison.Ordinal) ? item[8..] : item)
                .Where(BackendIdentities.ContainsKey).Distinct(StringComparer.Ordinal).ToArray());
        }
        public Task<IReadOnlyDictionary<string, RecommendationTrackIdentity>> ResolveBackendItemsAsync(
            IntelligenceScope scope, IReadOnlyList<string> backendItemIds, CancellationToken token)
        {
            LastResolvedScope = scope;
            return Task.FromResult<IReadOnlyDictionary<string, RecommendationTrackIdentity>>(backendItemIds
                .Where(BackendIdentities.ContainsKey).Distinct(StringComparer.Ordinal)
                .ToDictionary(item => item, item => BackendIdentities[item], StringComparer.Ordinal));
        }
    }

    private sealed class SecretAccessor(string json, bool configured = true) : IScopedRecommendationAccountAccessor
    {
        public Task<bool> HasAccountAsync(IntelligenceScope scope, string providerId, CancellationToken token) =>
            Task.FromResult(configured);
        public Task<ProviderAccountContext?> FindAccountAsync(IntelligenceScope scope, string providerId, CancellationToken token) =>
            Task.FromResult<ProviderAccountContext?>(configured
                ? new(Guid.CreateVersion7(), providerId, ProviderAccountScope.User, 1,
                    tenantId: scope.TenantId, ownerUserId: scope.OwnerUserId)
                : null);
        public async Task<T> UseAsync<T>(IntelligenceScope scope, string providerId, Func<JsonElement, CancellationToken, Task<T>> operation, CancellationToken token)
        { using var document = JsonDocument.Parse(json); return await operation(document.RootElement, token); }
    }

    private sealed class IntelligenceCapability : IProviderIntelligenceCapability
    {
        public string ProviderId => "audiomuse-ai";
        public ProviderCapabilityKind Capability => ProviderCapabilityKind.Intelligence;
        public IReadOnlyList<string> Seeds { get; private set; } = [];
        public int CallCount { get; private set; }
        public string? AnalysisIdempotencyKey { get; private set; }
        public string? ProgressJobId { get; private set; }
        public ProviderPageRequest? LastMapPage { get; private set; }
        public Task<ProviderOutcome<IReadOnlyList<ProviderIntelligenceTrack>>> RecommendAsync(
            ProviderExecutionContext context, IReadOnlyList<string> seedTrackIds, int limit)
        {
            CallCount++;
            Seeds = seedTrackIds;
            return Task.FromResult(ProviderOutcome<IReadOnlyList<ProviderIntelligenceTrack>>.Success(
                [new("audio-333", "Future Song", "Future Artist", .8, "Future Album"),
                 new("audio-333", "Duplicate Song", "Duplicate Artist", .6),
                 new("other-user-track", "Private Song", "Private Artist", .7)]));
        }
        public Task<ProviderOutcome<ProviderAnalysisProgress>> StartAnalysisAsync(ProviderExecutionContext context, bool rebuild = false)
        {
            CallCount++;
            AnalysisIdempotencyKey = context.IdempotencyKey;
            return Task.FromResult(ProviderOutcome<ProviderAnalysisProgress>.Success(
                new("job-1", ProviderAnalysisState.Queued, 0, 10)));
        }
        public Task<ProviderOutcome<ProviderAnalysisProgress>> GetAnalysisProgressAsync(ProviderExecutionContext context, string jobId)
        {
            CallCount++;
            ProgressJobId = jobId;
            return Task.FromResult(ProviderOutcome<ProviderAnalysisProgress>.Success(
                new(jobId, ProviderAnalysisState.Running, 5, 10)));
        }
        public Task<ProviderOutcome<IReadOnlyList<ProviderIntelligenceCluster>>> GetClustersAsync(ProviderExecutionContext context, int limit = 50)
        {
            CallCount++;
            return Task.FromResult(ProviderOutcome<IReadOnlyList<ProviderIntelligenceCluster>>.Success(
                [new("cluster-1", "Bright songs", [new("audio-333", "Future Song", "Future Artist", .8)])]));
        }
        public Task<ProviderOutcome<IReadOnlyList<ProviderIntelligenceTrack>>> SearchAsync(ProviderExecutionContext context, string query, bool includeLyrics, int limit)
        {
            CallCount++;
            return Task.FromResult(ProviderOutcome<IReadOnlyList<ProviderIntelligenceTrack>>.Success(
                [new("audio-333", "Future Song", "Future Artist", .8)]));
        }
        public Task<ProviderOutcome<ProviderIntelligencePath>> FindPathAsync(ProviderExecutionContext context, string startTrackId, string endTrackId, int limit)
        {
            CallCount++;
            return Task.FromResult(ProviderOutcome<ProviderIntelligencePath>.Success(new(
                [new(startTrackId, "Start", "Artist", 1), new("audio-bridge", "Bridge", "Artist", .8),
                 new(endTrackId, "End", "Artist", 1)], .4)));
        }
        public Task<ProviderOutcome<IReadOnlyList<ProviderIntelligenceTrack>>> BlendAsync(ProviderExecutionContext context, IReadOnlyList<string> positiveSeedTrackIds, IReadOnlyList<string> negativeSeedTrackIds, int limit)
        {
            CallCount++;
            return Task.FromResult(ProviderOutcome<IReadOnlyList<ProviderIntelligenceTrack>>.Success(
                [new(positiveSeedTrackIds[0], "Seed", "Artist", 1),
                 new("audio-333", "Future Song", "Future Artist", .8)]));
        }
        public Task<ProviderOutcome<ProviderIntelligenceMapPage>> GetMapAsync(ProviderExecutionContext context, ProviderPageRequest page)
        {
            CallCount++;
            LastMapPage = page;
            return Task.FromResult(ProviderOutcome<ProviderIntelligenceMapPage>.Success(new(
                [new("audio-333", "Future Song", "Future Artist", .2, -.3),
                 new("other-user-track", "Private Song", "Private Artist", .4, .5)], "umap", "next")));
        }
        public Task<ProviderOutcome<bool>> DisconnectAsync(ProviderExecutionContext context) => throw new NotSupportedException();
    }

    private sealed class QueueHandler(params string[] responses) : HttpMessageHandler
    {
        private readonly Queue<string> responses = new(responses);
        public int Calls { get; private set; }
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(responses.Dequeue(), Encoding.UTF8, "application/json") });
        }
    }
}

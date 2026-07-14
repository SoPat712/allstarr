using allstarr.Core.Intelligence;
using allstarr.Services.Recommendations;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

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
        Assert.Equal("audiomuse_ai_sidecar_unavailable", audioMuse.SafeErrorCode);
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
            new SecretAccessor("""{"token":"protected","username":"listener"}"""));

        var item = Assert.Single(await client.GetRecommendationsAsync(Query(), default));

        Assert.Equal("22222222-2222-2222-2222-222222222222", item.Identity!.MusicBrainzRecordingId);
        Assert.Contains("/cf/recommendation/user/", handler.Requests.Single().AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AudioMuseConcreteClientIsOptionalBoundedAndPreservesIdentity()
    {
        var unavailable = new AudioMuseRecommendationClient(new HttpClient(new QueueHandler("{}")),
            new ConfigurationBuilder().AddInMemoryCollection().Build());
        Assert.False(unavailable.IsAvailable);

        var handler = new QueueHandler("""[{"item_id":"audio-333","title":"Future Song","author":"Future Artist","album":"Future Album","distance":0.25}]""");
        var configured = new AudioMuseRecommendationClient(new HttpClient(handler), new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Intelligence:AudioMuse:Url"] = "http://audiomuse.test" }).Build());
        var item = Assert.Single(await configured.RecommendAsync(Query(), default));
        Assert.Equal("audio-333", item.Identity!.BackendItemId);
        Assert.Equal("/api/sonic_fingerprint/generate", handler.Requests.Single().AbsolutePath);
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
        public IReadOnlyList<RecommendationSourceItem> Items { get; set; } = [];
        public Exception? Failure { get; set; }
        private Task<IReadOnlyList<RecommendationSourceItem>> Call(ScopedRecommendationQuery query)
        { Calls++; LastQuery = query; return Failure is null ? Task.FromResult(Items) : Task.FromException<IReadOnlyList<RecommendationSourceItem>>(Failure); }
        public Task<IReadOnlyList<RecommendationSourceItem>> GetInstantMixAsync(ScopedRecommendationQuery query, CancellationToken token) => Call(query);
        public Task<IReadOnlyList<RecommendationSourceItem>> GetSimilarTracksAsync(ScopedRecommendationQuery query, CancellationToken token) => Call(query);
        public Task<RecommendationProviderReadiness> GetReadinessAsync(IntelligenceScope scope, CancellationToken token) => Task.FromResult(new RecommendationProviderReadiness("fake", IsConfigured ? RecommendationProviderReadinessState.Ready : RecommendationProviderReadinessState.Unconfigured));
        public Task<IReadOnlyList<RecommendationSourceItem>> GetRecommendationsAsync(ScopedRecommendationQuery query, CancellationToken token) => Call(query);
        public Task<IReadOnlyList<RecommendationSourceItem>> RecommendAsync(ScopedRecommendationQuery query, CancellationToken token) => Call(query);
        public Task<bool> CheckHealthAsync(IntelligenceScope scope, CancellationToken token) => Task.FromResult(IsAvailable);
        public Task<IReadOnlyList<RecommendationSourceItem>> FindRelatedAsync(ScopedRecommendationQuery query, CancellationToken token) => Call(query);
        public Task<bool> HasCoverageAsync(IntelligenceScope scope, bool requireMusicBrainz, CancellationToken token) => Task.FromResult(true);
    }

    private sealed class SecretAccessor(string json) : IScopedRecommendationAccountAccessor
    {
        public Task<bool> HasAccountAsync(IntelligenceScope scope, string providerId, CancellationToken token) => Task.FromResult(true);
        public async Task<T> UseAsync<T>(IntelligenceScope scope, string providerId, Func<JsonElement, CancellationToken, Task<T>> operation, CancellationToken token)
        { using var document = JsonDocument.Parse(json); return await operation(document.RootElement, token); }
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

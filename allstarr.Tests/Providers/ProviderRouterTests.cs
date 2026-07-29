using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Routing;
using allstarr.Core.Storage;

namespace allstarr.Tests;

public sealed class ProviderRouterTests
{
    [Fact]
    public async Task Plan_OrdersEligibleProvidersAndAllowedTypedFailureAdvancesToVerifiedIdentity()
    {
        var identity = new FakeIdentityService(ProviderIdentityVerification.Verified);
        var router = Router(
            [Metadata("spotify"), Metadata("deezer")],
            identity: identity);
        var request = Request(
            ProviderCapabilityKind.Metadata,
            ["spotify", "deezer"],
            source: Track("spotify", "source-secret-id"));

        var plan = await router.PlanAsync<IProviderMetadataCapability>(request);
        var fallback = router.EvaluateFallback(
            plan,
            0,
            new ProviderError(ProviderErrorKind.TransientFailure));

        Assert.Equal(["spotify", "deezer"], plan.Candidates.Select(item => item.Provider.Id));
        Assert.Equal("deezer-verified-target", plan.Candidates[1].TrackId!.Value);
        Assert.Equal(ProviderFallbackDisposition.Advance, fallback.Disposition);
        Assert.Equal("deezer", fallback.NextCandidate!.Provider.Id);
        Assert.Equal("fallback-transient-failure", fallback.ReasonCode);
        Assert.Single(identity.Translations);
    }

    [Theory]
    [InlineData(ProviderErrorKind.AccountNeedsConfiguration)]
    [InlineData(ProviderErrorKind.Unauthorized)]
    [InlineData(ProviderErrorKind.Forbidden)]
    [InlineData(ProviderErrorKind.PermanentFailure)]
    [InlineData(ProviderErrorKind.Canceled)]
    public async Task Fallback_DoesNotCrossProvidersForNonFallbackFailures(ProviderErrorKind kind)
    {
        var router = Router(
            [Metadata("spotify"), Metadata("deezer")],
            identity: new FakeIdentityService(ProviderIdentityVerification.Verified));
        var plan = await router.PlanAsync<IProviderMetadataCapability>(Request(
            ProviderCapabilityKind.Metadata,
            ["spotify", "deezer"],
            source: Track("spotify", "source")));

        var fallback = router.EvaluateFallback(plan, 0, new ProviderError(kind));

        Assert.Equal(ProviderFallbackDisposition.StopFailure, fallback.Disposition);
        Assert.Null(fallback.NextCandidate);
    }

    [Fact]
    public async Task Plan_RejectsCrossProviderFallbackWithoutVerifiedExactIdentity()
    {
        var router = Router(
            [Metadata("spotify"), Metadata("deezer")],
            identity: new FakeIdentityService(ProviderIdentityVerification.Unknown));

        var plan = await router.PlanAsync<IProviderMetadataCapability>(Request(
            ProviderCapabilityKind.Metadata,
            ["spotify", "deezer"],
            source: Track("spotify", "source")));

        Assert.Single(plan.Candidates);
        Assert.Equal("spotify", plan.Candidates[0].Provider.Id);
        Assert.Contains(plan.Decision.Candidates, item =>
            item.ProviderId == "deezer" && item.ReasonCode == "verified-identity-required");
    }

    [Fact]
    public async Task Plan_EnforcesAccountScopeEnabledAndRevisionBeforePriority()
    {
        var tenant = Guid.CreateVersion7();
        var user = Guid.CreateVersion7();
        var actor = Actor(tenant, user);
        var validAccount = Account("valid", tenant, user, revision: 3);
        var wrongScope = new ProviderAccountContext(
            Guid.CreateVersion7(),
            "wrong-scope",
            ProviderAccountScope.Global,
            1,
            resolutionReason: "global-account");
        var disabled = new ProviderAccountContext(
            Guid.CreateVersion7(),
            "disabled",
            ProviderAccountScope.User,
            1,
            enabled: false,
            tenantId: tenant,
            ownerUserId: user);
        var accounts = new FakeAccountResolver(new Dictionary<string, ProviderRouteAccountResolution>
        {
            ["valid"] = new(validAccount, 3),
            ["wrong-scope"] = new(wrongScope, 1),
            ["disabled"] = new(disabled, 1),
            ["stale"] = new(Account("stale", tenant, user, revision: 1), 2)
        });
        var router = Router(
            [
                Metadata("wrong-scope", ProviderAccountRequirement.Required, [ProviderAccountScope.User]),
                Metadata("disabled", ProviderAccountRequirement.Required, [ProviderAccountScope.User]),
                Metadata("stale", ProviderAccountRequirement.Required, [ProviderAccountScope.User]),
                Metadata("valid", ProviderAccountRequirement.Required, [ProviderAccountScope.User])
            ],
            accounts: accounts);

        var plan = await router.PlanAsync<IProviderMetadataCapability>(Request(
            ProviderCapabilityKind.Metadata,
            ["wrong-scope", "disabled", "stale", "valid"],
            actor: actor,
            states: [new ProviderRouteProviderState("valid", expectedAccountRevision: 3)]));

        Assert.Single(plan.Candidates);
        Assert.Equal("valid", plan.Candidates[0].Provider.Id);
        Assert.Contains(plan.Decision.Candidates, item => item.ReasonCode == "account-scope-denied");
        Assert.Contains(plan.Decision.Candidates, item => item.ReasonCode == "account-disabled");
        Assert.Contains(plan.Decision.Candidates, item => item.ReasonCode == "account-stale");
    }

    [Fact]
    public async Task Plan_RemovesDisabledCapabilityWithoutRemovingAnotherProvider()
    {
        var router = Router([Metadata("spotify"), Metadata("deezer")]);

        var plan = await router.PlanAsync<IProviderMetadataCapability>(Request(
            ProviderCapabilityKind.Metadata,
            ["spotify", "deezer"],
            states: [new ProviderRouteProviderState("spotify", capabilityEnabled: false)]));

        Assert.Equal("deezer", Assert.Single(plan.Candidates).Provider.Id);
        Assert.Contains(plan.Decision.Candidates, item =>
            item.ProviderId == "spotify" && item.ReasonCode == "capability-disabled");
    }

    [Fact]
    public async Task Plan_UsesCapabilityPriorityOnlyAfterQualityPolicyFiltering()
    {
        var router = Router([Streaming("lossy"), Streaming("lossless")]);
        var policy = Policy(minimum: ProviderAudioQuality.Lossless);

        var plan = await router.PlanAsync<IProviderStreamingCapability>(Request(
            ProviderCapabilityKind.Streaming,
            ["lossy", "lossless"],
            policy: policy,
            states:
            [
                new ProviderRouteProviderState("lossy", availableQualities: [ProviderAudioQuality.Lossy]),
                new ProviderRouteProviderState("lossless", availableQualities: [ProviderAudioQuality.Lossless])
            ]));

        Assert.Equal("lossless", Assert.Single(plan.Candidates).Provider.Id);
        Assert.Contains(plan.Decision.Candidates, item =>
            item.ProviderId == "lossy" && item.ReasonCode == "quality-policy-denied");
    }

    [Fact]
    public async Task Plan_RejectsOpenCircuitAndUnreadyDeclaredSidecar()
    {
        var tenant = Guid.CreateVersion7();
        var user = Guid.CreateVersion7();
        var account = Account("circuit", tenant, user);
        var accounts = new FakeAccountResolver(new Dictionary<string, ProviderRouteAccountResolution>
        {
            ["circuit"] = new(account, account.Revision)
        });
        var health = new FakeHealthSource(new Dictionary<string, ProviderRouteHealthSnapshot>
        {
            ["circuit"] = new(ProviderRouteHealthState.Healthy, CircuitOpen: true)
        });
        var router = Router(
            [
                Metadata("circuit", ProviderAccountRequirement.Required, [ProviderAccountScope.User]),
                Metadata("sidecar", sidecar: "sidecar-runtime"),
                Metadata("ready", sidecar: "ready-runtime")
            ],
            accounts,
            health,
            new FakeSidecarSource(["ready-runtime"]));

        var plan = await router.PlanAsync<IProviderMetadataCapability>(Request(
            ProviderCapabilityKind.Metadata,
            ["circuit", "sidecar", "ready"],
            actor: Actor(tenant, user)));

        Assert.Equal("ready", Assert.Single(plan.Candidates).Provider.Id);
        Assert.Contains(plan.Decision.Candidates, item => item.ReasonCode == "circuit-open");
        Assert.Contains(plan.Decision.Candidates, item => item.ReasonCode == "sidecar-not-ready");
    }

    [Fact]
    public async Task DecisionRecord_IsExplainableAndDoesNotContainOpaqueIdsOrAccountDetails()
    {
        const string rawTrack = "track-token=https://secret.invalid/audio?token=very-secret";
        var router = Router(
            [Metadata("spotify"), Metadata("deezer")],
            identity: new FakeIdentityService(ProviderIdentityVerification.Verified));
        var plan = await router.PlanAsync<IProviderMetadataCapability>(Request(
            ProviderCapabilityKind.Metadata,
            ["spotify", "deezer"],
            source: Track("spotify", rawTrack)));

        var serialized = JsonSerializer.Serialize(plan.Decision);

        Assert.Contains("spotify", serialized, StringComparison.Ordinal);
        Assert.Contains("deezer", serialized, StringComparison.Ordinal);
        Assert.Contains("selected", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(rawTrack, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("very-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plan_RejectsUntypedPlanningAndProviderOutsideAllowlist()
    {
        var router = Router([Metadata("spotify"), Metadata("deezer")]);
        var request = Request(
            ProviderCapabilityKind.Metadata,
            ["spotify", "deezer"],
            policy: Policy(allowedProviders: ["deezer"]));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            router.PlanAsync<IProviderCapability>(request));
        var plan = await router.PlanAsync<IProviderMetadataCapability>(request);

        Assert.Equal("deezer", Assert.Single(plan.Candidates).Provider.Id);
        Assert.Contains(plan.Decision.Candidates, item => item.ReasonCode == "provider-not-allowed");
    }

    [Fact]
    public async Task Plan_RejectsDownloadWithoutDurableIdempotencyKey()
    {
        var router = Router([Download("download-provider")]);

        var plan = await router.PlanAsync<IProviderDownloadCapability>(Request(
            ProviderCapabilityKind.Download,
            ["download-provider"],
            states:
            [
                new ProviderRouteProviderState(
                    "download-provider",
                    availableQualities: [ProviderAudioQuality.Lossless])
            ]));

        Assert.Empty(plan.Candidates);
        Assert.Equal("idempotency-key-required", Assert.Single(plan.Decision.Candidates).ReasonCode);
    }

    [Fact]
    public async Task Plan_EnforcesExplicitTermsRateLimitAndStoragePolicyInputs()
    {
        var metadataRouter = Router([
            Metadata("clean"), Metadata("explicit"), Metadata("unknown"),
            Metadata("terms"), Metadata("rate")
        ]);
        var metadata = await metadataRouter.PlanAsync<IProviderMetadataCapability>(Request(
            ProviderCapabilityKind.Metadata,
            ["explicit", "unknown", "clean", "terms", "rate"],
            policy: Policy(explicitContent: ProviderExplicitContentPolicy.CleanOnly),
            states:
            [
                new ProviderRouteProviderState("clean", isExplicit: false),
                new ProviderRouteProviderState("explicit", isExplicit: true),
                new ProviderRouteProviderState("terms", isExplicit: false, providerTermsAllowed: false),
                new ProviderRouteProviderState("rate", isExplicit: false, rateLimitBudgetAvailable: false)
            ]));

        Assert.Equal("clean", Assert.Single(metadata.Candidates).Provider.Id);
        Assert.Contains(metadata.Decision.Candidates, item => item.ProviderId == "explicit" && item.ReasonCode == "explicit-content-denied");
        Assert.Contains(metadata.Decision.Candidates, item => item.ProviderId == "unknown" && item.ReasonCode == "explicit-state-unknown");
        Assert.Contains(metadata.Decision.Candidates, item => item.ProviderId == "terms" && item.ReasonCode == "provider-terms-denied");
        Assert.Contains(metadata.Decision.Candidates, item => item.ProviderId == "rate" && item.ReasonCode == "rate-limit-budget-exhausted");

        var download = await Router([Download("full")]).PlanAsync<IProviderDownloadCapability>(Request(
            ProviderCapabilityKind.Download,
            ["full"],
            idempotencyKey: "download-1",
            states:
            [
                new ProviderRouteProviderState("full",
                    availableQualities: [ProviderAudioQuality.Lossless],
                    storageCapacityAvailable: false)
            ]));
        Assert.Empty(download.Candidates);
        Assert.Equal("storage-capacity-unavailable", Assert.Single(download.Decision.Candidates).ReasonCode);
    }

    [Fact]
    public async Task PreferClean_ReordersEligibleCandidatesWithoutDiscardingExplicitFallback()
    {
        var plan = await Router([Metadata("explicit"), Metadata("clean")])
            .PlanAsync<IProviderMetadataCapability>(Request(
                ProviderCapabilityKind.Metadata,
                ["explicit", "clean"],
                policy: Policy(explicitContent: ProviderExplicitContentPolicy.PreferClean),
                states:
                [
                    new ProviderRouteProviderState("explicit", isExplicit: true),
                    new ProviderRouteProviderState("clean", isExplicit: false)
                ]));

        Assert.Equal(["clean", "explicit"], plan.Candidates.Select(item => item.Provider.Id));
    }

    [Fact]
    public async Task Plan_RejectsAnExpiredRequestBeforeAccountOrProviderRouting()
    {
        var accounts = new FakeAccountResolver();
        var router = Router([Metadata("spotify")], accounts);
        var request = Request(
            ProviderCapabilityKind.Metadata,
            ["spotify"],
            deadline: DateTimeOffset.UtcNow.AddMinutes(-1));

        await Assert.ThrowsAsync<TimeoutException>(() =>
            router.PlanAsync<IProviderMetadataCapability>(request));
        Assert.Equal(0, accounts.CallCount);
    }

    private static ProviderRouter Router(
        IEnumerable<ProviderRegistration> registrations,
        IProviderRouteAccountResolver? accounts = null,
        IProviderRouteHealthSource? health = null,
        IProviderRouteSidecarSource? sidecars = null,
        ITrackIdentityService? identity = null) => new(
        new ProviderRegistry(registrations),
        accounts ?? new FakeAccountResolver(),
        health ?? new FakeHealthSource(),
        sidecars ?? new FakeSidecarSource(),
        identity ?? new FakeIdentityService(ProviderIdentityVerification.Verified));

    private static ProviderRegistration Metadata(
        string providerId,
        ProviderAccountRequirement requirement = ProviderAccountRequirement.None,
        IEnumerable<ProviderAccountScope>? scopes = null,
        string? sidecar = null)
    {
        var capability = new ProviderCapabilityDescriptor(
            ProviderCapabilityKind.Metadata,
            ProviderCapabilitySupportState.Supported,
            requirement,
            "1.0",
            ["searchTracks", "getTrack"],
            scopes,
            sidecar);
        return Registration(providerId, capability, new FakeMetadataCapability(providerId));
    }

    private static ProviderRegistration Streaming(string providerId)
    {
        var capability = new ProviderCapabilityDescriptor(
            ProviderCapabilityKind.Streaming,
            ProviderCapabilitySupportState.Supported,
            ProviderAccountRequirement.None,
            "1.0",
            ["getStreamLease"]);
        return Registration(providerId, capability, new FakeStreamingCapability(providerId));
    }

    private static ProviderRegistration Download(string providerId)
    {
        var capability = new ProviderCapabilityDescriptor(
            ProviderCapabilityKind.Download,
            ProviderCapabilitySupportState.Supported,
            ProviderAccountRequirement.None,
            "1.0",
            ["checkAvailability", "download"]);
        return Registration(providerId, capability, new FakeDownloadCapability(providerId));
    }

    private static ProviderRegistration Registration(
        string providerId,
        ProviderCapabilityDescriptor capability,
        IProviderCapability implementation) => new(
        new ProviderDescriptor(
            providerId,
            providerId,
            $"{providerId} fake provider",
            ProviderOrigin.BuiltIn,
            "1",
            "1.0",
            [capability],
            new ProviderPermissionDescriptor()),
        [implementation]);

    private static ProviderRouteRequest Request(
        ProviderCapabilityKind capability,
        IEnumerable<string> priority,
        ProviderActorContext? actor = null,
        ProviderExecutionPolicy? policy = null,
        IEnumerable<ProviderRouteProviderState>? states = null,
        ProviderExternalResourceId? source = null,
        DateTimeOffset? deadline = null,
        string? idempotencyKey = null) => new(
        capability,
        actor ?? Actor(Guid.CreateVersion7(), Guid.CreateVersion7()),
        policy ?? Policy(),
        "router-test",
        "router-correlation",
        deadline ?? DateTimeOffset.UtcNow.AddMinutes(1),
        priority,
        states,
        sourceTrackId: source,
        idempotencyKey: idempotencyKey);

    private static ProviderExecutionPolicy Policy(
        ProviderAudioQuality minimum = ProviderAudioQuality.Any,
        IEnumerable<string>? allowedProviders = null,
        ProviderExplicitContentPolicy explicitContent = ProviderExplicitContentPolicy.Allow) => new(
        new ProviderQualityPolicy(minimum, ProviderAudioQuality.HighResolution, allowTranscode: false),
        explicitContent,
        allowFallback: true,
        allowSharedAccount: true,
        allowManagedDownloads: true,
        allowedProviders);

    private static ProviderActorContext Actor(Guid tenantId, Guid userId) => new(
        tenantId,
        ProviderActorKind.User,
        userId,
        new ProviderBackendPrincipal("jellyfin", "main", userId.ToString("N")));

    private static ProviderAccountContext Account(
        string providerId,
        Guid tenantId,
        Guid userId,
        long revision = 1) => new(
        Guid.CreateVersion7(),
        providerId,
        ProviderAccountScope.User,
        revision,
        tenantId: tenantId,
        ownerUserId: userId);

    private static ProviderExternalResourceId Track(string providerId, string value) =>
        new(providerId, ProviderResourceKind.Track, value);

    private sealed class FakeAccountResolver(
        IReadOnlyDictionary<string, ProviderRouteAccountResolution>? resolutions = null)
        : IProviderRouteAccountResolver
    {
        private readonly IReadOnlyDictionary<string, ProviderRouteAccountResolution> _resolutions =
            resolutions ?? new Dictionary<string, ProviderRouteAccountResolution>();

        public int CallCount { get; private set; }

        public Task<ProviderRouteAccountResolution?> ResolveAsync(
            ProviderRouteAccountRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(_resolutions.GetValueOrDefault(request.ProviderId));
        }
    }

    private sealed class FakeHealthSource(
        IReadOnlyDictionary<string, ProviderRouteHealthSnapshot>? snapshots = null)
        : IProviderRouteHealthSource
    {
        private readonly IReadOnlyDictionary<string, ProviderRouteHealthSnapshot> _snapshots =
            snapshots ?? new Dictionary<string, ProviderRouteHealthSnapshot>();

        public ProviderRouteHealthSnapshot Get(
            string providerId,
            Guid providerAccountId,
            ProviderCapabilityKind capability) =>
            _snapshots.GetValueOrDefault(providerId) ??
            new ProviderRouteHealthSnapshot(ProviderRouteHealthState.Unknown, CircuitOpen: false);
    }

    private sealed class FakeSidecarSource(IEnumerable<string>? ready = null) : IProviderRouteSidecarSource
    {
        private readonly HashSet<string> _ready = new(ready ?? [], StringComparer.Ordinal);

        public bool IsReady(string dependencyId) => _ready.Contains(dependencyId);
    }

    private sealed class FakeIdentityService(ProviderIdentityVerification verification) : ITrackIdentityService
    {
        public List<(string Source, string Target)> Translations { get; } = [];

        public Task<CanonicalRecordingCreationResult> CreateRecordingAsync(
            ProviderActorContext actor,
            string correlationId,
            string? isrc = null,
            string? musicBrainzRecordingId = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<TrackIdentityLinkResult> LinkAsync(
            ProviderExecutionContext executionContext,
            TrackIdentityLinkRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<TrackIdentityResolution?> ResolveAsync(
            ProviderExecutionContext executionContext,
            ProviderExternalResourceId externalId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<TrackIdentityTranslationResult> TranslateAsync(
            ProviderExecutionContext sourceContext,
            ProviderExternalResourceId sourceId,
            ProviderExecutionContext targetContext,
            ProviderTrackIdentityTarget target,
            CancellationToken cancellationToken = default)
        {
            Translations.Add((sourceContext.ProviderId, targetContext.ProviderId));
            var targetId = Track(target.ProviderId, $"{target.ProviderId}-verified-target");
            var resolution = new TrackIdentityResolution(
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                targetId,
                ProviderIdentityScope.Catalog,
                null,
                verification,
                "test-fixture",
                1);
            return Task.FromResult(new TrackIdentityTranslationResult(
                TrackIdentityTranslationStatus.Translated,
                resolution.CanonicalRecordingId,
                null,
                resolution));
        }
    }

    private sealed class FakeMetadataCapability(string providerId) : IProviderMetadataCapability
    {
        public string ProviderId { get; } = providerId;
        public ProviderCapabilityKind Capability => ProviderCapabilityKind.Metadata;

        public Task<ProviderOutcome<ProviderPage<ProviderTrackMetadata>>> SearchTracksAsync(
            ProviderExecutionContext context, ProviderMetadataSearchRequest request) => throw new NotSupportedException();
        public Task<ProviderOutcome<ProviderTrackMetadata>> GetTrackAsync(
            ProviderExecutionContext context, ProviderTrackLookupRequest request) => throw new NotSupportedException();
        public Task<ProviderOutcome<ProviderTrackMetadata>> LookupByIsrcAsync(
            ProviderExecutionContext context, ProviderIsrcLookupRequest request) => throw new NotSupportedException();
        public Task<ProviderOutcome<ProviderPage<ProviderAlbumMetadata>>> SearchAlbumsAsync(
            ProviderExecutionContext context, ProviderMetadataSearchRequest request) => throw new NotSupportedException();
        public Task<ProviderOutcome<ProviderAlbumMetadata>> GetAlbumAsync(
            ProviderExecutionContext context, ProviderAlbumLookupRequest request) => throw new NotSupportedException();
        public Task<ProviderOutcome<ProviderPage<ProviderArtistMetadata>>> SearchArtistsAsync(
            ProviderExecutionContext context, ProviderMetadataSearchRequest request) => throw new NotSupportedException();
        public Task<ProviderOutcome<ProviderArtistMetadata>> GetArtistAsync(
            ProviderExecutionContext context, ProviderArtistLookupRequest request) => throw new NotSupportedException();
    }

    private sealed class FakeStreamingCapability(string providerId) : IProviderStreamingCapability
    {
        public string ProviderId { get; } = providerId;
        public ProviderCapabilityKind Capability => ProviderCapabilityKind.Streaming;

        public Task<ProviderOutcome<ProviderStreamLease>> GetStreamLeaseAsync(
            ProviderExecutionContext context, ProviderStreamLeaseRequest request) => throw new NotSupportedException();
        public Task<ProviderOutcome<ProviderStreamProbeResult>> ProbeStreamAsync(
            ProviderExecutionContext context, ProviderStreamLeaseRequest request) => throw new NotSupportedException();
    }

    private sealed class FakeDownloadCapability(string providerId) : IProviderDownloadCapability
    {
        public string ProviderId { get; } = providerId;
        public ProviderCapabilityKind Capability => ProviderCapabilityKind.Download;

        public Task<ProviderOutcome<ProviderDownloadAvailability>> CheckAvailabilityAsync(
            ProviderExecutionContext context,
            ProviderDownloadAvailabilityRequest request) => throw new NotSupportedException();

        public Task<ProviderOutcome<ProviderDownloadedArtifact>> DownloadAsync(
            ProviderExecutionContext context,
            ProviderDownloadRequest request,
            IProgress<ProviderDownloadProgress>? progress = null) => throw new NotSupportedException();
    }
}

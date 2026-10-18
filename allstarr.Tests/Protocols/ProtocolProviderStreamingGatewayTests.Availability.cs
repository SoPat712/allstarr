using System.Collections.Immutable;
using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using Microsoft.Extensions.Configuration;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolProviderStreamingGatewayTests
{
    [Fact]
    public async Task RecordingAvailability_AbsentStreamingOrderDoesNotResolve()
    {
        var fixture = new AvailabilityFixture("qobuz");
        Assert.Null(await fixture.Open());
        fixture.VerifyLookups(0);
    }

    [Fact]
    public async Task RecordingAvailability_EarlierVerifiedRouteDoesNotResolve()
    {
        var fixture = new AvailabilityFixture("qobuz,pandora", verifiedFirst: true);
        using var response = (await fixture.Open())!.Response;
        fixture.VerifyLookups(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecordingAvailability_PersistsVerifiedMappingAndReusesIt(bool accountBound)
    {
        var fixture = new AvailabilityFixture("pandora", accountBound: accountBound);
        using var first = (await fixture.Open())!.Response;
        using var second = (await fixture.Open())!.Response;
        fixture.VerifyLookups(1);
        var link = Assert.Single(fixture.Links);
        Assert.Equal(fixture.RecordingId, link.CanonicalRecordingId);
        Assert.Equal("target-track", link.ExternalId.Value);
        Assert.Equal(ProviderIdentityVerification.Verified, link.Verification);
        Assert.Equal("availability", link.VerificationMethod);
        Assert.Equal(accountBound ? ProviderIdentityScope.Account : ProviderIdentityScope.Catalog, link.Scope);
    }

    [Theory]
    [InlineData("account")]
    [InlineData("policy")]
    [InlineData("manual")]
    [InlineData("health")]
    [InlineData("resume")]
    public async Task RecordingAvailability_RestrictedRoutesDoNotResolve(string restriction)
    {
        var fixture = new AvailabilityFixture("pandora", restriction: restriction);
        Assert.Null(await fixture.Open(restriction == "resume" ? "bytes=10-19" : null));
        fixture.VerifyLookups(0);
    }

    [Fact]
    public async Task RecordingAvailability_ConflictingLinkDoesNotPlay()
    {
        var fixture = new AvailabilityFixture("pandora", conflict: true);
        Assert.Null(await fixture.Open());
        fixture.VerifyLookups(1);
        Assert.Single(fixture.Links);
    }

    [Fact]
    public async Task RecordingAvailability_ConcurrentRequestsShareLookup()
    {
        var fixture = new AvailabilityFixture("pandora");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Resolve = async context =>
        {
            started.SetResult();
            await release.Task.WaitAsync(context.CancellationToken);
            return ProviderOutcome<ProviderRecordingAvailability>.Success(new(new("pandora", ProviderResourceKind.Track, "target-track")));
        };
        var first = fixture.Open();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = fixture.Open();
        release.SetResult();
        foreach (var stream in await Task.WhenAll(first, second)) stream!.Response.Dispose();
        fixture.VerifyLookups(1);
        Assert.Single(fixture.Links);
    }

    [Fact]
    public async Task RecordingAvailability_MissCacheDoesNotHidePersistedMapping()
    {
        var fixture = new AvailabilityFixture("pandora");
        fixture.Resolve = _ => Task.FromResult(ProviderOutcome<ProviderRecordingAvailability>.Success(new(null)));
        Assert.Null(await fixture.Open());
        Assert.Null(await fixture.Open());
        fixture.VerifyLookups(1);
        fixture.Linked = true;
        using var response = (await fixture.Open())!.Response;
        fixture.VerifyLookups(1);
    }

    [Fact]
    public async Task RecordingAvailability_CallerCancellationStopsLookup()
    {
        var fixture = new AvailabilityFixture("pandora");
        using var cancellation = new CancellationTokenSource();
        fixture.Resolve = async context =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
            return ProviderOutcome<ProviderRecordingAvailability>.Success(new(null));
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Open(cancellation: cancellation.Token));
        Assert.Empty(fixture.Links);
    }

    [Fact]
    public async Task RecordingAvailability_TimeoutPreservesMissAndBoundsDeadline()
    {
        var fixture = new AvailabilityFixture("pandora");
        fixture.Resolve = context =>
        {
            Assert.InRange(context.Deadline - DateTimeOffset.UtcNow, TimeSpan.Zero, TimeSpan.FromSeconds(10));
            Assert.True(context.Policy.AllowManagedDownloads);
            throw new TimeoutException();
        };
        Assert.Null(await fixture.Open());
        fixture.VerifyLookups(1);
        Assert.Empty(fixture.Links);
    }

    [Fact]
    public async Task RecordingAvailability_ExhaustedVerifiedRouteResolvesFallback()
    {
        var fixture = new AvailabilityFixture("qobuz,pandora", verifiedFirst: true,
            firstError: ProviderErrorKind.TransientFailure);
        var stream = await fixture.Open();
        Assert.NotNull(stream);
        Assert.Equal("pandora", stream.ServingProviderId);
        stream.Response.Dispose();
        fixture.VerifyLookups(1);
    }

    [Fact]
    public async Task RecordingAvailability_ForbiddenVerifiedRouteStopsFallback()
    {
        var fixture = new AvailabilityFixture("qobuz,pandora", verifiedFirst: true,
            firstError: ProviderErrorKind.Forbidden);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Open());
        fixture.VerifyLookups(0);
    }

    [Fact]
    public async Task RecordingAvailability_AccountRevisionChangesExpireMiss()
    {
        var fixture = new AvailabilityFixture("pandora", accountBound: true);
        fixture.Resolve = _ => Task.FromResult(ProviderOutcome<ProviderRecordingAvailability>.Success(new(null)));
        Assert.Null(await fixture.Open());
        Assert.Null(await fixture.Open());
        fixture.VerifyLookups(1);
        fixture.AccountRevision++;
        Assert.Null(await fixture.Open());
        fixture.VerifyLookups(2);
    }

    [Fact]
    public async Task RecordingAvailability_ExplicitPolicyDenialDoesNotResolve()
    {
        var fixture = new AvailabilityFixture("pandora", explicitDenied: true);
        Assert.Null(await fixture.Open());
        fixture.VerifyLookups(0);
    }

    [Fact]
    public async Task RecordingAvailability_LookupDeadlineUsesRemainingPlaybackBudget()
    {
        var fixture = new AvailabilityFixture("pandora");
        fixture.Resolve = context =>
        {
            Assert.InRange(context.Deadline - DateTimeOffset.UtcNow, TimeSpan.Zero, TimeSpan.FromSeconds(2));
            return Task.FromResult(ProviderOutcome<ProviderRecordingAvailability>.Success(new(null)));
        };
        Assert.Null(await fixture.Open(remaining: TimeSpan.FromSeconds(2)));
        fixture.VerifyLookups(1);
    }

    [Fact]
    public async Task RecordingAvailability_UserStreamingOrderExcludesRegistryAppend()
    {
        var fixture = new AvailabilityFixture("pandora", effectiveOrder: "qobuz");
        Assert.Null(await fixture.Open());
        fixture.VerifyLookups(0);
    }

    [Fact]
    public async Task RecordingAvailability_UnresponsiveLookupStopsAtTenSeconds()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var fixture = new AvailabilityFixture("pandora", clock: clock);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ProviderOutcome<ProviderRecordingAvailability>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observed = default;
        fixture.Resolve = context =>
        {
            observed = context.CancellationToken;
            started.SetResult();
            return release.Task;
        };
        var opening = fixture.Open();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Null(await opening.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(observed.IsCancellationRequested);
        release.SetResult(ProviderOutcome<ProviderRecordingAvailability>.Success(new(new("pandora", ProviderResourceKind.Track, "late-track"))));
        Assert.Empty(fixture.Links);
        fixture.VerifyLookups(1);
    }

    [Fact]
    public async Task RecordingAvailability_CancelledWaiterDoesNotCancelSharedLookup()
    {
        var fixture = new AvailabilityFixture("pandora");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Resolve = async context =>
        {
            started.SetResult();
            await release.Task.WaitAsync(context.CancellationToken);
            return ProviderOutcome<ProviderRecordingAvailability>.Success(new(new("pandora", ProviderResourceKind.Track, "target-track")));
        };
        var first = fixture.Open();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancelled = new CancellationTokenSource();
        var second = fixture.Open(cancellation: cancelled.Token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        release.SetResult();
        using var response = (await first)!.Response;
        fixture.VerifyLookups(1);
    }

    [Fact]
    public async Task RecordingAvailability_RequestedQualityChangesExpireMiss()
    {
        var fixture = new AvailabilityFixture("pandora");
        fixture.Resolve = _ => Task.FromResult(ProviderOutcome<ProviderRecordingAvailability>.Success(new(null)));
        Assert.Null(await fixture.Open(requestedQuality: ProviderAudioQuality.Any));
        Assert.Null(await fixture.Open(requestedQuality: ProviderAudioQuality.HighResolution));
        fixture.VerifyLookups(2);
    }

    private sealed class AvailabilityFixture
    {
        private readonly ProtocolProviderGateway gateway;
        private readonly ProtocolExecutionContext protocol = Context();
        private readonly Mock<IProviderDownloadStreamingCapability> download = new();
        public Guid RecordingId { get; } = Guid.CreateVersion7();
        public List<TrackIdentityLinkRequest> Links { get; } = [];
        public bool Linked { get; set; }
        public long AccountRevision { get; set; } = 3;
        public Func<ProviderExecutionContext, Task<ProviderOutcome<ProviderRecordingAvailability>>> Resolve { get; set; } =
            _ => Task.FromResult(ProviderOutcome<ProviderRecordingAvailability>.Success(new(new("pandora", ProviderResourceKind.Track, "target-track"))));

        public AvailabilityFixture(string order, bool verifiedFirst = false, bool accountBound = false,
            string? restriction = null, bool conflict = false, ProviderErrorKind? firstError = null, bool explicitDenied = false, string? effectiveOrder = null, TimeProvider? clock = null)
        {
            download.SetupGet(item => item.ProviderId).Returns("pandora");
            download.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Streaming);
            download.SetupGet(item => item.SupportsRecordingAvailability).Returns(true);
            download.Setup(item => item.ResolveRecordingAsync(It.IsAny<ProviderExecutionContext>(),
                    It.IsAny<ProviderRecordingAvailabilityRequest>(), It.IsAny<ProviderAudioQuality>()))
                .Returns((ProviderExecutionContext context, ProviderRecordingAvailabilityRequest request, ProviderAudioQuality _) =>
                {
                    Assert.Equal(RecordingId.ToString("D"), request.SampleId);
                    Assert.Equal("Retained title", request.Title);
                    Assert.Equal("source-track", request.ProviderIds["qobuz"]);
                    return Resolve(context);
                });
            var lease = new ProviderStreamLease("lease", new Uri("https://media.example.test/track"),
                DateTimeOffset.UtcNow.AddMinutes(1), false, false, new("audio/flac", "flac", "flac"),
                ProviderStreamRetryBehavior.DoNotRetry, (_, _) => Task.FromResult(AudioResponse()));
            download.Setup(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()))
                .ReturnsAsync(ProviderOutcome<ProviderStreamLease>.Success(lease));
            var first = Capability("qobuz", firstError is { } error
                ? ProviderOutcome<ProviderStreamLease>.Failure(new(error)) : ProviderOutcome<ProviderStreamLease>.Success(lease));
            var registry = new ProviderRegistry(new IProviderStreamingCapability[] { first.Object, download.Object }.Select(capability =>
                new ProviderRegistration(new ProviderDescriptor(capability.ProviderId, capability.ProviderId, "Test provider",
                    ProviderOrigin.BuiltIn, "1", "1",
                    [new ProviderCapabilityDescriptor(ProviderCapabilityKind.Streaming, ProviderCapabilitySupportState.Supported,
                        capability.ProviderId == "pandora" && (accountBound || restriction == "account")
                            ? ProviderAccountRequirement.Required : ProviderAccountRequirement.None,
                        "1", ["getStreamLease"], capability.ProviderId == "pandora" && (accountBound || restriction == "account")
                            ? [ProviderAccountScope.Personal] : [])], new ProviderPermissionDescriptor()), [capability])));
            var identities = new Mock<ITrackIdentityService>();
            identities.Setup(item => item.ResolveTargetAsync(It.IsAny<ProviderExecutionContext>(), RecordingId,
                    It.IsAny<ProviderTrackIdentityTarget>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((ProviderExecutionContext context, Guid _, ProviderTrackIdentityTarget target, CancellationToken _) =>
                    target.ProviderId == "pandora" && Linked || target.ProviderId == "qobuz" && verifiedFirst
                        ? new(RecordingId, Guid.CreateVersion7(), new(target.ProviderId, ProviderResourceKind.Track, "target-track"),
                            context.Account == null ? ProviderIdentityScope.Catalog : ProviderIdentityScope.Account,
                            context.Account?.AccountId, ProviderIdentityVerification.Verified, "availability", 1) : null);
            identities.Setup(item => item.GetRecordingAvailabilityAsync(It.IsAny<ProviderExecutionContext>(),
                    It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(restriction == "manual" ? null : new ProviderRecordingAvailabilityRequest(RecordingId.ToString("D"), "Retained title", ["Artist"],
                    "USRC17607839", 180000, new Dictionary<string, string> { ["qobuz"] = "source-track" })
                { ExplicitContentLyrics = explicitDenied ? 1 : 0 });
            identities.Setup(item => item.LinkAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<TrackIdentityLinkRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((ProviderExecutionContext _, TrackIdentityLinkRequest request, CancellationToken _) =>
                {
                    Links.Add(request);
                    Linked = !conflict;
                    return new(conflict ? TrackIdentityLinkStatus.Conflict : TrackIdentityLinkStatus.Created, RecordingId,
                        Guid.CreateVersion7(), conflict ? Guid.CreateVersion7() : null);
                });
            var accounts = new Mock<IProviderRouteAccountResolver>();
            if (restriction == "account")
                accounts.Setup(item => item.ResolveAsync(It.IsAny<ProviderRouteAccountRequest>(), It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new UnauthorizedAccessException());
            var health = new Mock<IProviderRouteHealthSource>();
            health.Setup(item => item.Get(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<ProviderCapabilityKind>()))
                .Returns(new ProviderRouteHealthSnapshot(ProviderRouteHealthState.Healthy, false));
            if (accountBound)
            {
                var accountId = Guid.CreateVersion7();
                accounts.Setup(item => item.ResolveAsync(It.IsAny<ProviderRouteAccountRequest>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(() => new ProviderRouteAccountResolution(new(accountId, "pandora", ProviderAccountScope.Personal,
                        AccountRevision, ownerUserId: protocol.RequireActor().EffectiveUserId), AccountRevision));
            }
            if (restriction == "health")
                health.Setup(item => item.Get("pandora", It.IsAny<Guid?>(), ProviderCapabilityKind.Streaming))
                    .Returns(new ProviderRouteHealthSnapshot(ProviderRouteHealthState.Unavailable, false));
            var router = new ProviderRouter(registry, accounts.Object, health.Object, Mock.Of<IProviderRouteSidecarSource>(), identities.Object);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Providers:StreamingOrder"] = order,
                ["Providers:DownloadOrder"] = "pandora",
                ["Providers:MetadataOrder"] = "pandora",
                ["Providers:Disabled"] = restriction == "policy" ? "pandora" : ""
            }).Build();
            if (explicitDenied || effectiveOrder != null)
                protocol = protocol with
                {
                    Policy = new(
                    ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty
                        .Add(ProviderCapabilityKind.Streaming, (effectiveOrder ?? "pandora").Split(',').ToImmutableArray()),
                    ImmutableHashSet<string>.Empty, "max", 0.07)
                    { UserId = protocol.RequireActor().EffectiveUserId, Preferences = new(explicitDenied ? "CleanOnly" : "All") }
                };
            gateway = new(router, registry, new HttpClientFactory(), configuration,
                applicationCache: new TestMemoryApplicationCache(), identities: identities.Object, timeProvider: clock);
        }

        public Task<ProtocolProviderStream?> Open(string? range = null, CancellationToken cancellation = default,
            TimeSpan? remaining = null, ProviderAudioQuality requestedQuality = ProviderAudioQuality.Lossless) =>
            gateway.OpenStreamAsync(new(protocol.Protocol, protocol.BackendInstanceId, protocol.VerifiedBackendPrincipalId,
                protocol.Principal, protocol.CorrelationId, remaining == null ? protocol.Deadline : DateTimeOffset.UtcNow + remaining.Value,
                cancellation, protocol.Client)
            { Policy = protocol.Policy },
                "allstarr", RecordingId.ToString("D"), requestedQuality, range);

        public void VerifyLookups(int count) => download.Verify(item => item.ResolveRecordingAsync(
            It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderRecordingAvailabilityRequest>(), It.IsAny<ProviderAudioQuality>()), Times.Exactly(count));
    }
}

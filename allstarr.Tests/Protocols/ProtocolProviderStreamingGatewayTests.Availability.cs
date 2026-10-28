using System.Collections.Immutable;
using System.Text.Json;
using allstarr.Core.Downloads;
using allstarr.Core.Extensions;
using allstarr.Services.Common;
using Microsoft.Extensions.Logging.Abstractions;
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
    public async Task PlaybackSourceInspection_DoesNotDiscoverOrPersistNewRoutes()
    {
        var fixture = new AvailabilityFixture("pandora");
        Assert.Null(await fixture.Open(headOnly: true));
        fixture.VerifyLookups(0);
        Assert.Empty(fixture.Links);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlaybackSourceInspection_UsesKnownDownloadMediaWithoutPreparingAnArtifact(bool known)
    {
        var fixture = new AvailabilityFixture("pandora") { Linked = true, ProbeMediaKnown = known };
        var opened = await fixture.Open(headOnly: true);
        Assert.Equal(known, opened != null);
        if (opened != null)
        {
            using var response = opened.Response;
            Assert.Equal("flac", opened.Lease.Media.Codec);
        }
        fixture.VerifyInspection();
        Assert.Empty(fixture.Links);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecordingAvailability_EarlierMissDoesNotStarveMonochrome(bool transient)
    {
        var fixture = new AvailabilityFixture("pandora,monochrome", includeMonochrome: true);
        fixture.Resolve = _ => Task.FromResult(transient
            ? ProviderOutcome<ProviderRecordingAvailability>.Failure(new(ProviderErrorKind.TransientFailure))
            : ProviderOutcome<ProviderRecordingAvailability>.Success(new(null)));
        fixture.MonochromeResolve = _ => Task.FromResult(ProviderOutcome<ProviderRecordingAvailability>.Failure(new(ProviderErrorKind.TransientFailure)));
        Assert.Null(await fixture.Open());
        Assert.Equal(1, fixture.MonochromeLookups);
        fixture.MonochromeResolve = _ => Task.FromResult(ProviderOutcome<ProviderRecordingAvailability>.Success(
            new(new("monochrome", ProviderResourceKind.Track, "target-track"))));
        var stream = await fixture.Open();
        Assert.NotNull(stream);
        Assert.Equal("monochrome", stream.ServingProviderId);
        stream.Response.Dispose();
        fixture.VerifyLookups(1);
        Assert.Equal(2, fixture.MonochromeLookups);
    }

    [Fact]
    public async Task RecordingAvailability_EarlierFailureReachesWorkingMonochrome()
    {
        var fixture = new AvailabilityFixture("pandora,monochrome", includeMonochrome: true);
        fixture.Resolve = _ => Task.FromResult(ProviderOutcome<ProviderRecordingAvailability>.Failure(new(ProviderErrorKind.TransientFailure)));
        var stream = await fixture.Open();
        Assert.NotNull(stream);
        Assert.Equal("monochrome", stream.ServingProviderId);
        stream.Response.Dispose();
        fixture.VerifyLookups(1);
        Assert.Equal(1, fixture.MonochromeLookups);
    }

    [Fact]
    public async Task RecordingAvailability_MultipleSourcesShareOneLookupDeadline()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var fixture = new AvailabilityFixture("pandora,monochrome", clock: clock, includeMonochrome: true);
        DateTimeOffset? deadline = null;
        fixture.Resolve = context =>
        {
            deadline = context.Deadline;
            clock.Advance(TimeSpan.FromSeconds(4));
            return Task.FromResult(ProviderOutcome<ProviderRecordingAvailability>.Success(new(null)));
        };
        fixture.MonochromeResolve = context =>
        {
            Assert.Equal(deadline, context.Deadline);
            Assert.Equal(TimeSpan.FromSeconds(6), context.Deadline - clock.GetUtcNow());
            return Task.FromResult(ProviderOutcome<ProviderRecordingAvailability>.Success(new(null)));
        };
        Assert.Null(await fixture.Open());
        Assert.Equal(1, fixture.MonochromeLookups);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecordingAvailability_SearchExtensionResolvesOnlyWithinStreamingOrder(bool inOrder)
    {
        var root = Path.Combine(Path.GetTempPath(), "allstarr-playback-availability", Guid.NewGuid().ToString("N"));
        try
        {
            const string source = """
                {"name":"fixture","version":"1.0.0","type":["metadata_provider","download_provider"],
                 "permissions":{"network":["media.example.test"]}}
                """;
            const string script = """
                var calls = 0;
                registerExtension({customSearch:function(){throw new Error('playback must not search');},
                  getTrack:function(id){return {id:id,name:String(calls),artists:'Artist'};},
                  checkAvailability:function(isrc,title,artist,options){
                    if (options.qobuz_id === 'source-track') calls++;
                    return {available:true,track_id:'target-track'};
                  },download:function(id,quality,path){return file.download('https://media.example.test/audio',path,{});}});
                """;
            var normalized = SpotiFlacExtensionCompatibility.NormalizeManifest(source, script);
            var manifest = ExtensionSdkV1.ParseManifest(normalized);
            var clients = new Mock<IHttpClientFactory>();
            clients.Setup(item => item.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(new AvailabilityDownloadHandler()));
            var sandbox = new ExtensionSandbox(root, normalized, script, clients.Object, NullLogger.Instance,
                new ExtensionRuntimePermissionSet(new HashSet<string>(["https://media.example.test/"]),
                    new HashSet<string>(), new HashSet<string>()), Path.Combine(root, "runtime"));
            var options = new ProviderDownloadWorkspaceOptions { RootPath = Path.Combine(root, "workspaces"), MaximumArtifactBytes = 1024 };
            var store = new Mock<IProviderDownloadArtifactStore>(MockBehavior.Strict);
            store.Setup(item => item.GetWorkspaceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((ProviderDownloadWorkspaceEntity?)null);
            var streaming = new ExtensionDownloadStreamingCapabilityAdapter(sandbox, manifest, null,
                new ProviderDownloadArtifactResolver(store.Object, options, audioVerifier: FixtureAudioVerifier.Instance), options);
            var fixture = new AvailabilityFixture(inOrder ? manifest.Id : "qobuz", implementation: streaming);

            var first = await fixture.Open();
            Assert.Equal(inOrder, first != null);
            if (first != null)
            {
                Assert.Equal(manifest.Id, first.ServingProviderId);
                first.Response.Dispose();
                using var second = (await fixture.Open())!.Response;
                var link = Assert.Single(fixture.Links);
                Assert.Equal("target-track", link.ExternalId.Value);
                Assert.Equal(ProviderIdentityVerification.Verified, link.Verification);
            }
            else Assert.Empty(fixture.Links);
            using var count = JsonDocument.Parse(sandbox.InvokeJson("getTrack", """{"id":"calls"}""")!);
            Assert.Equal(inOrder ? "1" : "0", count.RootElement.GetProperty("title").GetString());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(ProviderErrorKind.RateLimited)]
    [InlineData(ProviderErrorKind.TransientFailure)]
    public async Task MonochromeLookup_DoesNotCacheTemporaryFailureAsMissing(ProviderErrorKind kind)
    {
        var fixture = new AvailabilityFixture("monochrome", providerId: "monochrome");
        fixture.Resolve = _ => Task.FromResult(ProviderOutcome<ProviderRecordingAvailability>.Failure(
            new(kind, kind == ProviderErrorKind.RateLimited ? TimeSpan.FromSeconds(47) : null)));
        Assert.Null(await fixture.Open());
        Assert.Null(await fixture.Open());
        fixture.VerifyLookups(2);
    }

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
    public async Task RecordingAvailability_RestrictedRoutesDoNotResolve(string restriction)
    {
        var fixture = new AvailabilityFixture("pandora", restriction: restriction);
        Assert.Null(await fixture.Open());
        fixture.VerifyLookups(0);
    }

    [Fact]
    public async Task RecordingAvailability_FirstRangeCanResolveAFreshSource()
    {
        var fixture = new AvailabilityFixture("pandora");
        using var response = (await fixture.Open("bytes=10-19"))!.Response;
        fixture.VerifyLookups(1);
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
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Open());
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

    private sealed class AvailabilityDownloadHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = AudioResponse();
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private sealed class AvailabilityFixture
    {
        private readonly ProtocolProviderGateway gateway;
        private readonly ProtocolExecutionContext protocol = Context();
        private readonly Mock<IProviderDownloadStreamingCapability> download = new();
        public Guid RecordingId { get; } = Guid.CreateVersion7();
        public List<TrackIdentityLinkRequest> Links { get; } = [];
        public bool Linked { get; set; }
        public bool ProbeMediaKnown { get; set; } = true;
        public long AccountRevision { get; set; } = 3;
        public int MonochromeLookups { get; private set; }
        public Func<ProviderExecutionContext, Task<ProviderOutcome<ProviderRecordingAvailability>>> MonochromeResolve { get; set; } =
            _ => Task.FromResult(ProviderOutcome<ProviderRecordingAvailability>.Success(new(new("monochrome", ProviderResourceKind.Track, "target-track"))));
        public Func<ProviderExecutionContext, Task<ProviderOutcome<ProviderRecordingAvailability>>> Resolve { get; set; } =
            _ => Task.FromResult(ProviderOutcome<ProviderRecordingAvailability>.Success(new(new("pandora", ProviderResourceKind.Track, "target-track"))));

        public AvailabilityFixture(string order, bool verifiedFirst = false, bool accountBound = false,
            string? restriction = null, bool conflict = false, ProviderErrorKind? firstError = null, bool explicitDenied = false, string? effectiveOrder = null, TimeProvider? clock = null,
            IProviderDownloadStreamingCapability? implementation = null, string providerId = "pandora", bool includeMonochrome = false)
        {
            var provider = implementation?.ProviderId ?? providerId;
            download.SetupGet(item => item.ProviderId).Returns(provider);
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
            download.Setup(item => item.ProbeStreamAsync(It.Is<ProviderExecutionContext>(context => context.IsReadOnly), It.IsAny<ProviderStreamLeaseRequest>()))
                .ReturnsAsync(() => ProviderOutcome<ProviderStreamProbeResult>.Success(new(true, DateTimeOffset.UtcNow,
                    ProbeMediaKnown ? new("audio/flac", "flac", "flac") : null)));
            var first = Capability("qobuz", firstError is { } error
                ? ProviderOutcome<ProviderStreamLease>.Failure(new(error)) : ProviderOutcome<ProviderStreamLease>.Success(lease));
            var streaming = new List<IProviderStreamingCapability> { first.Object, implementation ?? download.Object };
            if (includeMonochrome)
            {
                var monochrome = new Mock<IProviderDownloadStreamingCapability>();
                monochrome.SetupGet(item => item.ProviderId).Returns("monochrome");
                monochrome.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Streaming);
                monochrome.SetupGet(item => item.SupportsRecordingAvailability).Returns(true);
                monochrome.Setup(item => item.ResolveRecordingAsync(It.IsAny<ProviderExecutionContext>(),
                        It.IsAny<ProviderRecordingAvailabilityRequest>(), It.IsAny<ProviderAudioQuality>()))
                    .Returns((ProviderExecutionContext context, ProviderRecordingAvailabilityRequest _, ProviderAudioQuality _) =>
                    {
                        MonochromeLookups++;
                        return MonochromeResolve(context);
                    });
                monochrome.Setup(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()))
                    .ReturnsAsync(ProviderOutcome<ProviderStreamLease>.Success(lease));
                streaming.Add(monochrome.Object);
            }
            var registry = new ProviderRegistry(streaming.Select(capability =>
                new ProviderRegistration(new ProviderDescriptor(capability.ProviderId, capability.ProviderId, "Test provider",
                    ProviderOrigin.BuiltIn, "1", "1",
                    [new ProviderCapabilityDescriptor(ProviderCapabilityKind.Streaming, ProviderCapabilitySupportState.Supported,
                        capability.ProviderId == provider && (accountBound || restriction == "account")
                            ? ProviderAccountRequirement.Required : ProviderAccountRequirement.None,
                        "1", ["getStreamLease"], capability.ProviderId == provider && (accountBound || restriction == "account")
                            ? [ProviderAccountScope.Personal] : [])], new ProviderPermissionDescriptor()), [capability])));
            var identities = new Mock<ITrackIdentityService>();
            identities.Setup(item => item.ResolveTargetAsync(It.IsAny<ProviderExecutionContext>(), RecordingId,
                    It.IsAny<ProviderTrackIdentityTarget>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((ProviderExecutionContext context, Guid _, ProviderTrackIdentityTarget target, CancellationToken _) =>
                    (target.ProviderId == provider && Linked && (!includeMonochrome || Links.Any(x => x.ExternalId.ProviderId == provider))) ||
                    target.ProviderId == "monochrome" && Links.Any(x => x.ExternalId.ProviderId == "monochrome") || target.ProviderId == "qobuz" && verifiedFirst
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
                    .ReturnsAsync(() => new ProviderRouteAccountResolution(new(accountId, provider, ProviderAccountScope.Personal,
                        AccountRevision, ownerUserId: protocol.RequireActor().EffectiveUserId), AccountRevision));
            }
            if (restriction == "health")
                health.Setup(item => item.Get(provider, It.IsAny<Guid?>(), ProviderCapabilityKind.Streaming))
                    .Returns(new ProviderRouteHealthSnapshot(ProviderRouteHealthState.Unavailable, false));
            var router = new ProviderRouter(registry, accounts.Object, health.Object, Mock.Of<IProviderRouteSidecarSource>(), identities.Object);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Providers:StreamingOrder"] = order,
                ["Providers:DownloadOrder"] = provider,
                ["Providers:MetadataOrder"] = provider,
                ["Providers:Disabled"] = restriction == "policy" ? provider : ""
            }).Build();
            if (explicitDenied || effectiveOrder != null)
                protocol = protocol with
                {
                    Policy = new(
                    ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty
                        .Add(ProviderCapabilityKind.Streaming, (effectiveOrder ?? provider).Split(',').ToImmutableArray()),
                    ImmutableHashSet<string>.Empty, "max", 0.07)
                    { UserId = protocol.RequireActor().EffectiveUserId, Preferences = new(explicitDenied ? "CleanOnly" : "All") }
                };
            gateway = new(router, registry, new HttpClientFactory(), configuration,
                applicationCache: new TestMemoryApplicationCache(), identities: identities.Object, timeProvider: clock, audioVerifier: FixtureAudioVerifier.Instance);
        }

        public Task<ProtocolProviderStream?> Open(string? range = null, CancellationToken cancellation = default,
            TimeSpan? remaining = null, ProviderAudioQuality requestedQuality = ProviderAudioQuality.Lossless, bool headOnly = false) =>
            gateway.OpenStreamAsync(new(protocol.Protocol, protocol.BackendInstanceId, protocol.VerifiedBackendPrincipalId,
                protocol.Principal, protocol.CorrelationId, remaining == null ? protocol.Deadline : DateTimeOffset.UtcNow + remaining.Value,
                cancellation, protocol.Client)
            { Policy = protocol.Policy, IsReadOnly = headOnly },
                "allstarr", RecordingId.ToString("D"), requestedQuality, range, headOnly);

        public void VerifyInspection()
        {
            download.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()), Times.Never);
            download.Verify(item => item.ProbeStreamAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()), Times.Once);
        }

        public void VerifyLookups(int count) => download.Verify(item => item.ResolveRecordingAsync(
            It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderRecordingAvailabilityRequest>(), It.IsAny<ProviderAudioQuality>()), Times.Exactly(count));
    }
}

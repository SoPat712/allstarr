using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Operations;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed class ManagedTrackDownloadBindingTests
{
    [Fact]
    public async Task Execute_PersistsTheTranslatedCandidateTrackAndAccountRevision()
    {
        await using var fixture = await ProviderDownloadArtifactBindingTests.BindingFixture.CreateAsync(accountBound: true);
        var capability = new RecordingDownloadCapability(fixture.Resolver);
        var initialTrack = Track(fixture.ProviderId, "command-track");
        var candidateTrack = Track(fixture.ProviderId, "translated-track");
        var account = Account(fixture, revision: 0);
        var plan = Plan(fixture, capability, initialTrack, candidateTrack, account);
        var service = Service(fixture, capability, plan);

        var result = await service.ExecuteAsync(Command(fixture, "recording-keep", initialTrack.Value));

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Artifact);
        Assert.Equal(candidateTrack.Value, result.Artifact!.SourceExternalTrackId);
        Assert.Equal(0L, result.Artifact.SourceAccountRevision);
        Assert.Equal([candidateTrack.Value, candidateTrack.Value], capability.RequestedTrackIds);

        var persisted = await fixture.FreshResolver().FindByJobAsync(fixture.JobId, fixture.ProviderId);
        Assert.NotNull(persisted);
        Assert.Equal(candidateTrack.Value, persisted!.SourceExternalTrackId);
        Assert.Equal(0L, persisted.SourceAccountRevision);
    }

    [Theory]
    [InlineData("account")]
    [InlineData("track")]
    [InlineData("revision")]
    public async Task Execute_RejectsPriorBindingMismatchWithoutDownloading(string mismatch)
    {
        await using var fixture = await ProviderDownloadArtifactBindingTests.BindingFixture.CreateAsync(accountBound: true);
        var (workspace, output) = await fixture.WriteArtifactAsync();
        var originalTrack = Track(fixture.ProviderId, "original-track");
        var prior = await fixture.Resolver.ResolveAsync(
            workspace.Reference,
            output,
            sourceTrack: originalTrack,
            sourceAccountRevision: 0);

        var capability = new RecordingDownloadCapability(fixture.Resolver);
        var candidateTrack = mismatch == "track"
            ? Track(fixture.ProviderId, "different-track")
            : originalTrack;
        var candidateAccount = mismatch == "account"
            ? new ProviderAccountContext(
                Guid.CreateVersion7(),
                fixture.ProviderId,
                ProviderAccountScope.Personal,
                0,
                ownerUserId: fixture.UserId)
            : Account(fixture, mismatch == "revision" ? 1 : 0);
        var plan = Plan(fixture, capability, originalTrack, candidateTrack, candidateAccount);
        var service = Service(fixture, capability, plan);

        var result = await service.ExecuteAsync(Command(fixture, "recording-keep", originalTrack.Value));

        Assert.False(result.Succeeded);
        Assert.Equal(mismatch == "revision" ? "managed_download_account_changed" : "managed_download_artifact_invalid", result.ErrorCode);
        Assert.Equal(mismatch == "revision", result.Retryable);
        Assert.Equal(0, capability.CheckAvailabilityCalls);
        Assert.Equal(0, capability.DownloadCalls);
        var persisted = await fixture.FreshResolver().FindByJobAsync(fixture.JobId, fixture.ProviderId);
        Assert.NotNull(persisted);
        Assert.Equal(prior.Id, persisted!.Id);
        Assert.Equal(prior.ContentSha256, persisted.ContentSha256);
        Assert.Equal(prior.Length, persisted.Length);
        Assert.Equal(originalTrack.Value, persisted.SourceExternalTrackId);
        Assert.Equal(0L, persisted.SourceAccountRevision);
    }

    [Fact]
    public async Task Execute_DeniesLegacyUnboundArtifactForKeptRouteButReusesItForFavoriteRoute()
    {
        await using var fixture = await ProviderDownloadArtifactBindingTests.BindingFixture.CreateAsync(accountBound: false);
        var (workspace, output) = await fixture.WriteArtifactAsync();
        var legacy = await fixture.Resolver.ResolveAsync(workspace.Reference, output);
        var capability = new RecordingDownloadCapability(fixture.Resolver);
        var track = Track(fixture.ProviderId, "legacy-track");
        var plan = Plan(fixture, capability, track, track, account: null);
        var service = Service(fixture, capability, plan);

        var denied = await service.ExecuteAsync(Command(fixture, "recording-keep", track.Value));
        var reused = await service.ExecuteAsync(Command(fixture, "favorite-download", track.Value));

        Assert.False(denied.Succeeded);
        Assert.Equal("managed_download_artifact_invalid", denied.ErrorCode);
        Assert.True(reused.Succeeded);
        Assert.NotNull(reused.Artifact);
        Assert.Equal(legacy.Id, reused.Artifact!.Id);
        Assert.Equal(0, capability.CheckAvailabilityCalls);
        Assert.Equal(0, capability.DownloadCalls);
    }

    private static ManagedTrackDownloadCommand Command(
        ProviderDownloadArtifactBindingTests.BindingFixture fixture,
        string routePurpose,
        string externalTrackId) =>
        new(
            fixture.UserId,
            fixture.JobId,
            fixture.ProviderId,
            externalTrackId,
            $"binding-correlation-{routePurpose}",
            $"binding-idempotency-{routePurpose}",
            0,
            routePurpose);

    private static ProviderAccountContext Account(
        ProviderDownloadArtifactBindingTests.BindingFixture fixture,
        long revision) =>
        new(
            fixture.AccountId!.Value,
            fixture.ProviderId,
            ProviderAccountScope.Personal,
            revision,
            ownerUserId: fixture.UserId);

    private static ProviderRoutePlan<IProviderDownloadCapability> Plan(
        ProviderDownloadArtifactBindingTests.BindingFixture fixture,
        RecordingDownloadCapability capability,
        ProviderExternalResourceId initialTrack,
        ProviderExternalResourceId candidateTrack,
        ProviderAccountContext? account)
    {
        var clock = TestClock.Instance;
        var actor = new ProviderActorContext(
            ProviderActorKind.SystemJob,
            null,
            durableJobId: fixture.JobId,
            actingForUserId: fixture.UserId);
        var policy = Policy();
        var request = new ProviderRouteRequest(
            ProviderCapabilityKind.Download,
            actor,
            policy,
            "managed-download-binding",
            "binding-plan-correlation",
            clock.UtcNow.AddHours(1),
            [fixture.ProviderId],
            [new ProviderRouteProviderState(
                fixture.ProviderId,
                availableQualities: Enum.GetValues<ProviderAudioQuality>())],
            initialTrack,
            "binding-plan-idempotency");
        var descriptor = Descriptor(fixture.ProviderId, account != null);
        var context = new ProviderExecutionContext(
            actor,
            fixture.ProviderId,
            account,
            policy,
            "managed-download-binding",
            "binding-plan-correlation",
            clock.UtcNow.AddHours(1),
            CancellationToken.None,
            "binding-plan-idempotency");
        var candidate = new ProviderRouteCandidate<IProviderDownloadCapability>(
            0,
            descriptor,
            descriptor.Capabilities.Single(item => item.Capability == ProviderCapabilityKind.Download),
            capability,
            context,
            candidateTrack);
        return new(
            request,
            [candidate],
            new ProviderRouteDecisionRecord(
                request.CorrelationId,
                request.Capability,
                fixture.ProviderId,
                account?.AccountId,
                [new ProviderRouteCandidateDecision(
                    fixture.ProviderId,
                    account?.AccountId,
                    ProviderRouteDecisionStatus.Accepted,
                    "selected",
                    0)]));
    }

    private static ManagedTrackDownloadService Service(
        ProviderDownloadArtifactBindingTests.BindingFixture fixture,
        RecordingDownloadCapability capability,
        ProviderRoutePlan<IProviderDownloadCapability> plan)
    {
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(item => item.PlanAsync<IProviderDownloadCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync(plan);
        var providers = new Mock<IProviderRegistry>(MockBehavior.Strict);
        providers.Setup(item => item.FindByCapability(ProviderCapabilityKind.Download, true))
            .Returns([Descriptor(fixture.ProviderId, plan.Candidates[0].Context.Account != null)]);
        return new ManagedTrackDownloadService(
            router.Object,
            providers.Object,
            fixture.Resolver,
            TestClock.Instance,
            NullLogger<ManagedTrackDownloadService>.Instance);
    }

    private static ProviderExecutionPolicy Policy() => new(
        new ProviderQualityPolicy(
            ProviderAudioQuality.Any,
            ProviderAudioQuality.HighResolution,
            allowTranscode: false),
        ProviderExplicitContentPolicy.Allow,
        allowFallback: true,
        allowSharedAccount: false,
        allowManagedDownloads: true);

    private static ProviderDescriptor Descriptor(string providerId, bool accountBound) => new(
        providerId,
        "Binding test provider",
        "Provider used to verify managed download source binding.",
        ProviderOrigin.BuiltIn,
        "1",
        "1.0",
        [new ProviderCapabilityDescriptor(
            ProviderCapabilityKind.Download,
            ProviderCapabilitySupportState.Supported,
            accountBound ? ProviderAccountRequirement.Optional : ProviderAccountRequirement.None,
            "1.0",
            ["checkAvailability", "download"],
            accountBound ? [ProviderAccountScope.Personal] : null)],
        new ProviderPermissionDescriptor());

    private static ProviderExternalResourceId Track(string provider, string value) =>
        new(provider, ProviderResourceKind.Track, value);

    private sealed class RecordingDownloadCapability(ProviderDownloadArtifactResolver resolver)
        : IProviderDownloadCapability
    {
        private static readonly byte[] Content = Encoding.UTF8.GetBytes("managed download binding fixture");

        public string ProviderId => "spotify";

        public ProviderCapabilityKind Capability => ProviderCapabilityKind.Download;

        public int CheckAvailabilityCalls { get; private set; }

        public int DownloadCalls { get; private set; }

        public List<string> RequestedTrackIds { get; } = [];

        public Task<ProviderOutcome<ProviderDownloadAvailability>> CheckAvailabilityAsync(
            ProviderExecutionContext context,
            ProviderDownloadAvailabilityRequest request)
        {
            CheckAvailabilityCalls++;
            RequestedTrackIds.Add(request.TrackId.Value);
            return Task.FromResult(ProviderOutcome<ProviderDownloadAvailability>.Success(
                new ProviderDownloadAvailability(ProviderDownloadAvailabilityState.Available)));
        }

        public async Task<ProviderOutcome<ProviderDownloadedArtifact>> DownloadAsync(
            ProviderExecutionContext context,
            ProviderDownloadRequest request,
            IProgress<ProviderDownloadProgress>? progress = null)
        {
            DownloadCalls++;
            RequestedTrackIds.Add(request.TrackId.Value);
            await using var content = new MemoryStream(Content, writable: false);
            var write = await resolver.WriteAsync(new ProviderDownloadArtifactWriteRequest(
                request.Workspace,
                request.DurableJobId,
                ProviderId,
                "track.flac",
                content,
                1024),
                context.CancellationToken);
            return ProviderOutcome<ProviderDownloadedArtifact>.Success(new ProviderDownloadedArtifact(
                write.ArtifactId,
                write.Sha256,
                write.SizeBytes,
                new ProviderMediaFormat(
                    "audio/flac",
                    "flac",
                    "flac",
                    1_411_000,
                    44_100,
                    16,
                    2),
                verified: true));
        }
    }

    private sealed class TestClock : IPlatformClock
    {
        public static TestClock Instance { get; } = new();

        public DateTimeOffset UtcNow { get; } = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    }
}

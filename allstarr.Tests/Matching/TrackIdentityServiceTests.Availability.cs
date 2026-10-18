using System.Net;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using Microsoft.Extensions.Configuration;
using Moq;
using allstarr.Core.Capabilities;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class TrackIdentityServiceTests
{
    [Fact]
    public async Task RecordingAvailabilityEvidence_ExcludesSuggestionsAndPrivateSnapshots()
    {
        var actor = Actor(_userA);
        var recording = (await _service.CreateRecordingAsync(actor, "availability-evidence", "USRC17607839")).Recording;
        await Link(recording.Id, Context(actor, "qobuz"), "verified-track");
        var account = await SeedUserAccount("deezer", _userB);
        await Link(recording.Id, Context(Actor(_userB), "deezer", account), "private-track", ProviderIdentityScope.Account);
        await _service.LinkAsync(Context(actor, "spotify"), new(recording.Id, Track("spotify", "suggested-track"),
            ProviderIdentityScope.Catalog, ProviderIdentityVerification.Verified, "automatic-suggestion", 1));
        await SeedAvailabilitySnapshot("private-track", "Private title");
        await SeedAvailabilitySnapshot("suggested-track", "Suggested title");
        await SeedAvailabilitySnapshot("verified-track");
        var evidence = await _service.GetRecordingAvailabilityAsync(Context(actor, "pandora"), recording.PublicId);
        Assert.NotNull(evidence);
        Assert.Equal(recording.Id.ToString("D"), evidence.SampleId);
        Assert.Equal("Retained title", evidence.Title);
        Assert.Equal(["Retained artist"], evidence.ArtistNames);
        Assert.Equal(180000, evidence.DurationMilliseconds);
        Assert.Equal("USRC17607839", evidence.Isrc);
        Assert.Equal("verified-track", Assert.Single(evidence.ProviderIds).Value);
    }

    [Theory]
    [InlineData(ProviderIdentityVerification.Pinned, "manual-review")]
    [InlineData(ProviderIdentityVerification.Verified, "manual-released")]
    [InlineData(ProviderIdentityVerification.Verified, "manual-replaced")]
    public async Task RecordingAvailabilityEvidence_ManualAssociationsPreventResolution(
        ProviderIdentityVerification verification, string method)
    {
        var actor = Actor(_userA);
        var recording = (await _service.CreateRecordingAsync(actor, "manual-evidence")).Recording;
        await Link(recording.Id, Context(actor, "spotify"), "verified-track");
        await SeedAvailabilitySnapshot("verified-track");
        Assert.NotNull(await _service.GetRecordingAvailabilityAsync(Context(actor, "pandora"), recording.PublicId));
        await _service.LinkAsync(Context(actor, "qobuz"), new(recording.Id, Track("qobuz", "manual-track"),
            ProviderIdentityScope.Catalog, verification, method, 1));
        Assert.Null(await _service.GetRecordingAvailabilityAsync(Context(actor, "pandora"), recording.PublicId));
    }

    [Fact]
    public async Task RecordingAvailabilityLink_ConflictingAndRejectedAssociationsRetainAuthority()
    {
        var actor = Actor(_userA);
        var first = (await _service.CreateRecordingAsync(actor, "first-recording")).Recording;
        var second = (await _service.CreateRecordingAsync(actor, "second-recording")).Recording;
        var execution = Context(actor, "pandora");
        await _service.LinkAsync(execution, new(first.Id, Track("pandora", "target-track"),
            ProviderIdentityScope.Catalog, ProviderIdentityVerification.Verified, "manual-released", 4));
        var result = await _service.LinkAsync(execution, new(second.Id, Track("pandora", "target-track"),
            ProviderIdentityScope.Catalog, ProviderIdentityVerification.Verified, "availability", 1));
        Assert.Equal(allstarr.Core.Matching.TrackIdentityLinkStatus.Conflict, result.Status);
        var retained = await _service.ResolveAsync(execution, Track("pandora", "target-track"));
        Assert.Equal(first.Id, retained!.CanonicalRecordingId);
        Assert.Equal("manual-released", retained.VerificationMethod);
        Assert.Equal(4, retained.DecisionVersion);
    }
    [Fact]
    public async Task RecordingAvailabilityLink_AccountMappingStaysPrivate()
    {
        var actor = Actor(_userA);
        var recording = (await _service.CreateRecordingAsync(actor, "private-availability")).Recording;
        var account = await SeedUserAccount("pandora", _userA);
        var other = await SeedUserAccount("pandora", _userB);
        await _service.LinkAsync(Context(actor, "pandora", account), new(recording.Id, Track("pandora", "private-target"),
            ProviderIdentityScope.Account, ProviderIdentityVerification.Verified, "availability", 1));
        Assert.NotNull(await _service.ResolveTargetAsync(Context(actor, "pandora", account), recording.Id, new("pandora")));
        Assert.Null(await _service.ResolveTargetAsync(Context(Actor(_userB), "pandora", other), recording.Id, new("pandora")));
        Assert.Null(await _service.ResolveTargetAsync(Context(actor, "pandora"), recording.Id, new("pandora")));
    }

    [Theory]
    [InlineData(ManualOverrideDecision.Pin)]
    [InlineData(ManualOverrideDecision.Reject)]
    public async Task RecordingAvailabilityEvidence_ActiveManualOverridePreventsResolution(ManualOverrideDecision decision)
    {
        var actor = Actor(_userA);
        var recording = (await _service.CreateRecordingAsync(actor, "manual-override-evidence")).Recording;
        await Link(recording.Id, Context(actor, "qobuz"), "verified-track");
        var snapshot = await SeedAvailabilitySnapshot("verified-track");
        Assert.NotNull(await _service.GetRecordingAvailabilityAsync(Context(actor, "pandora"), recording.PublicId));
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.ManualTrackOverrides.Add(new()
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = _userA,
                ExternalSnapshotId = snapshot.Id,
                SourceProviderId = "qobuz",
                SourceExternalIdHash = snapshot.ExternalIdHash,
                Decision = decision,
                DecisionVersion = 1,
                CreatedAt = _clock.UtcNow,
                TargetProviderId = decision == ManualOverrideDecision.Pin ? "qobuz" : null,
                TargetExternalId = decision == ManualOverrideDecision.Pin ? "manual-target" : null
            });
            await db.SaveChangesAsync();
        }
        Assert.Null(await _service.GetRecordingAvailabilityAsync(Context(actor, "pandora"), recording.PublicId));
    }

    [Fact]
    public async Task RecordingAvailabilityLink_ManualPinPreventsCompetingAssociation()
    {
        var actor = Actor(_userA);
        var recording = (await _service.CreateRecordingAsync(actor, "pinned-availability")).Recording;
        var execution = Context(actor, "pandora");
        await _service.LinkAsync(execution, new(recording.Id, Track("pandora", "pinned-target"),
            ProviderIdentityScope.Catalog, ProviderIdentityVerification.Pinned, "manual-review", 2));
        var result = await _service.LinkAsync(execution, new(recording.Id, Track("pandora", "new-target"),
            ProviderIdentityScope.Catalog, ProviderIdentityVerification.Verified, "availability", 1));
        Assert.Equal(allstarr.Core.Matching.TrackIdentityLinkStatus.Conflict, result.Status);
        var target = await _service.ResolveTargetAsync(execution, recording.Id, new("pandora"));
        Assert.Equal("pinned-target", target!.ExternalId.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecordingAvailabilityPlayback_ResolvesRetainedRecordingAndPersistsOnce(bool legacySource)
    {
        var actor = Actor(_userA);
        var recording = (await _service.CreateRecordingAsync(actor, "retained-playback", "USRC17607839")).Recording;
        await Link(recording.Id, Context(actor, "qobuz"), "verified-track");
        await SeedAvailabilitySnapshot("verified-track");
        var capability = new Mock<IProviderDownloadStreamingCapability>();
        capability.SetupGet(item => item.ProviderId).Returns("pandora");
        capability.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Streaming);
        capability.SetupGet(item => item.SupportsRecordingAvailability).Returns(true);
        capability.Setup(item => item.ResolveRecordingAsync(It.IsAny<ProviderExecutionContext>(),
                It.IsAny<ProviderRecordingAvailabilityRequest>(), ProviderAudioQuality.Lossless))
            .ReturnsAsync((ProviderExecutionContext _, ProviderRecordingAvailabilityRequest request, ProviderAudioQuality _) =>
            {
                Assert.Equal(recording.Id.ToString("D"), request.SampleId);
                Assert.Equal("Retained title", request.Title);
                Assert.Equal("verified-track", request.ProviderIds["qobuz"]);
                return ProviderOutcome<ProviderRecordingAvailability>.Success(new(Track("pandora", "resolved-track")));
            });
        var lease = new ProviderStreamLease("lease", new Uri("https://media.example.test/track"),
            DateTimeOffset.UtcNow.AddMinutes(1), false, false, new("audio/flac", "flac", "flac"),
            ProviderStreamRetryBehavior.DoNotRetry, (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent([1, 2, 3, 4]) }));
        capability.Setup(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(),
                It.Is<ProviderStreamLeaseRequest>(request => request.TrackId.Value == "resolved-track")))
            .ReturnsAsync(ProviderOutcome<ProviderStreamLease>.Success(lease));
        var registry = new ProviderRegistry([new ProviderRegistration(new ProviderDescriptor("pandora", "Pandora", "Test provider",
            ProviderOrigin.BuiltIn, "1", "1", [new ProviderCapabilityDescriptor(ProviderCapabilityKind.Streaming,
                ProviderCapabilitySupportState.Supported, ProviderAccountRequirement.None, "1", ["getStreamLease"])],
            new ProviderPermissionDescriptor()), [capability.Object])]);
        var health = new Mock<IProviderRouteHealthSource>();
        health.Setup(item => item.Get(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<ProviderCapabilityKind>()))
            .Returns(new ProviderRouteHealthSnapshot(ProviderRouteHealthState.Healthy, false));
        var router = new ProviderRouter(registry, Mock.Of<IProviderRouteAccountResolver>(), health.Object,
            Mock.Of<IProviderRouteSidecarSource>(), _service);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Providers:StreamingOrder"] = "pandora" }).Build();
        var gateway = new ProtocolProviderGateway(router, registry, Mock.Of<IHttpClientFactory>(MockBehavior.Strict),
            configuration, identities: _service);
        var protocol = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "fixture", _userA.ToString("N"),
            new AllstarrPrincipal(_userA, "jellyfin", "fixture", _userA.ToString("N"), "User", false), "retained-playback",
            DateTimeOffset.UtcNow.AddMinutes(1), default, new("client", "device"));
        for (var play = 0; play < 2; play++)
        {
            var opened = await gateway.OpenStreamAsync(protocol, legacySource ? "qobuz" : "allstarr",
                legacySource ? "verified-track" : recording.Id.ToString("D"), ProviderAudioQuality.Lossless, null);
            Assert.NotNull(opened);
            Assert.Equal("pandora", opened.ServingProviderId);
            opened.Response.Dispose();
        }
        capability.Verify(item => item.ResolveRecordingAsync(It.IsAny<ProviderExecutionContext>(),
            It.IsAny<ProviderRecordingAvailabilityRequest>(), It.IsAny<ProviderAudioQuality>()), Times.Once);
        await using var db = await _factory.CreateDbContextAsync();
        var persisted = await db.ProviderTrackIdentities.SingleAsync(item => item.ProviderId == "pandora");
        Assert.Equal(recording.Id, persisted.CanonicalRecordingId);
        Assert.Equal("availability", persisted.VerificationMethod);
        Assert.Equal(1, persisted.DecisionVersion);
    }

    private async Task<ExternalMetadataSnapshotRecord> SeedAvailabilitySnapshot(string externalId, string title = "Retained title")
    {
        await using var db = await _factory.CreateDbContextAsync();
        var identity = await db.ProviderTrackIdentities.SingleAsync(item => item.ExternalId == externalId);
        var payload = System.Text.Json.JsonSerializer.Serialize(new { Title = title, Artist = "Retained artist", Duration = 180 });
        var snapshot = new ExternalMetadataSnapshotRecord
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = _userA,
            ProviderId = identity.ProviderId,
            ProviderTrackIdentityId = identity.Id,
            ProviderAccountId = identity.ProviderAccountId,
            ExternalIdHash = identity.ExternalIdHash,
            SnapshotVersion = 1,
            ResourceKind = "track",
            ProviderRevision = "1",
            PayloadJson = payload,
            PayloadSha256 = Hash(payload),
            RetrievedAt = _clock.UtcNow
        };
        db.ExternalMetadataSnapshots.Add(snapshot);
        await db.SaveChangesAsync();
        return snapshot;
    }

}

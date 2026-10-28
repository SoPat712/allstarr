using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Playlists;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed partial class RecordingKeptFilesTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlaylistWorkerKeepsHouseholdBytesAndReusesAnExistingCopyWithoutProviderCalls(bool existing)
    {
        var payload = await SeedPlaylistInterestAsync();
        if (existing) await PlaylistService().PublishAsync(PlaylistRequest(payload));
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        var providers = new Mock<IProviderRegistry>(MockBehavior.Strict);
        string? purpose = null;
        if (!existing)
        {
            var descriptor = ExecutorDescriptor();
            providers.Setup(item => item.FindByCapability(ProviderCapabilityKind.Download, true)).Returns([descriptor]);
            router.Setup(item => item.PlanAsync<IProviderDownloadCapability>(It.IsAny<ProviderRouteRequest>()))
                .Callback<ProviderRouteRequest>(request => purpose = request.OperationId)
                .ReturnsAsync(ExecutorPlan(descriptor));
        }
        var downloads = new ManagedTrackDownloadService(router.Object, providers.Object, artifacts, new SystemPlatformClock(),
            NullLogger<ManagedTrackDownloadService>.Instance);
        var handler = new PlaylistTrackRetentionJobHandler(factory, downloads, PlaylistService());
        var claim = new DurableJobClaim(jobId, 1, PlaylistTrackRetentionJobHandler.JobTypeName,
            JsonSerializer.SerializeToElement(payload), owner, null, null, JsonSerializer.SerializeToElement(new { }), "playlist-worker");
        var result = await handler.ExecuteAsync(new(claim, services), default);
        Assert.Equal(DurableJobCompletionKind.Succeeded, result.Kind);
        if (!existing) Assert.Equal("recording-keep", purpose);
        else
        {
            router.VerifyNoOtherCalls();
            providers.VerifyNoOtherCalls();
        }
        Assert.NotNull(await PlaylistService().FindAsync(Actor(other), recordingId));
        Assert.Equal("Fixture", (await PlaylistService().FindSongAsync(Actor(other), recordingId.ToString("D")))!.Title);
        await using var db = new AllstarrDbContext(database.Options);
        Assert.Null((await db.ManagedFiles.SingleAsync(item => item.RemovedAt == null)).OwnerUserId);
        Assert.Equal(ProviderDownloadArtifactState.Verified, (await db.Set<ProviderDownloadArtifactEntity>().SingleAsync()).State);
        Assert.Equal(PlaylistRequest(payload).ReferenceKey, Assert.Single(await db.ManagedFileReferences
            .Where(item => item.ReleasedAt == null).ToArrayAsync()).ReferenceKey);
        Assert.Equal(audio, await File.ReadAllBytesAsync(artifact.SourcePath));
    }

    [Fact]
    public async Task PlaylistPublicationUsesTheCurrentPublishedRouteAndStableHouseholdInterest()
    {
        var payload = await SeedPlaylistInterestAsync();
        var request = PlaylistRequest(payload);
        var first = await PlaylistService().PublishAsync(request);
        var repeated = await PlaylistService().PublishAsync(request);
        Assert.Equal(first.ManagedFileId, repeated.ManagedFileId);
        Assert.NotNull(await PlaylistService().FindAsync(Actor(other), recordingId));
        await using var db = new AllstarrDbContext(database.Options);
        var reference = Assert.Single(await db.ManagedFileReferences.Where(item => item.ReleasedAt == null).ToArrayAsync());
        Assert.Equal(request.ReferenceKey, reference.ReferenceKey);
        Assert.Null(reference.OwnerUserId);
        Assert.Equal(audio, await File.ReadAllBytesAsync(artifact.SourcePath));
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("new-generation")]
    [InlineData("unpublished")]
    [InlineData("removed-entry")]
    [InlineData("manual-reject")]
    [InlineData("manual-provider")]
    [InlineData("foreign-principal")]
    [InlineData("reference-key")]
    public async Task PlaylistPublicationRejectsObsoleteOrForeignInterest(string change)
    {
        var payload = await SeedPlaylistInterestAsync();
        var request = PlaylistRequest(payload);
        if (change == "reference-key") request = request with { ReferenceKey = "playlist:another-link" };
        else await ChangePlaylistAsync(payload, change);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => PlaylistService().PublishAsync(request));
        await using var db = new AllstarrDbContext(database.Options);
        Assert.Empty(await db.Set<RecordingKeptRouteEntity>().ToArrayAsync());
        Assert.Empty(await db.ManagedFileReferences.Where(item => item.ReleasedAt == null).ToArrayAsync());
        Assert.Equal(audio, await File.ReadAllBytesAsync(artifact.SourcePath));
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("new-generation")]
    [InlineData("manual-reject")]
    public async Task PlaylistPublicationRechecksIntentAfterPreparingTheFile(string change)
    {
        var payload = await SeedPlaylistInterestAsync();
        placementBoundary = () => ChangePlaylistAsync(payload, change).GetAwaiter().GetResult();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => PlaylistService().PublishAsync(PlaylistRequest(payload)));
        Assert.Null(placementBoundary);
        await using var db = new AllstarrDbContext(database.Options);
        Assert.Empty(await db.Set<RecordingKeptRouteEntity>().ToArrayAsync());
        Assert.DoesNotContain(await db.ManagedFileReferences.Where(item => item.ReleasedAt == null).ToArrayAsync(),
            item => item.ReferenceKey.StartsWith("playlist:", StringComparison.Ordinal));
        Assert.Equal(audio, await File.ReadAllBytesAsync(artifact.SourcePath));
    }

    [Fact]
    public async Task UnrelatedJobCannotPublishAReservedPlaylistInterest()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service().PublishAsync(Request() with
        { ReferenceKey = $"playlist:{Guid.CreateVersion7():N}:{new string('a', 64)}" }));
    }

    private RecordingKeptFiles PlaylistService() => new(factory, artifacts,
        services.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
        placement, identities, new allstarr.Core.Operations.SystemPlatformClock(),
        libraryAccess: new TestBackendLibraryAccess(factory, "music"), audioVerifier: FixtureAudioVerifier.Instance);

    private KeptRecordingPublication PlaylistRequest(PlaylistTrackRetentionJobPayload payload) => Request() with
    { ReferenceKey = $"playlist:{payload.PlaylistLinkId:N}:{payload.RetentionKey}" };

    private async Task<PlaylistTrackRetentionJobPayload> SeedPlaylistInterestAsync(bool privateSource = false)
    {
        await using var db = new AllstarrDbContext(database.Options);
        var now = DateTimeOffset.UtcNow;
        var sourceAccount = account;
        var sourceProvider = "deezer";
        if (privateSource)
        {
            sourceAccount = Guid.CreateVersion7();
            sourceProvider = "spotify";
            db.ProviderAccounts.Add(new()
            {
                Id = sourceAccount,
                OwnerUserId = owner,
                ProviderId = sourceProvider,
                DisplayName = "Import account",
                Enabled = true,
                Revision = 3,
                CreatedAt = now,
                UpdatedAt = now
            });
            await db.SaveChangesAsync();
            await identities.LinkAsync(new ProviderExecutionContext(Actor(owner), sourceProvider,
                new(sourceAccount, sourceProvider, ProviderAccountScope.Personal, 3, ownerUserId: owner), SourceContext().Policy,
                "playlist-source", "playlist-source", now.AddMinutes(1), default),
                new(recordingId, new(sourceProvider, ProviderResourceKind.Track, "source-track"), ProviderIdentityScope.Account,
                    ProviderIdentityVerification.Verified, "fixture-exact", 1));
        }
        var identity = await db.ProviderTrackIdentities.SingleAsync(item => item.ProviderId == sourceProvider);
        var link = new PlaylistLinkRecord
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = owner,
            ProviderAccountId = sourceAccount,
            SourceProviderId = sourceProvider,
            SourcePlaylistId = "playlist",
            SourcePlaylistIdHash = PlaylistHash("playlist"),
            TargetProtocol = "jellyfin",
            TargetBackendInstanceId = "fixture",
            TrackRetention = PlaylistTrackRetention.KeepAll,
            RuleVersion = "fixture",
            PolicyVersion = "fixture",
            CreatedAt = now,
            UpdatedAt = now,
            Enabled = true
        };
        var snapshot = new PlaylistSourceSnapshotRecord
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = owner,
            PlaylistLinkId = link.Id,
            ProviderAccountId = sourceAccount,
            SnapshotVersion = 1,
            ProviderRevision = "first",
            Name = "Playlist",
            PayloadSha256 = PlaylistHash("snapshot"),
            CorrelationId = "playlist",
            RetrievedAt = now,
            PublishedAt = now
        };
        var external = new ExternalMetadataSnapshotRecord
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = owner,
            ProviderAccountId = sourceAccount,
            ProviderTrackIdentityId = identity.Id,
            BackendInstanceId = "fixture",
            BackendPrincipalId = owner.ToString("N"),
            Protocol = "jellyfin",
            ProviderId = sourceProvider,
            ResourceKind = "track",
            ExternalIdHash = identity.ExternalIdHash,
            SnapshotVersion = 1,
            ProviderRevision = "first",
            PayloadJson = "{\"Title\":\"Fixture\",\"Artists\":[\"Artist\"]}",
            PayloadSha256 = PlaylistHash("metadata"),
            CorrelationId = "playlist",
            RetrievedAt = now
        };
        var match = new TrackMatchRecord
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = owner,
            ExternalSnapshotId = external.Id,
            CanonicalRecordingId = recordingId,
            State = TrackMatchState.Accepted,
            Confidence = 1,
            Threshold = 0.9,
            DecisionVersion = 1,
            SourceSnapshotVersion = 1,
            PolicyVersion = "fixture",
            CorrelationId = "playlist",
            DecidedAt = now
        };
        var entry = new PlaylistSourceEntryRecord
        {
            Id = Guid.CreateVersion7(),
            PlaylistSourceSnapshotId = snapshot.Id,
            ExternalMetadataSnapshotId = external.Id,
            PublishedTrackMatchId = match.Id,
            SourcePosition = 0,
            SourceEntryIdHash = PlaylistHash("entry")
        };
        var key = PlaylistHash($"{link.Id:N}\u001f{sourceProvider}\u001fsource-track");
        var payload = new PlaylistTrackRetentionJobPayload(link.Id, snapshot.Id, entry.Id,
            sourceProvider, "source-track", key, "Fixture", ["Artist"], null);
        var job = await db.Jobs.SingleAsync(item => item.Id == jobId);
        job.Type = PlaylistTrackRetentionJobHandler.JobTypeName;
        job.PayloadJson = JsonSerializer.Serialize(payload);
        job.Revision++;
        db.AddRange(link, snapshot, external, match, entry);
        await db.SaveChangesAsync();
        return payload;
    }

    private async Task ChangePlaylistAsync(PlaylistTrackRetentionJobPayload payload, string change)
    {
        await using var db = new AllstarrDbContext(database.Options);
        var link = await db.PlaylistLinks.SingleAsync(item => item.Id == payload.PlaylistLinkId);
        var snapshot = await db.PlaylistSourceSnapshots.SingleAsync(item => item.Id == payload.SourceSnapshotId);
        if (change == "disabled") link.TrackRetention = PlaylistTrackRetention.OnDemand;
        else if (change == "unpublished") snapshot.PublishedAt = null;
        else if (change == "removed-entry")
            await db.PlaylistSourceEntries.Where(item => item.Id == payload.SourceEntryId).ExecuteDeleteAsync();
        else if (change == "foreign-principal")
            await db.ExternalMetadataSnapshots.ExecuteUpdateAsync(update => update.SetProperty(item => item.BackendPrincipalId, other.ToString("N")));
        else if (change == "new-generation") db.PlaylistSourceSnapshots.Add(new()
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = owner,
            PlaylistLinkId = link.Id,
            ProviderAccountId = account,
            SnapshotVersion = 2,
            ProviderRevision = "second",
            Name = "Playlist",
            PayloadSha256 = PlaylistHash("second"),
            CorrelationId = "playlist",
            RetrievedAt = DateTimeOffset.UtcNow,
            PublishedAt = DateTimeOffset.UtcNow
        });
        else if (change.StartsWith("manual-", StringComparison.Ordinal))
        {
            var external = await db.ExternalMetadataSnapshots.SingleAsync();
            db.ManualTrackOverrides.Add(new()
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = owner,
                ExternalSnapshotId = external.Id,
                SourceProviderId = external.ProviderId,
                SourceExternalIdHash = external.ExternalIdHash,
                TargetProviderId = change == "manual-provider" ? "deezer" : null,
                TargetExternalId = change == "manual-provider" ? "different-track" : null,
                Decision = change == "manual-provider" ? ManualOverrideDecision.Pin : ManualOverrideDecision.Reject,
                Reason = "Fixture correction",
                DecisionVersion = 1,
                CreatedAt = DateTimeOffset.UtcNow
            });
        }
        await db.SaveChangesAsync();
    }

    private static string PlaylistHash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

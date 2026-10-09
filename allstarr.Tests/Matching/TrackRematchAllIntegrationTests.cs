using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed class TrackRematchAllIntegrationTests
{
    [Fact]
    [Trait("Category", "Sqlite")]
    public async Task Administrator_preview_includes_every_owner()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new DbFactory(database.Options);
        var now = new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
        var firstUserId = Guid.CreateVersion7();
        var secondUserId = Guid.CreateVersion7();
        var firstAccountId = Guid.CreateVersion7();
        var secondAccountId = Guid.CreateVersion7();

        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Users.AddRange(
                User(firstUserId, "First owner", now),
                User(secondUserId, "Second owner", now));
            db.ProviderAccounts.AddRange(
                Account(firstAccountId, firstUserId, now),
                Account(secondAccountId, secondUserId, now));
            db.ExternalMetadataSnapshots.AddRange(
                Snapshot(Guid.CreateVersion7(), firstAccountId, firstUserId,
                    "First", Hash(200), now),
                Snapshot(Guid.CreateVersion7(), secondAccountId, secondUserId,
                    "Second", Hash(201), now));
            await db.SaveChangesAsync();
        }

        var jobOptions = new DurableJobOptions();
        var queue = new DurableJobQueue(
            factory,
            jobOptions,
            new JobPayloadPolicy(jobOptions),
            new Clock(now),
            new DurableJobContextAuthorizer(factory));
        var rematches = new TrackRematchAllService(factory, queue, new Clock(now));

        var householdPreview = await rematches.PreviewAsync(new(firstUserId, true), null);
        var ownerPreview = await rematches.PreviewAsync(new(firstUserId, true), firstUserId);

        Assert.Equal(2, householdPreview.TotalTracks);
        Assert.Equal(2, householdPreview.TracksToRematch);
        Assert.Null(householdPreview.ScopeOwnerUserId);
        Assert.Equal(1, ownerPreview.TotalTracks);
        Assert.Equal(firstUserId, ownerPreview.ScopeOwnerUserId);
    }

    [Fact]
    [Trait("Category", "Sqlite")]
    public async Task Force_rematch_replaces_resolved_and_unresolved_decisions_once_and_preserves_manual_authority()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new DbFactory(database.Options);
        var now = new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);
        var clock = new Clock(now);
        var userId = Guid.CreateVersion7();
        var providerAccountId = Guid.CreateVersion7();
        var localOne = Guid.CreateVersion7();
        var localTwo = Guid.CreateVersion7();
        var snapshotIds = Enumerable.Range(0, 28).Select(_ => Guid.CreateVersion7()).ToArray();
        var manualCanonicalId = Guid.CreateVersion7();
        var manualSourceIdentityId = Guid.CreateVersion7();

        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Users.Add(User(userId, "Rematch owner", now));
            db.ProviderAccounts.Add(new ProviderAccountRecord
            {
                Id = providerAccountId,
                OwnerUserId = userId,
                ProviderId = "spotify",
                DisplayName = "Spotify",
                Enabled = true,
                CreatedAt = now,
                UpdatedAt = now
            });
            db.CanonicalRecordings.Add(new CanonicalRecordingRecord
            {
                Id = manualCanonicalId,
                CreatedByUserId = userId,
                CreatedAt = now,
                UpdatedAt = now
            });
            db.ProviderTrackIdentities.AddRange(
                new ProviderTrackIdentityRecord
                {
                    Id = manualSourceIdentityId,
                    CanonicalRecordingId = manualCanonicalId,
                    ProviderAccountId = providerAccountId,
                    ProviderId = "spotify",
                    ResourceKind = ProviderResourceKind.Track,
                    Scope = ProviderIdentityScope.Account,
                    ExternalId = "manual-source",
                    ExternalIdHash = Hash(3),
                    Verification = ProviderIdentityVerification.Verified,
                    VerificationMethod = "provider-snapshot",
                    DecisionVersion = 1,
                    VerifiedAt = now,
                    CreatedAt = now,
                    UpdatedAt = now
                },
                new ProviderTrackIdentityRecord
                {
                    Id = Guid.CreateVersion7(),
                    CanonicalRecordingId = manualCanonicalId,
                    ProviderId = "manual-provider",
                    ResourceKind = ProviderResourceKind.Track,
                    Scope = ProviderIdentityScope.Catalog,
                    ExternalId = "manual-target",
                    ExternalIdHash = new string('f', 64),
                    Verification = ProviderIdentityVerification.Pinned,
                    VerificationMethod = "manual-review",
                    DecisionVersion = 2,
                    VerifiedAt = now,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            db.LibraryTracks.AddRange(
                Local(localOne, userId, "One", now),
                Local(localTwo, userId, "Two", now));
            db.ExternalMetadataSnapshots.AddRange(
                Snapshot(snapshotIds[0], providerAccountId, userId, "One", Hash(0), now),
                Snapshot(snapshotIds[1], providerAccountId, userId, "Two", Hash(1), now),
                Snapshot(snapshotIds[2], providerAccountId, userId, "One", Hash(2), now),
                Snapshot(snapshotIds[3], providerAccountId, userId, "Manual", Hash(3), now,
                    manualSourceIdentityId));
            db.ExternalMetadataSnapshots.AddRange(snapshotIds.Skip(4).Select((id, index) =>
                Snapshot(id, providerAccountId, userId, "One", Hash(index + 4), now)));
            db.TrackMatches.AddRange(
                Decision(snapshotIds[0], userId, localOne, null, TrackMatchState.Accepted, now),
                Decision(snapshotIds[1], userId, null, null, TrackMatchState.Unresolved, now),
                Decision(snapshotIds[2], userId, localOne, null, TrackMatchState.Accepted, now),
                Decision(snapshotIds[3], userId, null, manualCanonicalId, TrackMatchState.Accepted, now));
            db.TrackMatches.AddRange(snapshotIds.Skip(4).Select(id =>
                Decision(id, userId, localOne, null, TrackMatchState.Accepted, now)));
            db.ManualTrackOverrides.Add(new ManualTrackOverrideRecord
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = userId,
                ExternalSnapshotId = snapshotIds[2],
                SourceProviderId = "spotify",
                SourceExternalIdHash = Hash(2),
                LibraryTrackId = localOne,
                Decision = ManualOverrideDecision.Pin,
                Reason = "Keep this selection",
                DecisionVersion = 1,
                MatcherVersion = TrackMatchDecisionEngine.AlgorithmVersion,
                CreatedAt = now
            });
            db.ManualTrackOverrides.Add(new ManualTrackOverrideRecord
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = null,
                ExternalSnapshotId = snapshotIds[3],
                SourceProviderId = "spotify",
                SourceExternalIdHash = Hash(3),
                TargetProviderId = "deezer",
                TargetExternalId = "manual-target",
                Decision = ManualOverrideDecision.Pin,
                Reason = "Household provider choice",
                DecisionVersion = 1,
                MatcherVersion = TrackMatchDecisionEngine.AlgorithmVersion,
                CreatedAt = now
            });
            await db.SaveChangesAsync();
        }

        var jobOptions = new DurableJobOptions();
        var queue = new DurableJobQueue(
            factory,
            jobOptions,
            new JobPayloadPolicy(jobOptions),
            clock,
            new DurableJobContextAuthorizer(factory));
        var rematches = new TrackRematchAllService(factory, queue, clock);
        var preview = await rematches.PreviewAsync(new(userId, true), userId);

        Assert.Equal(28, preview.TotalTracks);
        Assert.Equal(2, preview.ProtectedManualTracks);
        Assert.Equal(26, preview.TracksToRematch);
        Assert.Equal(26, preview.AutomaticDecisionsToReplace);

        var receipt = await rematches.QueueForceAsync(new(userId, true), preview);
        var lateSnapshotId = Guid.CreateVersion7();
        TrackRematchAllJobPayload payload;
        await using (var db = await factory.CreateDbContextAsync())
        {
            var job = await db.Jobs.SingleAsync(item => item.Id == receipt.JobId);
            payload = JsonSerializer.Deserialize<TrackRematchAllJobPayload>(job.PayloadJson)!;
            db.ExternalMetadataSnapshots.Add(Snapshot(
                lateSnapshotId,
                providerAccountId,
                userId,
                "Late",
                Hash(99),
                now.AddSeconds(1)));
            await db.SaveChangesAsync();
        }
        Assert.Equal(preview.SnapshotCutoff, payload.SnapshotCutoff);
        Assert.Equal(preview.SnapshotFingerprint, payload.SnapshotFingerprint);
        var claim = new DurableJobClaim(
            receipt.JobId,
            Guid.CreateVersion7(),
            1,
            TrackRematchAllJobHandler.Type,
            JsonSerializer.SerializeToElement(payload),
            userId,
            null,
            null,
            JsonSerializer.SerializeToElement(new { }),
            TrackRematchAllService.OperationCorrelation(payload.OperationId),
            "worker",
            now.AddMinutes(1));
        var commands = new TrackMatchCommandService(
            factory,
            new TrackMatchDecisionEngine(),
            new ProviderAccountResolver(factory),
            clock, new TestBackendLibraryAccess(factory, "music"));
        var handler = new TrackRematchAllJobHandler(factory, rematches, commands, clock);

        var completion = await handler.ExecuteAsync(
            new DurableJobExecutionContext(claim, EmptyServices.Instance), default);
        Assert.Equal(DurableJobCompletionKind.Deferred, completion.Kind);

        await using (var db = await factory.CreateDbContextAsync())
        {
            Assert.Equal(25, await db.TrackMatches.CountAsync(item =>
                item.MatcherVersion == TrackMatchDecisionEngine.AlgorithmVersion));
            Assert.Single(await db.TrackMatches.Where(item => item.ExternalSnapshotId == snapshotIds[2]).ToListAsync());
            Assert.Single(await db.TrackMatches.Where(item => item.ExternalSnapshotId == snapshotIds[3]).ToListAsync());
            Assert.Equal(2, await db.ManualTrackOverrides.CountAsync(item => item.RevokedAt == null));
            Assert.Equal(25, await db.AuditEvents.CountAsync(item => item.Category == "track-rematch"));
            Assert.False(await db.TrackMatches.AnyAsync(item => item.ExternalSnapshotId == lateSnapshotId));
        }

        var resumed = await handler.ExecuteAsync(
            new DurableJobExecutionContext(claim with { AttemptNumber = 2 }, EmptyServices.Instance), default);
        Assert.Equal(DurableJobCompletionKind.Succeeded, resumed.Kind);
        var repeated = await handler.ExecuteAsync(
            new DurableJobExecutionContext(claim with { AttemptNumber = 3 }, EmptyServices.Instance), default);
        Assert.Equal(DurableJobCompletionKind.Succeeded, repeated.Kind);
        await using (var final = await factory.CreateDbContextAsync())
        {
            Assert.Equal(54, await final.TrackMatches.CountAsync());
            Assert.Equal(26, await final.AuditEvents.CountAsync(item => item.Category == "track-rematch"));
            Assert.False(await final.TrackMatches.AnyAsync(item => item.ExternalSnapshotId == lateSnapshotId));
            var stale = await final.TrackMatches
                .Where(item => item.ExternalSnapshotId == snapshotIds[0])
                .OrderByDescending(item => item.DecisionVersion)
                .FirstAsync();
            stale.MatcherVersion = "retired-v2";
            await final.SaveChangesAsync();
        }

        Assert.Equal(1, await rematches.QueueAlgorithmUpgradesAsync());
        await using var queued = await factory.CreateDbContextAsync();
        var rollout = JsonSerializer.Deserialize<TrackRematchAllJobPayload>(
            (await queued.Jobs.SingleAsync(item => item.Type == TrackRematchAllJobHandler.Type &&
                                                   item.Id != receipt.JobId)).PayloadJson)!;
        Assert.False(rollout.Force);
        Assert.Equal(1, rollout.ApprovedCount);
    }

    private static LibraryTrackRecord Local(
        Guid id,
        Guid userId,
        string title,
        DateTimeOffset now) => new()
        {
            Id = id,
            OwnerUserId = userId,
            BackendLibraryId = "music",
            Protocol = "jellyfin",
            BackendInstanceId = "backend",
            BackendItemId = $"local-{title.ToLowerInvariant()}",
            FilePath = $"/music/{title.ToLowerInvariant()}.flac",
            Title = title,
            Artist = "Artist",
            DurationMilliseconds = 180_000,
            ProviderIdsJson = "{}",
            IndexedAt = now,
            SourceModifiedAt = now,
            UpdatedAt = now
        };

    private static UserRecord User(
        Guid id,
        string name,
        DateTimeOffset now) => new()
        {
            Id = id,
            DisplayName = name,
            Enabled = true,
            IsAdmin = true,
            BackendType = "jellyfin",
            BackendInstanceId = "backend",
            BackendPrincipalId = id.ToString("N"),
            CreatedAt = now,
            UpdatedAt = now
        };

    private static ProviderAccountRecord Account(
        Guid id,
        Guid ownerUserId,
        DateTimeOffset now) => new()
        {
            Id = id,
            OwnerUserId = ownerUserId,
            ProviderId = "spotify",
            DisplayName = "Spotify",
            Enabled = true,
            CreatedAt = now,
            UpdatedAt = now
        };

    private static ExternalMetadataSnapshotRecord Snapshot(
        Guid id,
        Guid providerAccountId,
        Guid userId,
        string title,
        string hash,
        DateTimeOffset now,
        Guid? providerTrackIdentityId = null) => new()
        {
            Id = id,
            OwnerUserId = userId,
            ProviderAccountId = providerAccountId,
            ProviderTrackIdentityId = providerTrackIdentityId,
            BackendInstanceId = "backend",
            BackendPrincipalId = userId.ToString("N"),
            Protocol = "jellyfin",
            ProviderId = "spotify",
            ResourceKind = "track",
            ExternalIdHash = hash,
            SnapshotVersion = 1,
            ProviderRevision = "1",
            PayloadJson = JsonSerializer.Serialize(new
            {
                Title = title,
                Artist = "Artist",
                DurationMilliseconds = 180_000
            }),
            PayloadSha256 = hash,
            CorrelationId = "setup",
            RetrievedAt = now
        };

    private static TrackMatchRecord Decision(
        Guid snapshotId,
        Guid userId,
        Guid? libraryTrackId,
        Guid? canonicalRecordingId,
        TrackMatchState state,
        DateTimeOffset now) => new()
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = userId,
            ExternalSnapshotId = snapshotId,
            LibraryTrackId = libraryTrackId,
            CanonicalRecordingId = canonicalRecordingId,
            State = state,
            Confidence = state == TrackMatchState.Accepted ? .95 : 0,
            Threshold = .88,
            DecisionVersion = 1,
            SourceSnapshotVersion = 1,
            MatcherVersion = "retired-v1",
            PolicyVersion = "old-policy",
            CandidateResultsJson = "[]",
            ReasonsJson = "[]",
            WarningsJson = "[]",
            CorrelationId = "setup",
            DecidedAt = now
        };

    private static string Hash(int index) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"track-{index}"))).ToLowerInvariant();

    private sealed class DbFactory(DbContextOptions<AllstarrDbContext> options)
        : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);

        public Task<AllstarrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class Clock(DateTimeOffset now) : IPlatformClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public static readonly EmptyServices Instance = new();
        public object? GetService(Type serviceType) => null;
    }
}

using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

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
            1,
            TrackRematchJobHandler.Type,
            JsonSerializer.SerializeToElement(payload),
            userId,
            null,
            null,
            JsonSerializer.SerializeToElement(new { }),
            TrackRematchAllService.OperationCorrelation(payload.OperationId));
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
        // A restart re-runs the scan; the durable job already records this version, so nothing is queued twice.
        Assert.Equal(0, await rematches.QueueAlgorithmUpgradesAsync());
        await using var queued = await factory.CreateDbContextAsync();
        var rollout = JsonSerializer.Deserialize<TrackRematchAllJobPayload>(
            (await queued.Jobs.SingleAsync(item => item.Type == TrackRematchJobHandler.Type &&
                                                   item.Id != receipt.JobId)).PayloadJson)!;
        Assert.False(rollout.Force);
        Assert.Equal(TrackRematchScopes.Algorithm, rollout.Scope);
        Assert.Equal(1, rollout.ApprovedCount);
    }

    [Theory]
    [InlineData("access", true, false)]
    [InlineData("stale", true, false)]
    [InlineData("not-configured", true, false)]
    [InlineData("failing", true, false)]
    [InlineData("access", false, false)]
    [InlineData("stale", false, false)]
    [InlineData("not-configured", false, false)]
    [InlineData("failing", false, false)]
    [InlineData("service-access", true, false)]
    [InlineData("service-access", false, false)]
    [InlineData("stale", true, true)]
    [InlineData("stale", false, true)]
    [Trait("Category", "Sqlite")]
    public async Task Full_rematch_preserves_local_selection_until_library_is_available(string unavailable, bool force, bool newerSnapshot)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new DbFactory(database.Options);
        var now = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
        var clock = new Clock(now);
        var userId = Guid.CreateVersion7();
        var snapshotId = Guid.CreateVersion7();
        var access = new TestBackendLibraryAccess(factory, "music");
        var queue = Queue(factory, clock);
        var original = await SeedUnavailableLibraryAsync(factory, queue, access, userId, snapshotId, unavailable, now);
        if (newerSnapshot) snapshotId = await AddNewerSnapshotAsync(factory, snapshotId, now);
        var rematches = new TrackRematchAllService(factory, queue, clock);
        Guid jobId;
        if (force)
            jobId = (await rematches.QueueForceAsync(new(userId, true),
                await rematches.PreviewAsync(new(userId, true), userId))).JobId;
        else
        {
            Assert.Equal(1, await rematches.QueueAlgorithmUpgradesAsync());
            await using var queued = await factory.CreateDbContextAsync();
            jobId = (await queued.Jobs.SingleAsync(item => item.Type == TrackRematchJobHandler.Type)).Id;
        }
        DurableJobClaim claim;
        await using (var db = await factory.CreateDbContextAsync())
        {
            var job = await db.Jobs.SingleAsync(item => item.Id == jobId);
            claim = new(job.Id, 1, job.Type, JsonDocument.Parse(job.PayloadJson).RootElement.Clone(),
                userId, null, null, JsonSerializer.SerializeToElement(new { }), job.CorrelationId);
        }
        var configuration = new ConfigurationManager
        {
            ["Jellyfin:Url"] = "https://jellyfin.test",
            ["Jellyfin:ApiKey"] = unavailable == "service-access" ? "" : "fixture"
        };
        var commands = new TrackMatchCommandService(factory, new TrackMatchDecisionEngine(),
            new ProviderAccountResolver(factory), clock, access, configuration: configuration);
        var handler = new TrackRematchAllJobHandler(factory, rematches, commands, clock);

        var completion = await handler.ExecuteAsync(new(claim, EmptyServices.Instance), default);

        Assert.Equal(DurableJobCompletionKind.Retry, completion.Kind);
        await using var verification = await factory.CreateDbContextAsync();
        Assert.Equal(original, JsonSerializer.Serialize(Assert.Single(await verification.TrackMatches.AsNoTracking().ToListAsync())));
        var audit = Assert.Single(await verification.AuditEvents.Where(item => item.Category == "track-rematch").ToListAsync());
        Assert.Equal("retry_local_library_unavailable", audit.Outcome);
        Assert.Single((await rematches.ReadWorkAsync(
            claim.Payload.Deserialize<TrackRematchAllJobPayload>()!, default)).Pending);

        access.Permissions[userId] = new(true, ["music"]);
        configuration["Jellyfin:ApiKey"] = "restored-fixture";
        clock.UtcNow = now.AddMinutes(1);
        await using (var refreshed = await factory.CreateDbContextAsync())
        {
            var track = await refreshed.LibraryTracks.SingleAsync();
            track.IndexedAt = clock.UtcNow;
            await refreshed.SaveChangesAsync();
        }
        var resumed = await handler.ExecuteAsync(new(claim with { AttemptNumber = 2 }, EmptyServices.Instance), default);
        Assert.Equal(DurableJobCompletionKind.Succeeded, resumed.Kind);
        await using var recovered = await factory.CreateDbContextAsync();
        var latest = await recovered.TrackMatches.OrderByDescending(item => item.DecidedAt)
            .ThenByDescending(item => item.DecisionVersion).FirstAsync();
        Assert.Equal(newerSnapshot ? 1 : 2, latest.DecisionVersion);
        Assert.Null(latest.LibraryTrackId);
    }

    [Theory]
    [InlineData("access", true, false)]
    [InlineData("stale", true, false)]
    [InlineData("not-configured", true, false)]
    [InlineData("failing", true, false)]
    [InlineData("access", false, false)]
    [InlineData("stale", false, false)]
    [InlineData("not-configured", false, false)]
    [InlineData("failing", false, false)]
    [InlineData("service-access", true, false)]
    [InlineData("service-access", false, false)]
    [InlineData("stale", true, true)]
    [InlineData("stale", false, true)]
    [Trait("Category", "Sqlite")]
    public async Task Snapshot_rematch_preserves_local_selection_until_library_is_available(string unavailable, bool withExecution, bool newerSnapshot)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new DbFactory(database.Options);
        var now = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
        var clock = new Clock(now);
        var userId = Guid.CreateVersion7();
        var snapshotId = Guid.CreateVersion7();
        var access = new TestBackendLibraryAccess(factory, "music");
        var original = await SeedUnavailableLibraryAsync(factory, Queue(factory, clock), access,
            userId, snapshotId, unavailable, now);
        if (newerSnapshot) snapshotId = await AddNewerSnapshotAsync(factory, snapshotId, now);
        var configuration = new ConfigurationManager
        {
            ["Jellyfin:Url"] = "https://jellyfin.test",
            ["Jellyfin:ApiKey"] = unavailable == "service-access" ? "" : "fixture"
        };
        var commands = new TrackMatchCommandService(factory, new TrackMatchDecisionEngine(),
            new ProviderAccountResolver(factory), clock, access, configuration: configuration);
        var execution = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "backend", userId.ToString("N"),
            new AllstarrPrincipal(userId, "jellyfin", "backend", userId.ToString("N"), "Owner", true),
            "snapshot-rematch", now.AddMinutes(2), default);

        var result = withExecution
            ? await commands.RematchSnapshotAsync(execution, snapshotId, "snapshot-rematch", "manual-rematch-v3")
            : await commands.RematchSnapshotAsync(new TrackMatchActor(userId, true), snapshotId, "snapshot-rematch");

        Assert.False(result.Succeeded);
        Assert.Equal("LocalLibraryUnavailable", result.Failure.ToString());
        await using var verification = await factory.CreateDbContextAsync();
        Assert.Equal(original, JsonSerializer.Serialize(Assert.Single(await verification.TrackMatches.AsNoTracking().ToListAsync())));
        Assert.Equal("retry_local_library_unavailable", (await verification.AuditEvents.SingleAsync(item =>
            item.Category == "track-rematch")).Outcome);
    }

    private static async Task<Guid> AddNewerSnapshotAsync(DbFactory factory, Guid originalId, DateTimeOffset now)
    {
        await using var db = await factory.CreateDbContextAsync();
        var previous = await db.ExternalMetadataSnapshots.SingleAsync(item => item.Id == originalId);
        var newer = Snapshot(Guid.CreateVersion7(), previous.ProviderAccountId!.Value,
            previous.OwnerUserId, "Missing", previous.ExternalIdHash, now);
        newer.SnapshotVersion = 2;
        db.ExternalMetadataSnapshots.Add(newer);
        await db.SaveChangesAsync();
        return newer.Id;
    }

    private static DurableJobQueue Queue(DbFactory factory, Clock clock)
    {
        var options = new DurableJobOptions();
        return new(factory, options, new JobPayloadPolicy(options), clock, new DurableJobContextAuthorizer(factory));
    }

    private static async Task<string> SeedUnavailableLibraryAsync(DbFactory factory, DurableJobQueue queue,
        TestBackendLibraryAccess access, Guid userId, Guid snapshotId, string unavailable, DateTimeOffset now)
    {
        var accountId = Guid.CreateVersion7();
        var localId = Guid.CreateVersion7();
        var original = Decision(snapshotId, userId, localId, null, TrackMatchState.Accepted, now);
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Users.Add(User(userId, "Owner", now));
            db.ProviderAccounts.Add(Account(accountId, userId, now));
            db.ExternalMetadataSnapshots.Add(Snapshot(snapshotId, accountId, userId, "Missing", Hash(300), now));
            db.LibraryTracks.Add(Local(localId, userId, "Other", unavailable == "stale" ? now.AddMinutes(-61) : now));
            db.TrackMatches.Add(original);
            await db.SaveChangesAsync();
        }
        if (unavailable == "access") access.Permissions[userId] = BackendLibraryAccess.Unavailable;
        if (unavailable is "not-configured" or "failing")
        {
            var receipt = await queue.EnqueueAsync(new DurableJobEnqueueRequest<LibraryIndexJobPayload>(
                "library.index", "index-test", new(null, "backend", userId.ToString("N")), userId));
            await using var db = await factory.CreateDbContextAsync();
            var job = await db.Jobs.SingleAsync(item => item.Id == receipt.JobId);
            job.State = unavailable == "not-configured" ? DurableJobState.Failed : DurableJobState.Pending;
            job.LastErrorCode = unavailable == "not-configured" ? "library_index_not_configured" : "library_index_backend_unavailable";
            job.LastAttemptCompletedAt = now;
            await db.SaveChangesAsync();
        }
        return JsonSerializer.Serialize(original);
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
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public static readonly EmptyServices Instance = new();
        public object? GetService(Type serviceType) => null;
    }
}

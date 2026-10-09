using allstarr.Core.Downloads;
using allstarr.Core.Favorites;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;

namespace allstarr.Tests;

public sealed class DatabaseLineageConstraintTests : IAsyncLifetime
{
    private SqliteTestDatabase _database = null!;
    private TestDbContextFactory _factory = null!;
    private Guid _userA;
    private Guid _userB;
    private Guid _jobA;
    private Guid _jobB;
    private Guid _fileA;
    private Guid _fileB;
    private Guid _accountB;

    public async Task InitializeAsync()
    {
        _database = await SqliteTestDatabase.CreateAsync();
        _factory = new TestDbContextFactory(_database.Options);
        await using var db = await _factory.CreateDbContextAsync();

        _userA = Guid.CreateVersion7();
        _userB = Guid.CreateVersion7();
        _jobA = Guid.CreateVersion7();
        _jobB = Guid.CreateVersion7();
        _fileA = Guid.CreateVersion7();
        _fileB = Guid.CreateVersion7();
        _accountB = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;

        db.Users.AddRange(
            User(_userA, "User A", now),
            User(_userB, "User B", now));
        db.ProviderAccounts.Add(new ProviderAccountRecord
        {
            Id = _accountB,
            OwnerUserId = _userB,
            ProviderId = "lineage-provider",
            DisplayName = "Listener B account",
            Enabled = true,
            CreatedAt = now,
            UpdatedAt = now
        });
        db.Jobs.AddRange(
            Job(_jobA, _userA, "job-a", now),
            Job(_jobB, _userB, "job-b", now));
        db.ManagedFiles.AddRange(
            File(_fileA, _userA, _jobA, "a", now),
            File(_fileB, _userB, _jobB, "b", now));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task SqliteBaseline_RejectsCrossScopeJobAndArtifactLineage()
    {
        await RejectAsync(db => db.Jobs.Add(Job(
            Guid.CreateVersion7(), Guid.CreateVersion7(), "missing-owner", DateTimeOffset.UtcNow)));

        await RejectAsync(db =>
        {
            var job = Job(Guid.CreateVersion7(), _userA, "cross-account", DateTimeOffset.UtcNow);
            job.ProviderAccountId = _accountB;
            job.ProviderCapability = "download";
            db.Jobs.Add(job);
        });

        await RejectAsync(db => db.ManagedFiles.Add(File(
            Guid.CreateVersion7(), _userB, _jobA, "cross-job", DateTimeOffset.UtcNow)));

        await RejectAsync(db => db.FavoriteEvents.Add(new FavoriteEventRecord
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = _userB,
            Protocol = "subsonic",
            BackendInstanceId = "primary",
            BackendPrincipalId = "user-b",
            ItemId = "track",
            Operation = FavoriteOperation.Favorite,
            SourceRevision = "1",
            EventKey = Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant(),
            CorrelationId = "lineage-test",
            PolicySnapshotJson = "{}",
            JobId = _jobA,
            State = FavoriteEventState.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        }));

        await RejectAsync(db => db.ProviderDownloadWorkspaces.Add(new ProviderDownloadWorkspaceEntity
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = Guid.NewGuid().ToString("N"),
            OwnerUserId = _userB,
            DurableJobId = _jobA,
            ProviderId = "lineage-provider",
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow
        }));

        await RejectAsync(db => db.MetadataEnrichmentPlans.Add(Plan(
            _userB, _jobA, _fileB, "1")));
        await RejectAsync(db => db.MetadataEnrichmentPlans.Add(Plan(
            _userB, _jobB, _fileA, "2")));
    }

    [Fact]
    public async Task SqliteBaseline_EnforcesReferenceDmlAndDerivesReferenceCount()
    {
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var now = DateTimeOffset.UtcNow.UtcTicks;
            var scope = $"{_userA:N}";
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO managed_file_references
                    ("Id", "ManagedFileId", "OwnerUserId", "ScopeKey", "ReferenceKey", "CreatedAt", "ReleasedAt", "Revision")
                VALUES ({first}, {_fileA}, {_userA}, {scope}, {"direct:first"}, {now}, NULL, {1})
                """);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO managed_file_references
                    ("Id", "ManagedFileId", "OwnerUserId", "ScopeKey", "ReferenceKey", "CreatedAt", "ReleasedAt", "Revision")
                VALUES ({second}, {_fileA}, {_userA}, {scope}, {"direct:second"}, {now}, NULL, {1})
                """);
            Assert.Equal(2, await db.ManagedFiles.Where(item => item.Id == _fileA)
                .Select(item => item.ReferenceCount).SingleAsync());

            await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE managed_files SET "ReferenceCount"={9} WHERE "Id"={_fileA}
                """));

            var removal = await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE managed_files SET "RemovedAt"={now} WHERE "Id"={_fileA}
                """));
            Assert.Contains("CK_managed_file_removed_references", removal.Message);
            var rescope = await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE managed_files SET "ScopeKey"={"other"} WHERE "Id"={_fileA}
                """));
            Assert.Contains("FK_managed_file_saved_reference_lineage", rescope.Message);

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE managed_file_references SET "ReleasedAt"={DateTimeOffset.UtcNow.UtcTicks}, "Revision"="Revision"+1 WHERE "Id"={first}
                """);
            Assert.Equal(1, await db.ManagedFiles.Where(item => item.Id == _fileA)
                .Select(item => item.ReferenceCount).SingleAsync());
        }

        await using (var db = await _factory.CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM managed_file_references WHERE \"Id\"={second}");
            Assert.Equal(0, await db.ManagedFiles.Where(item => item.Id == _fileA)
                .Select(item => item.ReferenceCount).SingleAsync());
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE managed_files SET \"RemovedAt\"={DateTimeOffset.UtcNow.UtcTicks} WHERE \"Id\"={_fileA}");
        }

        await using var crossed = await _factory.CreateDbContextAsync();
        await Assert.ThrowsAsync<SqliteException>(() => crossed.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO managed_file_references
                ("Id", "ManagedFileId", "OwnerUserId", "ScopeKey", "ReferenceKey", "CreatedAt", "ReleasedAt", "Revision")
            VALUES ({Guid.CreateVersion7()}, {_fileA}, {_userB}, {$"{_userB:N}"}, {"direct:crossed"}, {DateTimeOffset.UtcNow.UtcTicks}, NULL, {1})
            """));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SqliteBaseline_NullOwnerCannotBypassJobLineage(bool householdJob)
    {
        var jobId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.Jobs.Add(Job(jobId, householdJob ? null : _userA, "nullable-owner", now));
            await db.SaveChangesAsync();
        }
        await RejectAsync(db => db.ManagedFiles.Add(File(Guid.CreateVersion7(),
            householdJob ? _userA : null, jobId, "c", now)));
        await RejectAsync(db => db.ProviderDownloadWorkspaces.Add(new()
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = Guid.NewGuid().ToString("N"),
            OwnerUserId = householdJob ? _userA : null,
            DurableJobId = jobId,
            ProviderId = "lineage-provider",
            IdempotencyKey = "nullable-workspace",
            CreatedAt = now
        }));
        await using var valid = await _factory.CreateDbContextAsync();
        valid.ManagedFiles.Add(File(Guid.CreateVersion7(), householdJob ? null : _userA, jobId, "d", now));
        await valid.SaveChangesAsync();
        Guid? changedOwner = householdJob ? _userA : null;
        await Assert.ThrowsAsync<SqliteException>(() => valid.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE durable_jobs SET "OwnerUserId"={changedOwner} WHERE "Id"={jobId}
            """));
    }

    [Fact]
    public async Task SqliteBaseline_RejectsArtifactManagedFileOutsideExactScope()
    {
        var workspaceId = Guid.CreateVersion7();
        var workspaceKey = Guid.NewGuid().ToString("N");
        var verifiedArtifactId = Guid.CreateVersion7();
        await using var db = await _factory.CreateDbContextAsync();
        db.ProviderDownloadWorkspaces.Add(new ProviderDownloadWorkspaceEntity
        {
            Id = workspaceId,
            WorkspaceId = workspaceKey,
            OwnerUserId = _userA,
            DurableJobId = _jobA,
            ProviderId = "lineage-provider",
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.ProviderDownloadArtifacts.Add(new ProviderDownloadArtifactEntity
        {
            Id = verifiedArtifactId,
            WorkspaceRecordId = workspaceId,
            WorkspaceId = workspaceKey,
            OwnerUserId = _userA,
            DurableJobId = _jobA,
            ProviderId = "lineage-provider",
            ProviderArtifactId = "verified",
            RelativePath = "verified.flac",
            ContentSha256 = new string('c', 64),
            Length = 1,
            State = ProviderDownloadArtifactState.Verified,
            CreatedAt = DateTimeOffset.UtcNow,
            VerifiedAt = DateTimeOffset.UtcNow,
            Revision = 1
        });
        await db.SaveChangesAsync();

        var runtimeStore = new EfProviderDownloadArtifactStore(_factory);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            runtimeStore.MarkPlacedAsync(verifiedArtifactId, _fileB, default));

        db.ProviderDownloadArtifacts.Add(new ProviderDownloadArtifactEntity
        {
            Id = Guid.CreateVersion7(),
            WorkspaceRecordId = workspaceId,
            WorkspaceId = Guid.NewGuid().ToString("N"),
            OwnerUserId = _userA,
            DurableJobId = _jobA,
            ProviderId = "lineage-provider",
            ProviderArtifactId = "cross-file",
            RelativePath = "song.flac",
            ContentSha256 = new string('b', 64),
            Length = 1,
            State = ProviderDownloadArtifactState.Placed,
            ManagedFileId = _fileB,
            CreatedAt = DateTimeOffset.UtcNow,
            VerifiedAt = DateTimeOffset.UtcNow,
            Revision = 1
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    private async Task RejectAsync(Action<AllstarrDbContext> addInvalid)
    {
        await using var db = await _factory.CreateDbContextAsync();
        addInvalid(db);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    private static UserRecord User(Guid id, string name, DateTimeOffset now) => new()
    {
        Id = id,
        DisplayName = name,
        Enabled = true,
        BackendType = "subsonic",
        BackendInstanceId = "primary",
        BackendPrincipalId = id.ToString("N"),
        CreatedAt = now,
        UpdatedAt = now
    };

    internal static DurableJobRecord Job(Guid id, Guid? ownerUserId, string key, DateTimeOffset now) => new()
    {
        Id = id,
        ScopeKey = $"{ownerUserId:N}",
        OwnerUserId = ownerUserId,
        PolicySnapshotJson = "{}",
        RequestFingerprint = new string('a', 64),
        CorrelationId = "lineage-test",
        Type = "lineage.test",
        PayloadJson = "{}",
        IdempotencyKey = key,
        State = DurableJobState.Pending,
        MaxAttempts = 3,
        MaxDeferrals = 3,
        AvailableAt = now,
        CreatedAt = now,
        UpdatedAt = now
    };

    private static ManagedFileOwnershipEntity File(
        Guid id, Guid? ownerUserId, Guid jobId, string suffix, DateTimeOffset now) => new()
        {
            Id = id,
            RootId = Guid.CreateVersion7(),
            TargetRootPath = $"/library/{suffix}",
            CanonicalPath = $"/library/{suffix}/{id:N}.flac",
            ContentSha256 = new string(suffix[0], 64),
            Length = 1,
            PlacementMethod = ManagedFilePlacementMethod.Copy,
            OwnerUserId = ownerUserId,
            SourceJobId = jobId,
            ScopeKey = $"{ownerUserId:N}",
            ReferenceCount = 1,
            IsManaged = true,
            CreatedAt = now
        };

    private static MetadataEnrichmentPlanRecord Plan(
        Guid ownerUserId, Guid jobId, Guid fileId, string suffix) => new()
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = ownerUserId,
            LineageJobId = jobId,
            ManagedArtifactId = fileId,
            Fingerprint = new string(suffix[0], 64),
            PlanVersion = 1,
            SourceRevisionsJson = "[]",
            DecisionsJson = "[]",
            TagsJson = "{}",
            PathValuesJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow
        };

    private sealed class TestDbContextFactory(DbContextOptions<AllstarrDbContext> options)
        : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);

        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AllstarrDbContext(options));
    }

    public async Task DisposeAsync()
    {
        await _database.DisposeAsync();
    }
}

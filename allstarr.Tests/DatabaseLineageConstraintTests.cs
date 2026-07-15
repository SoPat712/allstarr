using allstarr.Core.Downloads;
using allstarr.Core.Favorites;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Data.Sqlite;

namespace allstarr.Tests;

public sealed class DatabaseLineageConstraintTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "allstarr-tests", Guid.NewGuid().ToString("N"));
    private TestDbContextFactory _factory = null!;
    private Guid _tenantA;
    private Guid _tenantB;
    private Guid _userA;
    private Guid _userB;
    private Guid _jobA;
    private Guid _jobB;
    private Guid _fileA;
    private Guid _fileB;
    private Guid _accountB;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var options = new DbContextOptionsBuilder<AllstarrDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_root, "lineage.db")}")
            .Options;
        _factory = new TestDbContextFactory(options);
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.MigrateAsync();

        _tenantA = Guid.CreateVersion7();
        _tenantB = Guid.CreateVersion7();
        _userA = Guid.CreateVersion7();
        _userB = Guid.CreateVersion7();
        _jobA = Guid.CreateVersion7();
        _jobB = Guid.CreateVersion7();
        _fileA = Guid.CreateVersion7();
        _fileB = Guid.CreateVersion7();
        _accountB = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;

        db.Tenants.AddRange(
            new TenantRecord { Id = _tenantA, Slug = "lineage-a", Name = "Lineage A", CreatedAt = now },
            new TenantRecord { Id = _tenantB, Slug = "lineage-b", Name = "Lineage B", CreatedAt = now });
        db.Users.AddRange(
            User(_userA, _tenantA, "User A", now),
            User(_userB, _tenantB, "User B", now));
        db.ProviderAccounts.Add(new ProviderAccountRecord
        {
            Id = _accountB,
            TenantId = _tenantB,
            OwnerUserId = _userB,
            ProviderId = "lineage-provider",
            DisplayName = "Tenant B account",
            Scope = ProviderAccountScope.User,
            Enabled = true,
            CreatedAt = now,
            UpdatedAt = now
        });
        db.Jobs.AddRange(
            Job(_jobA, _tenantA, _userA, "job-a", now),
            Job(_jobB, _tenantB, _userB, "job-b", now));
        db.ManagedFiles.AddRange(
            File(_fileA, _tenantA, _userA, _jobA, "a", now),
            File(_fileB, _tenantB, _userB, _jobB, "b", now));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task SqliteMigration_RejectsCrossScopeJobAndArtifactLineage()
    {
        await RejectAsync(db => db.Jobs.Add(Job(
            Guid.CreateVersion7(), _tenantA, _userB, "cross-owner", DateTimeOffset.UtcNow)));

        await RejectAsync(db =>
        {
            var job = Job(Guid.CreateVersion7(), _tenantA, _userA, "cross-account", DateTimeOffset.UtcNow);
            job.ProviderAccountId = _accountB;
            job.ProviderCapability = "download";
            db.Jobs.Add(job);
        });

        await RejectAsync(db => db.ManagedFiles.Add(File(
            Guid.CreateVersion7(), _tenantB, _userB, _jobA, "cross-job", DateTimeOffset.UtcNow)));

        await RejectAsync(db => db.FavoriteEvents.Add(new FavoriteEventRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantB,
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
            TenantId = _tenantB,
            OwnerUserId = _userB,
            DurableJobId = _jobA,
            ProviderId = "lineage-provider",
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow
        }));

        await RejectAsync(db => db.MetadataEnrichmentPlans.Add(Plan(
            _tenantB, _userB, _jobA, _fileB, "1")));
        await RejectAsync(db => db.MetadataEnrichmentPlans.Add(Plan(
            _tenantB, _userB, _jobB, _fileA, "2")));
    }

    [Fact]
    public async Task SqliteMigration_EnforcesReferenceDmlAndDerivesReferenceCount()
    {
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var now = DateTimeOffset.UtcNow.UtcTicks;
            var scope = $"{_tenantA:N}:{_userA:N}";
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO managed_file_references
                    (Id, ManagedFileId, TenantId, OwnerUserId, ScopeKey, ReferenceKey, CreatedAt, ReleasedAt, Revision)
                VALUES ({first}, {_fileA}, {_tenantA}, {_userA}, {scope}, {"direct:first"}, {now}, NULL, {1})
                """);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO managed_file_references
                    (Id, ManagedFileId, TenantId, OwnerUserId, ScopeKey, ReferenceKey, CreatedAt, ReleasedAt, Revision)
                VALUES ({second}, {_fileA}, {_tenantA}, {_userA}, {scope}, {"direct:second"}, {now}, NULL, {1})
                """);
            Assert.Equal(2, await db.ManagedFiles.Where(item => item.Id == _fileA)
                .Select(item => item.ReferenceCount).SingleAsync());

            await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE managed_files SET ReferenceCount={9} WHERE Id={_fileA}
                """));

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE managed_file_references SET ReleasedAt={DateTimeOffset.UtcNow.UtcTicks}, Revision=Revision+1 WHERE Id={first}
                """);
            Assert.Equal(1, await db.ManagedFiles.Where(item => item.Id == _fileA)
                .Select(item => item.ReferenceCount).SingleAsync());
        }

        await using var crossed = await _factory.CreateDbContextAsync();
        await Assert.ThrowsAsync<SqliteException>(() => crossed.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO managed_file_references
                (Id, ManagedFileId, TenantId, OwnerUserId, ScopeKey, ReferenceKey, CreatedAt, ReleasedAt, Revision)
            VALUES ({Guid.CreateVersion7()}, {_fileA}, {_tenantB}, {_userB}, {$"{_tenantB:N}:{_userB:N}"}, {"direct:crossed"}, {DateTimeOffset.UtcNow.UtcTicks}, NULL, {1})
            """));
    }

    [Fact]
    public async Task SqliteMigration_PreservesEveryLegacyReferenceAcrossUpgradeAndRollback()
    {
        const string previous = "20260713225623_Phase2BLegacyEnvImportIdempotency";
        var path = Path.Combine(_root, "legacy-reference-upgrade.db");
        var options = new DbContextOptionsBuilder<AllstarrDbContext>().UseSqlite($"Data Source={path}").Options;
        var tenant = Guid.CreateVersion7();
        var user = Guid.CreateVersion7();
        var file = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow.UtcTicks;

        await using var db = new AllstarrDbContext(options);
        var migrator = db.Database.GetService<IMigrator>();
        await migrator.MigrateAsync(previous);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO tenants (Id,Slug,Name,CreatedAt) VALUES ({tenant},{"legacy-ref"},{"Legacy refs"},{now})");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO users (Id,TenantId,DisplayName,Status,CreatedAt,UpdatedAt) VALUES ({user},{tenant},{"Legacy user"},{"Active"},{now},{now})");
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO managed_files
                (Id,RootId,TargetRootPath,CanonicalPath,ContentSha256,Length,PlacementMethod,TenantId,OwnerUserId,LibraryScopeId,SourceJobId,ScopeKey,ReferenceCount,IsManaged,CreatedAt,RemovedAt,Revision)
            VALUES ({file},{Guid.CreateVersion7()},{"/legacy"},{"/legacy/song.flac"},{new string('a', 64)},{1L},{"Copy"},{tenant},{user},{"music"},NULL,{"tenant:user:music"},{3},{true},{now},NULL,{1L})
            """);

        await migrator.MigrateAsync();
        var originalIds = await db.ManagedFileReferences.Where(item => item.ManagedFileId == file)
            .OrderBy(item => item.ReferenceKey).Select(item => item.Id).ToListAsync();
        Assert.Equal(3, originalIds.Count);
        Assert.Equal(3, await db.ManagedFiles.Where(item => item.Id == file).Select(item => item.ReferenceCount).SingleAsync());

        // Rollback intentionally drops Phase 8 reference metadata, but preserves the active count
        // in the legacy schema so no deletion protection is lost. Reapplying reconstructs the same IDs.
        await migrator.MigrateAsync(previous);
        await using (var command = db.Database.GetDbConnection().CreateCommand())
        {
            command.CommandText = "SELECT ReferenceCount FROM managed_files WHERE Id=$id";
            command.Parameters.Add(new SqliteParameter("$id", file));
            if (command.Connection!.State != System.Data.ConnectionState.Open) await command.Connection.OpenAsync();
            Assert.Equal(3L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        }
        await migrator.MigrateAsync();
        var reconstructedIds = await db.ManagedFileReferences.Where(item => item.ManagedFileId == file)
            .OrderBy(item => item.ReferenceKey).Select(item => item.Id).ToListAsync();
        Assert.Equal(originalIds, reconstructedIds);
    }

    [Fact]
    public async Task SqliteMigration_RejectsArtifactManagedFileOutsideExactScope()
    {
        var workspaceId = Guid.CreateVersion7();
        var workspaceKey = Guid.NewGuid().ToString("N");
        var verifiedArtifactId = Guid.CreateVersion7();
        await using var db = await _factory.CreateDbContextAsync();
        db.ProviderDownloadWorkspaces.Add(new ProviderDownloadWorkspaceEntity
        {
            Id = workspaceId,
            WorkspaceId = workspaceKey,
            TenantId = _tenantA,
            OwnerUserId = _userA,
            LibraryScopeId = "music",
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
            TenantId = _tenantA,
            OwnerUserId = _userA,
            LibraryScopeId = "music",
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
            TenantId = _tenantA,
            OwnerUserId = _userA,
            LibraryScopeId = "music",
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

    private static PlatformUserRecord User(Guid id, Guid tenantId, string name, DateTimeOffset now) => new()
    {
        Id = id,
        TenantId = tenantId,
        DisplayName = name,
        Status = PlatformUserStatus.Active,
        CreatedAt = now,
        UpdatedAt = now
    };

    internal static DurableJobRecord Job(Guid id, Guid tenantId, Guid ownerUserId, string key, DateTimeOffset now) => new()
    {
        Id = id,
        ScopeKey = $"{tenantId:N}:{ownerUserId:N}",
        TenantId = tenantId,
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
        Guid id, Guid tenantId, Guid ownerUserId, Guid jobId, string suffix, DateTimeOffset now) => new()
        {
            Id = id,
            RootId = Guid.CreateVersion7(),
            TargetRootPath = $"/library/{suffix}",
            CanonicalPath = $"/library/{suffix}/{id:N}.flac",
            ContentSha256 = new string(suffix[0], 64),
            Length = 1,
            PlacementMethod = ManagedFilePlacementMethod.Copy,
            TenantId = tenantId,
            OwnerUserId = ownerUserId,
            SourceJobId = jobId,
            ScopeKey = $"{tenantId:N}:{ownerUserId:N}",
            ReferenceCount = 1,
            IsManaged = true,
            CreatedAt = now
        };

    private static MetadataEnrichmentPlanRecord Plan(
        Guid tenantId, Guid ownerUserId, Guid jobId, Guid fileId, string suffix) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
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

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }
}

using System.Collections.Concurrent;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace allstarr.Tests;

public sealed class ConcurrentRematchDecisionTests
{
    [Fact]
    [Trait("Category", "Postgres")]
    public async Task ConcurrentRematch_SameUniqueKey_OneWinsOthersConflictDeterministically()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var options = new DbContextOptionsBuilder<AllstarrDbContext>()
            .UseNpgsql(database.ConnectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using (var initDb = new AllstarrDbContext(options))
        {
            await initDb.Database.MigrateAsync();
        }

        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var backendIdentityId = Guid.NewGuid();
        var providerAccountId = Guid.NewGuid();
        var libraryTrackId = Guid.NewGuid();
        var externalSnapshotId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using (var setup = new AllstarrDbContext(options))
        {
            setup.Tenants.Add(new TenantRecord
            {
                Id = tenantId,
                Slug = $"concurrent-{Guid.NewGuid():N}",
                Name = "Concurrent tenant",
                CreatedAt = now
            });
            setup.Users.Add(new PlatformUserRecord
            {
                Id = userId,
                TenantId = tenantId,
                DisplayName = "concurrent",
                Status = PlatformUserStatus.Active,
                CreatedAt = now,
                UpdatedAt = now
            });
            setup.BackendIdentities.Add(new BackendIdentityRecord
            {
                Id = backendIdentityId,
                TenantId = tenantId,
                UserId = userId,
                BackendType = "jellyfin",
                BackendInstanceId = "concurrent",
                PrincipalId = "princ",
                CreatedAt = now,
                LastSeenAt = now
            });
            setup.ProviderAccounts.Add(new ProviderAccountRecord
            {
                Id = providerAccountId,
                TenantId = tenantId,
                OwnerUserId = userId,
                ProviderId = "spotify",
                DisplayName = "concurrent",
                Scope = ProviderAccountScope.User,
                Enabled = true,
                CreatedAt = now,
                UpdatedAt = now
            });
            setup.LibraryTracks.Add(new LibraryTrackRecord
            {
                Id = libraryTrackId,
                TenantId = tenantId,
                OwnerUserId = userId,
                BackendIdentityId = backendIdentityId,
                Title = "Concurrent track",
                Artist = "Concurrent artist",
                IndexedAt = now,
                SourceModifiedAt = now,
                UpdatedAt = now
            });
            var hash64 = new string('b', 64);
            setup.ExternalMetadataSnapshots.Add(new ExternalMetadataSnapshotRecord
            {
                Id = externalSnapshotId,
                TenantId = tenantId,
                OwnerUserId = userId,
                ProviderAccountId = providerAccountId,
                ProviderId = "spotify",
                ResourceKind = "track",
                ExternalIdHash = hash64,
                SnapshotVersion = 1,
                ProviderRevision = "1",
                PayloadJson = "{}",
                PayloadSha256 = hash64,
                RetrievedAt = now
            });
            await setup.SaveChangesAsync();
        }

        const int concurrentWriters = 4;
        const int decisionVersion = 1;
        var readyToWrite = new TaskCompletionSource();
        var successes = 0;
        var conflicts = 0;
        var otherErrors = new ConcurrentBag<Exception>();
        var tasks = Enumerable.Range(0, concurrentWriters).Select(async writerIndex =>
        {
            await using var ctx = new AllstarrDbContext(options);
            var record = new TrackMatchRecord
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                OwnerUserId = userId,
                LibraryScopeId = "default",
                ExternalSnapshotId = externalSnapshotId,
                LibraryTrackId = libraryTrackId,
                State = TrackMatchState.Accepted,
                Confidence = 0.9,
                Threshold = 0.85,
                DecisionVersion = decisionVersion,
                PolicyVersion = "v3",
                DecidedAt = now,
                Revision = 1
            };
            ctx.TrackMatches.Add(record);
            await readyToWrite.Task;
            try
            {
                await ctx.SaveChangesAsync();
                Interlocked.Increment(ref successes);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                Interlocked.Increment(ref conflicts);
            }
            catch (Exception ex)
            {
                otherErrors.Add(ex);
            }
        }).ToArray();

        await Task.Delay(50);
        readyToWrite.SetResult();
        await Task.WhenAll(tasks);

        Assert.Empty(otherErrors);
        Assert.Equal(1, successes);
        Assert.Equal(concurrentWriters - 1, conflicts);

        await using var verify = new AllstarrDbContext(options);
        var matches = await verify.TrackMatches
            .Where(m => m.TenantId == tenantId
                && m.OwnerUserId == userId
                && m.LibraryScopeId == "default"
                && m.ExternalSnapshotId == externalSnapshotId
                && m.DecisionVersion == decisionVersion)
            .ToListAsync();
        Assert.Single(matches);
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task ConcurrentRematch_DifferentDecisionVersions_AllSucceedOnPostgres()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var options = new DbContextOptionsBuilder<AllstarrDbContext>()
            .UseNpgsql(database.ConnectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using (var initDb = new AllstarrDbContext(options))
        {
            await initDb.Database.MigrateAsync();
        }

        var result = await RunDifferentVersionsAsync(options);
        Assert.Equal(5, result);
    }

    private static async Task<int> RunDifferentVersionsAsync(DbContextOptions<AllstarrDbContext> options)
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var backendIdentityId = Guid.NewGuid();
        var providerAccountId = Guid.NewGuid();
        var libraryTrackId = Guid.NewGuid();
        var externalSnapshotId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using (var setup = new AllstarrDbContext(options))
        {
            setup.Tenants.Add(new TenantRecord
            {
                Id = tenantId,
                Slug = $"parity-{Guid.NewGuid():N}",
                Name = "Parity tenant",
                CreatedAt = now
            });
            setup.Users.Add(new PlatformUserRecord
            {
                Id = userId,
                TenantId = tenantId,
                DisplayName = "parity",
                Status = PlatformUserStatus.Active,
                CreatedAt = now,
                UpdatedAt = now
            });
            setup.BackendIdentities.Add(new BackendIdentityRecord
            {
                Id = backendIdentityId,
                TenantId = tenantId,
                UserId = userId,
                BackendType = "jellyfin",
                BackendInstanceId = "parity",
                PrincipalId = "princ",
                CreatedAt = now,
                LastSeenAt = now
            });
            setup.ProviderAccounts.Add(new ProviderAccountRecord
            {
                Id = providerAccountId,
                TenantId = tenantId,
                OwnerUserId = userId,
                ProviderId = "spotify",
                DisplayName = "parity",
                Scope = ProviderAccountScope.User,
                Enabled = true,
                CreatedAt = now,
                UpdatedAt = now
            });
            setup.LibraryTracks.Add(new LibraryTrackRecord
            {
                Id = libraryTrackId,
                TenantId = tenantId,
                OwnerUserId = userId,
                BackendIdentityId = backendIdentityId,
                Title = "Parity track",
                Artist = "Parity artist",
                IndexedAt = now,
                SourceModifiedAt = now,
                UpdatedAt = now
            });
            var hash64 = new string('c', 64);
            setup.ExternalMetadataSnapshots.Add(new ExternalMetadataSnapshotRecord
            {
                Id = externalSnapshotId,
                TenantId = tenantId,
                OwnerUserId = userId,
                ProviderAccountId = providerAccountId,
                ProviderId = "spotify",
                ResourceKind = "track",
                ExternalIdHash = hash64,
                SnapshotVersion = 1,
                ProviderRevision = "1",
                PayloadJson = "{}",
                PayloadSha256 = hash64,
                RetrievedAt = now
            });
            await setup.SaveChangesAsync();
        }

        var readyToWrite = new TaskCompletionSource();
        var tasks = Enumerable.Range(1, 5).Select(async version =>
        {
            await using var ctx = new AllstarrDbContext(options);
            var record = new TrackMatchRecord
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                OwnerUserId = userId,
                LibraryScopeId = "default",
                ExternalSnapshotId = externalSnapshotId,
                LibraryTrackId = libraryTrackId,
                State = TrackMatchState.Accepted,
                Confidence = 0.95,
                Threshold = 0.85,
                DecisionVersion = version,
                PolicyVersion = "v3",
                DecidedAt = now,
                Revision = version
            };
            ctx.TrackMatches.Add(record);
            await readyToWrite.Task;
            await ctx.SaveChangesAsync();
        }).ToArray();

        await Task.Delay(50);
        readyToWrite.SetResult();
        await Task.WhenAll(tasks);

        await using var verify = new AllstarrDbContext(options);
        return await verify.TrackMatches
            .Where(m => m.TenantId == tenantId)
            .CountAsync();
    }

    private static bool IsUniqueViolation(Exception ex)
    {
        var message = ex.InnerException?.Message ?? ex.Message;
        return message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unique constraint", StringComparison.OrdinalIgnoreCase)
            || message.Contains("23505", StringComparison.OrdinalIgnoreCase);
    }
}

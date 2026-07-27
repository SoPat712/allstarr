using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed class ConcurrentRematchDecisionTests
{
    [Fact]
    [Trait("Category", "Postgres")]
    public async Task ConcurrentCommand_CoalescesDecisionAndSurvivesServiceRestart()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var factory = new DbFactory(database.Options);
        await using (var migrated = await factory.CreateDbContextAsync())
        {
            await migrated.Database.MigrateAsync();
        }

        var tenantId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        var backendIdentityId = Guid.CreateVersion7();
        var providerAccountId = Guid.CreateVersion7();
        var libraryTrackId = Guid.CreateVersion7();
        var externalSnapshotId = Guid.CreateVersion7();
        var now = new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);
        await using (var setup = await factory.CreateDbContextAsync())
        {
            setup.Tenants.Add(new TenantRecord
            {
                Id = tenantId,
                Slug = $"concurrent-{tenantId:N}",
                Name = "Concurrent tenant",
                CreatedAt = now
            });
            setup.Users.Add(new PlatformUserRecord
            {
                Id = userId,
                TenantId = tenantId,
                DisplayName = "Concurrent owner",
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
                BackendInstanceId = "backend",
                PrincipalId = "principal",
                CreatedAt = now,
                LastSeenAt = now
            });
            setup.ProviderAccounts.Add(new ProviderAccountRecord
            {
                Id = providerAccountId,
                TenantId = tenantId,
                OwnerUserId = userId,
                ProviderId = "spotify",
                DisplayName = "Spotify",
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
                LibraryScopeId = "music",
                Protocol = "jellyfin",
                BackendInstanceId = "backend",
                BackendItemId = "local-1",
                FilePath = "/music/concurrent.flac",
                Title = "Concurrent track",
                Artist = "Concurrent artist",
                DurationMilliseconds = 180_000,
                ProviderIdsJson = "{}",
                IndexedAt = now,
                SourceModifiedAt = now,
                UpdatedAt = now
            });
            var hash = new string('b', 64);
            setup.ExternalMetadataSnapshots.Add(new ExternalMetadataSnapshotRecord
            {
                Id = externalSnapshotId,
                TenantId = tenantId,
                OwnerUserId = userId,
                ProviderAccountId = providerAccountId,
                LibraryScopeId = "music",
                BackendInstanceId = "backend",
                ProviderId = "spotify",
                ResourceKind = "track",
                ExternalIdHash = hash,
                SnapshotVersion = 1,
                ProviderRevision = "1",
                PayloadJson = """
                    {"Title":"Concurrent track","Artist":"Concurrent artist","DurationMilliseconds":180000}
                    """,
                PayloadSha256 = hash,
                RetrievedAt = now
            });
            await setup.SaveChangesAsync();
        }

        var actor = new TrackMatchActor(tenantId, userId, false);
        var service = CreateService(factory, now);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(index =>
            service.RematchSnapshotAsync(actor, externalSnapshotId, $"concurrent-{index}")));

        Assert.All(results, result =>
        {
            Assert.True(result.Succeeded);
            Assert.Equal(1, result.DecisionVersion);
        });
        await using (var verify = await factory.CreateDbContextAsync())
        {
            var decision = Assert.Single(await verify.TrackMatches.ToListAsync());
            Assert.Equal(TrackMatchState.Accepted, decision.State);
            Assert.Equal(libraryTrackId, decision.LibraryTrackId);
        }

        var restarted = CreateService(factory, now.AddMinutes(1));
        var next = await restarted.RematchSnapshotAsync(
            actor, externalSnapshotId, "after-restart");

        Assert.True(next.Succeeded);
        Assert.Equal(2, next.DecisionVersion);
        await using var final = await factory.CreateDbContextAsync();
        Assert.Equal(2, await final.TrackMatches.CountAsync());
    }

    private static TrackMatchCommandService CreateService(
        DbFactory factory,
        DateTimeOffset now) =>
        new(
            factory,
            new TrackMatchDecisionEngine(),
            new ProviderAccountResolver(factory, new ProviderPolicyOptions()),
            new Clock(now));

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
}

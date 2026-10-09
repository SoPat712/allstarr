using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed class ConcurrentRematchDecisionTests
{
    [Fact]
    [Trait("Category", "Sqlite")]
    public async Task ConcurrentCommand_CoalescesDecisionAndSurvivesServiceRestart()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new DbFactory(database.Options);
        var userId = Guid.CreateVersion7();
        var providerAccountId = Guid.CreateVersion7();
        var libraryTrackId = Guid.CreateVersion7();
        var externalSnapshotId = Guid.CreateVersion7();
        var now = new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);
        await using (var setup = await factory.CreateDbContextAsync())
        {
            setup.Users.Add(new UserRecord
            {
                Id = userId,
                DisplayName = "Concurrent owner",
                Enabled = true,
                BackendType = "jellyfin",
                BackendInstanceId = "backend",
                BackendPrincipalId = "principal",
                CreatedAt = now,
                UpdatedAt = now
            });
            setup.ProviderAccounts.Add(new ProviderAccountRecord
            {
                Id = providerAccountId,
                OwnerUserId = userId,
                ProviderId = "spotify",
                DisplayName = "Spotify",
                Enabled = true,
                CreatedAt = now,
                UpdatedAt = now
            });
            setup.LibraryTracks.Add(new LibraryTrackRecord
            {
                Id = libraryTrackId,
                OwnerUserId = userId,
                Protocol = "jellyfin",
                BackendInstanceId = "backend",
                BackendLibraryId = "music",
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
                OwnerUserId = userId,
                ProviderAccountId = providerAccountId,
                BackendInstanceId = "backend",
                BackendPrincipalId = "principal",
                Protocol = "jellyfin",
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

        var actor = new TrackMatchActor(userId, false);
        var service = CreateService(factory, now);
        using var gate = new Barrier(8);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(index =>
            Task.Factory.StartNew(() =>
            {
                Assert.True(gate.SignalAndWait(TimeSpan.FromSeconds(15)));
                return service.RematchSnapshotAsync(actor, externalSnapshotId, $"concurrent-{index}");
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap()));

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
            new ProviderAccountResolver(factory),
            new Clock(now), new TestBackendLibraryAccess(factory, "music"));

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

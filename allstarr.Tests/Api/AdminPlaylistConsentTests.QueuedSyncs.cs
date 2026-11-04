using System.Text.Json;
using allstarr.Core.Jobs;
using allstarr.Core.Playlists;
using allstarr.Core.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class AdminPlaylistConsentTests
{
    [Fact]
    public async Task JellyfinConsentQueuesOnlyEnabledOwnedLinkedPlaylistsNeedingWriteAccess()
    {
        var alice = (await _identities.ResolveAsync(new("jellyfin", "alice-id", "Alice")))!;
        var bob = (await _identities.ResolveAsync(new("jellyfin", "bob-id", "Bob")))!;
        var eligible = await SeedConsentLinkAsync(alice.UserId, "order,removal");
        var expired = await SeedConsentLinkAsync(alice.UserId, "credential_expired");
        await SeedConsentLinkAsync(alice.UserId, "order", enabled: false);
        await SeedConsentLinkAsync(alice.UserId, "order", mode: PlaylistImportMode.OneTime);
        await SeedConsentLinkAsync(bob.UserId, "order");
        await SeedConsentLinkAsync(alice.UserId, "order", protocol: "subsonic");
        await SeedConsentLinkAsync(alice.UserId, "removal");
        await SeedConsentLinkAsync(alice.UserId, "reorder");
        await SeedConsentLinkAsync(alice.UserId, null);
        var recovered = await SeedConsentLinkAsync(alice.UserId, "order");
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var old = await db.PlaylistSyncRuns.SingleAsync(item => item.PlaylistLinkId == recovered.Id);
            db.PlaylistSyncRuns.Add(new PlaylistSyncRunRecord
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = alice.UserId,
                PlaylistLinkId = recovered.Id,
                PlaylistSourceSnapshotId = old.PlaylistSourceSnapshotId,
                Generation = 2,
                IdempotencyKey = "recovered",
                State = PlaylistSyncState.Succeeded,
                RuleVersion = "rules",
                StartedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }
        string schedulesBefore;
        await using (var db = await _factory.CreateDbContextAsync())
            schedulesBefore = JsonSerializer.Serialize(await db.JobSchedules.OrderBy(item => item.Id).ToArrayAsync());
        var session = await _sessions.CreateSessionAsync("alice-id", "Alice", false, "session-token", null,
            backendType: "Jellyfin", allstarrUserId: alice.UserId);
        var controller = Controller(session.SessionId, "Jellyfin");
        var result = Assert.IsType<OkObjectResult>(await controller.GrantPlaylistConsent(new() { Password = "fixture" }, default));
        var value = JsonSerializer.SerializeToElement(result.Value);
        Assert.True(value.GetProperty("supported").GetBoolean());
        Assert.True(value.GetProperty("granted").GetBoolean());
        Assert.False(value.GetProperty("needsSignIn").GetBoolean());
        Assert.Equal(2, value.GetProperty("queuedSyncs").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, value.GetProperty("updatedAt").ValueKind);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var jobs = await db.Jobs.ToArrayAsync();
            Assert.Equal(2, jobs.Length);
            Assert.Equal(new[] { eligible.Id, expired.Id }.Order(), jobs.Select(item =>
                JsonSerializer.Deserialize<PlaylistMaterializationJobPayload>(item.PayloadJson)!.PlaylistLinkId).Order());
            Assert.All(jobs, job =>
            {
                Assert.Equal("playlist.materialize", job.Type);
                Assert.Equal(alice.UserId, job.OwnerUserId);
                Assert.Equal("playlist", job.ProviderCapability);
                Assert.Equal(job.IdempotencyKey.Contains(eligible.Id.ToString("N"), StringComparison.Ordinal)
                    ? eligible.ProviderAccountId : expired.ProviderAccountId, job.ProviderAccountId);
                Assert.StartsWith("manual:", job.IdempotencyKey);
                Assert.Equal(DurableJobState.Pending, job.State);
                Assert.Null(JsonSerializer.Deserialize<PlaylistMaterializationJobPayload>(job.PayloadJson)!.SourceSnapshotId);
            });
            Assert.Equal(schedulesBefore, JsonSerializer.Serialize(await db.JobSchedules.OrderBy(item => item.Id).ToArrayAsync()));
        }
        var repeated = Assert.IsType<OkObjectResult>(await controller.GrantPlaylistConsent(new() { Password = "fixture" }, default));
        Assert.Equal(0, JsonSerializer.SerializeToElement(repeated.Value).GetProperty("queuedSyncs").GetInt32());
        await using var after = await _factory.CreateDbContextAsync();
        Assert.Equal(2, await after.Jobs.CountAsync());
    }

    [Theory]
    [InlineData(DurableJobState.Pending)]
    [InlineData(DurableJobState.Running)]
    [InlineData(DurableJobState.RetryScheduled)]
    public async Task JellyfinConsentReusesActiveMaterialization(DurableJobState state)
    {
        var alice = (await _identities.ResolveAsync(new("jellyfin", "alice-id", "Alice")))!;
        var link = await SeedConsentLinkAsync(alice.UserId, "order");
        var queued = await _jobs.EnqueueAsync(new DurableJobEnqueueRequest<PlaylistMaterializationJobPayload>(
            "playlist.materialize", "scheduled-fixture", new(link.Id, 123), alice.UserId,
            ProviderAccountId: link.ProviderAccountId, Capability: "playlist"));
        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.Jobs.SingleAsync()).State = state;
            await db.SaveChangesAsync();
        }
        var session = await _sessions.CreateSessionAsync("alice-id", "Alice", false, "session-token", null,
            backendType: "Jellyfin", allstarrUserId: alice.UserId);
        var result = Assert.IsType<OkObjectResult>(await Controller(session.SessionId, "Jellyfin")
            .GrantPlaylistConsent(new() { Password = "fixture" }, default));
        Assert.Equal(0, JsonSerializer.SerializeToElement(result.Value).GetProperty("queuedSyncs").GetInt32());
        await using var after = await _factory.CreateDbContextAsync();
        Assert.Equal(queued.JobId, (await after.Jobs.SingleAsync()).Id);
        Assert.Equal(state, (await after.Jobs.SingleAsync()).State);
    }

    [Fact]
    public async Task JellyfinConsentDoesNotQueueBeforeSuccessfulReauthentication()
    {
        var alice = (await _identities.ResolveAsync(new("jellyfin", "alice-id", "Alice")))!;
        await SeedConsentLinkAsync(alice.UserId, "order");
        var session = await _sessions.CreateSessionAsync("alice-id", "Alice", false, "session-token", null,
            backendType: "Jellyfin", allstarrUserId: alice.UserId);
        _backend.Accept = false;
        Assert.IsType<UnauthorizedObjectResult>(await Controller(session.SessionId, "Jellyfin")
            .GrantPlaylistConsent(new() { Password = "fixture" }, default));
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.Jobs.ToArrayAsync());
        Assert.Null(await _secrets.GetJellyfinPlaylistGrantAsync(alice));
    }

    [Theory]
    [InlineData(DurableJobState.Succeeded)]
    [InlineData(DurableJobState.Failed)]
    [InlineData(DurableJobState.Cancelled)]
    public async Task JellyfinConsentQueuesAnotherRunAfterTerminalMaterialization(DurableJobState state)
    {
        var alice = (await _identities.ResolveAsync(new("jellyfin", "alice-id", "Alice")))!;
        var link = await SeedConsentLinkAsync(alice.UserId, "order");
        await _jobs.EnqueueManualPlaylistMaterializationAsync(link, 123);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.Jobs.SingleAsync()).State = state;
            await db.SaveChangesAsync();
        }
        var session = await _sessions.CreateSessionAsync("alice-id", "Alice", false, "session-token", null,
            backendType: "Jellyfin", allstarrUserId: alice.UserId);
        var result = Assert.IsType<OkObjectResult>(await Controller(session.SessionId, "Jellyfin")
            .GrantPlaylistConsent(new() { Password = "fixture" }, default));
        Assert.Equal(1, JsonSerializer.SerializeToElement(result.Value).GetProperty("queuedSyncs").GetInt32());
        await using var after = await _factory.CreateDbContextAsync();
        Assert.Equal(2, await after.Jobs.CountAsync());
    }

    [Theory]
    [InlineData(PlaylistImportMode.OneTime, "jellyfin", true)]
    [InlineData(PlaylistImportMode.Linked, "subsonic", true)]
    [InlineData(PlaylistImportMode.Linked, "jellyfin", false)]
    public async Task ManualRunsOutsideEnabledLinkedJellyfinKeepDistinctGenerations(
        PlaylistImportMode mode, string protocol, bool enabled)
    {
        var alice = (await _identities.ResolveAsync(new("jellyfin", "alice-id", "Alice")))!;
        var link = await SeedConsentLinkAsync(alice.UserId, "order", enabled, mode, protocol);
        var first = await _jobs.EnqueueManualPlaylistMaterializationAsync(link, 123);
        var second = await _jobs.EnqueueManualPlaylistMaterializationAsync(link, 124);
        Assert.True(first.Created);
        Assert.True(second.Created);
        Assert.NotEqual(first.JobId, second.JobId);
        await using var db = await _factory.CreateDbContextAsync();
        var jobs = await db.Jobs.ToArrayAsync();
        Assert.Equal(new long[] { 123, 124 }, jobs.Select(job =>
            JsonSerializer.Deserialize<PlaylistMaterializationJobPayload>(job.PayloadJson)!.Generation).Order());
    }

    [Theory]
    [InlineData(PlaylistSyncState.Pending, false)]
    [InlineData(PlaylistSyncState.Running, false)]
    [InlineData(PlaylistSyncState.Pending, true)]
    [InlineData(PlaylistSyncState.Running, true)]
    public async Task JellyfinConsentUsesLatestTerminalRunOfLatestPublishedSnapshot(
        PlaylistSyncState currentState, bool publishNewerSnapshot)
    {
        var alice = (await _identities.ResolveAsync(new("jellyfin", "alice-id", "Alice")))!;
        var link = await SeedConsentLinkAsync(alice.UserId, "order");
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var published = await db.PlaylistSourceSnapshots.SingleAsync();
            db.PlaylistSyncRuns.Add(new PlaylistSyncRunRecord
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = alice.UserId,
                PlaylistLinkId = link.Id,
                PlaylistSourceSnapshotId = published.Id,
                Generation = 2,
                IdempotencyKey = "current",
                State = currentState,
                RuleVersion = "rules",
                StartedAt = DateTimeOffset.UtcNow
            });
            var newer = new PlaylistSourceSnapshotRecord
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = alice.UserId,
                PlaylistLinkId = link.Id,
                SnapshotVersion = 2,
                Name = "Newer",
                ProviderRevision = "newer",
                PayloadSha256 = new string('b', 64),
                CorrelationId = "newer",
                RetrievedAt = DateTimeOffset.UtcNow,
                PublishedAt = publishNewerSnapshot ? DateTimeOffset.UtcNow : null
            };
            db.PlaylistSourceSnapshots.Add(newer);
            db.PlaylistSyncRuns.Add(new PlaylistSyncRunRecord
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = alice.UserId,
                PlaylistLinkId = link.Id,
                PlaylistSourceSnapshotId = newer.Id,
                Generation = 3,
                IdempotencyKey = "newer",
                State = PlaylistSyncState.Succeeded,
                RuleVersion = "rules",
                StartedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }
        var session = await _sessions.CreateSessionAsync("alice-id", "Alice", false, "session-token", null,
            backendType: "Jellyfin", allstarrUserId: alice.UserId);
        var result = Assert.IsType<OkObjectResult>(await Controller(session.SessionId, "Jellyfin")
            .GrantPlaylistConsent(new() { Password = "fixture" }, default));
        Assert.Equal(publishNewerSnapshot ? 0 : 1,
            JsonSerializer.SerializeToElement(result.Value).GetProperty("queuedSyncs").GetInt32());
    }

    private async Task<PlaylistLinkRecord> SeedConsentLinkAsync(Guid owner, string? code,
        bool enabled = true, PlaylistImportMode mode = PlaylistImportMode.Linked, string protocol = "jellyfin")
    {
        var now = DateTimeOffset.UtcNow;
        var account = new ProviderAccountRecord
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = owner,
            ProviderId = "spotify",
            Enabled = true,
            DisplayName = "Fixture",
            CreatedAt = now,
            UpdatedAt = now
        };
        var schedule = new JobScheduleRecord
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = owner,
            JobType = "playlist.materialize",
            CronExpression = "0 0 * * *",
            Enabled = enabled,
            NextRunAt = now.AddDays(1),
            CreatedAt = now,
            UpdatedAt = now,
            Revision = 4
        };
        var link = new PlaylistLinkRecord
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = owner,
            ProviderAccountId = account.Id,
            ScheduleId = mode == PlaylistImportMode.Linked ? schedule.Id : null,
            SourceProviderId = "spotify",
            SourcePlaylistId = Guid.NewGuid().ToString("N"),
            SourcePlaylistIdHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            TargetProtocol = protocol,
            TargetBackendInstanceId = "primary",
            ImportMode = mode,
            Enabled = enabled,
            RuleVersion = "rules",
            PolicyVersion = "policy",
            CreatedAt = now,
            UpdatedAt = now
        };
        var snapshot = new PlaylistSourceSnapshotRecord
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = owner,
            PlaylistLinkId = link.Id,
            SnapshotVersion = 1,
            Name = "Fixture",
            ProviderRevision = "fixture",
            PayloadSha256 = new string('a', 64),
            CorrelationId = "fixture",
            RetrievedAt = now,
            PublishedAt = now
        };
        await using var db = await _factory.CreateDbContextAsync();
        db.ProviderAccounts.Add(account);
        db.JobSchedules.Add(schedule);
        db.PlaylistLinks.Add(link);
        db.PlaylistSourceSnapshots.Add(snapshot);
        db.PlaylistSyncRuns.Add(new PlaylistSyncRunRecord
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = owner,
            PlaylistLinkId = link.Id,
            PlaylistSourceSnapshotId = snapshot.Id,
            Generation = 1,
            IdempotencyKey = "initial",
            State = PlaylistSyncState.PartiallySucceeded,
            ConflictCode = code,
            RuleVersion = "rules",
            StartedAt = now
        });
        await db.SaveChangesAsync();
        return link;
    }
}

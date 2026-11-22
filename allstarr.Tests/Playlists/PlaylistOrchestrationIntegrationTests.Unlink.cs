using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.ClientApi;
using allstarr.Core.Jobs;
using allstarr.Core.Playlists;
using allstarr.Core.Storage;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class PlaylistOrchestrationIntegrationTests
{
    [Fact]
    public async Task UnlinkKeepsTheMediaServerPlaylistAndEndsTheLinkUntilItIsLinkedAgain()
    {
        await MarkCreatedDestinationAsync();
        _source.Snapshot = Snapshot("unlink", Entry(0, "one", "source-1", "One"));
        await _service.RunAsync(Context(), new(_link, 9201));
        var scheduleId = Guid.CreateVersion7();
        string target;
        int memberships;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var link = await db.PlaylistLinks.SingleAsync();
            target = link.TargetPlaylistId!;
            link.ScheduleId = scheduleId;
            db.JobSchedules.Add(new()
            {
                Id = scheduleId,
                OwnerUserId = _user,
                JobType = DurableScheduleEngine.PlaylistSyncJobType,
                CronExpression = "0 4 * * *",
                TimeZoneId = "UTC",
                Enabled = true,
                NextRunAt = _now.AddDays(1),
                CreatedAt = _now,
                UpdatedAt = _now
            });
            await db.SaveChangesAsync();
            memberships = await db.PlaylistTargetMemberships.CountAsync(item => item.Active);
        }
        var writes = _target.WriteCalls;
        var persistence = new PlaylistPersistenceService(_factory, new(_factory), new Clock(_now), _trackMatches,
            new TestBackendLibraryAccess(_factory, "music"));
        var controller = UnlinkController(persistence);

        var unlinked = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await controller.Unlink(_link, default)).Value);
        Assert.True(unlinked.GetProperty("unlinked").GetBoolean());
        Assert.Equal(1, unlinked.GetProperty("keptSongs").GetInt32());
        Assert.Equal(writes, _target.WriteCalls);
        Assert.Equal(0, _target.DeleteCalls);
        Assert.Null(_target.ArtworkOnlyBytes);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var link = await db.PlaylistLinks.SingleAsync();
            Assert.False(link.Enabled);
            Assert.Null(link.ScheduleId);
            Assert.Null(link.TargetPlaylistId);
            var schedule = await db.JobSchedules.SingleAsync(item => item.Id == scheduleId);
            Assert.False(schedule.Enabled);
            Assert.Null(schedule.NextRunAt);
            Assert.Single(await db.TrackMatches.ToArrayAsync());
            Assert.Equal(memberships, await db.PlaylistTargetMemberships.CountAsync(item => item.Active));
        }
        var list = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await controller.List(default)).Value);
        Assert.Equal(0, list.GetProperty("playlistLinks").GetArrayLength());
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.RunAsync(Context(), new(_link, 9202)));
        Assert.IsType<ConflictObjectResult>(await controller.SetState(_link, new(1, true), default));
        Assert.IsType<OkObjectResult>(await controller.Unlink(_link, default));
        Assert.Equal(writes, _target.WriteCalls);

        var relinked = JsonSerializer.SerializeToElement(Assert.IsType<CreatedAtActionResult>(await controller.Create(
            new(_account, "fixture", "playlist", "jellyfin", "backend", "hybrid", "reconcile"), default)).Value);
        Assert.Equal(_link, relinked.GetProperty("id").GetGuid());
        Assert.True(relinked.GetProperty("enabled").GetBoolean());
        Assert.Equal(target, relinked.GetProperty("targetPlaylistId").GetString());
        Assert.NotEqual(JsonValueKind.Null, relinked.GetProperty("initialJobId").ValueKind);
        await _service.RunAsync(Context(), new(_link, 9203));
        Assert.Equal(target, _target.LastWrite!.BackendPlaylistId);
        Assert.Equal(_target.LastWrite.OrderedBackendItemIds.Distinct().Count(), _target.LastWrite.OrderedBackendItemIds.Count);
        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Single(await verify.PlaylistLinks.ToArrayAsync());
        Assert.Equal(memberships, await verify.PlaylistTargetMemberships.CountAsync(item => item.Active));
        Assert.Null(await PlaylistUnlink.FindAsync(verify, await verify.PlaylistLinks.SingleAsync(), default));
    }

    [Fact]
    public async Task PlaylistLibraryAdditionsListStreamingOnlySongsAndQueueEachOnce()
    {
        DurablePlaylistEntryProjection Song(int position, string route, string? provider, string id, string? backendItem = null) =>
            new(position, Guid.CreateVersion7(), id, $"Song {id}", ["Artist"], "Album", null, null, null, null, null,
                backendItem, route, provider, []);
        var projection = new DurablePlaylistProjection(_link, Guid.CreateVersion7(), 1, "Mix", "fixture", "playlist", _account,
            "jellyfin", "target", null, _now, _now, PlaylistSyncState.Succeeded,
            [Song(3, "external", "deezer", "second"), Song(0, "local", "jellyfin", "l1", "local-1"), Song(1, "external", "deezer", "first"),
             Song(2, "unmatched", null, "missing"), Song(4, "external", "deezer", "first")]);
        var plan = PlaylistLibraryAdditions.Plan(projection);
        Assert.Equal(["ext-deezer-song-first", "ext-deezer-song-second"], plan.Songs.Select(item => item.ItemId));
        Assert.Equal((2, 1), (plan.Total, plan.Skipped));

        var options = new DurableJobOptions();
        var clock = new Clock(_now);
        var queue = new DurableJobQueue(_factory, options, new JobPayloadPolicy(options), clock);
        var added = new List<(string ItemId, string Key)>();
        var additions = new PlaylistLibraryAdditions(_factory, queue, clock, new TestBackendLibraryAccess(_factory, "music"),
            (_, item, key, _) =>
            {
                added.Add((item.ItemId, key));
                return Task.FromResult(new ClientLibraryAddition(Guid.CreateVersion7(), item.ItemId, item.Unit, "done", "Added to the library."));
            });
        var first = await additions.QueueAsync(_user, _link, plan, null);
        Assert.Equal((2, 0, 1), (first.Queued, first.AlreadyThere, first.Skipped));
        var second = await additions.QueueAsync(_user, _link, plan, null);
        Assert.Equal((0, 2, 1, (Guid?)null), (second.Queued, second.AlreadyThere, second.Skipped, second.JobId));

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var job = await db.Jobs.SingleAsync(item => item.Type == PlaylistLibraryAdditions.Type);
            var claim = new DurableJobClaim(job.Id, 1, job.Type, JsonDocument.Parse(job.PayloadJson).RootElement, _user, null, null,
                JsonDocument.Parse("{}").RootElement, "bulk-add");
            var execution = new DurableJobExecutionContext(claim, null!);
            for (var step = 0; step < 5 && (await additions.ExecuteAsync(execution, default)).Kind != DurableJobCompletionKind.Succeeded; step++) { }
        }
        Assert.Equal(["ext-deezer-song-first", "ext-deezer-song-second"], added.Select(item => item.ItemId));
        Assert.Equal(2, added.Select(item => item.Key).Distinct().Count());
        Assert.All(added, item => Assert.Equal(PlaylistLibraryAdditions.RequestKey(_link, item.ItemId), item.Key));
    }

    private PlaylistLinksController UnlinkController(IPlaylistPersistenceService persistence)
    {
        var clock = new Clock(_now);
        var options = new DurableJobOptions();
        var controller = new PlaylistLinksController(_factory, persistence,
            new(_factory, new TestBackendLibraryAccess(_factory, "music")), _trackMatches, _service,
            new DurableJobQueue(_factory, options, new JobPayloadPolicy(options), clock), null!, null!, null!, null!, null!, null!,
            clock, null!, new(_factory, clock), null!, null!, null!, null!);
        controller.ControllerContext = new() { HttpContext = new DefaultHttpContext() };
        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = "unlink-session",
            AllstarrUserId = _user,
            UserId = "principal",
            UserName = "Owner",
            BackendType = "Jellyfin",
            BackendInstanceId = "backend",
            IsAdministrator = false,
            JellyfinAccessToken = "fixture",
            ExpiresAtUtc = _now.AddHours(1).UtcDateTime
        };
        return controller;
    }
}

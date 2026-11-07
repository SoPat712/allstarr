using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Identity;
using allstarr.Core.Jobs;
using allstarr.Core.Playlists;
using allstarr.Core.Playlists.Targets;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed partial class PlaylistOrchestrationIntegrationTests
{
    [Fact]
    public async Task PlaylistRemovalWaitsForActiveWritesAndPreventsLaterSyncs()
    {
        await MarkCreatedDestinationAsync();
        _source.Snapshot = Snapshot("active-write", Entry(0, "one", "source-1", "One"));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _target.WriteStarted = started;
        _target.WriteBlock = release.Task;
        var running = _service.RunAsync(Context(), new(_link, 9105));
        await started.Task;
        var removal = _service.RemoveFromAppsAsync(Context(), _link, 0);
        Assert.False(removal.IsCompleted);
        release.SetResult();
        await running;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => removal);
        Assert.Equal(0, _target.DeleteCalls);
        await using var db = await _factory.CreateDbContextAsync();
        var link = await db.PlaylistLinks.SingleAsync();
        _target.DeleteStatus = BackendPlaylistTargetStatus.Success;
        await _service.RemoveFromAppsAsync(Context(), _link, link.Revision);
        Assert.Equal(1, _target.DeleteCalls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.RunAsync(Context(), new(_link, 9106)));
        Assert.Equal(1, _target.WriteCalls);
    }

    [Fact]
    public async Task PlaylistImportRestoresTheRetainedLinkAndSavedSettings()
    {
        await MarkCreatedDestinationAsync();
        await using (var setup = await _factory.CreateDbContextAsync())
        {
            (await setup.PlaylistLinks.SingleAsync()).TrackRetention = PlaylistTrackRetention.KeepAll;
            await setup.SaveChangesAsync();
        }
        _source.Snapshot = Snapshot("import-again", Entry(0, "one", "source-1", "One"));
        await _service.RunAsync(Context(), new(_link, 9104));
        var backend = new BackendPlaylistTargetTests.JellyfinFakeBackend("target-created", "Provider Mix", ["local-1"]);
        var service = JellyfinService(backend);
        await using var before = await _factory.CreateDbContextAsync();
        var link = await before.PlaylistLinks.SingleAsync();
        await service.RemoveFromAppsAsync(Context(), _link, link.Revision);
        var persistence = new PlaylistPersistenceService(_factory, new(_factory), new Clock(_now), _trackMatches,
            new TestBackendLibraryAccess(_factory, "music"));
        var controller = AppController(service, persistence);
        var result = Assert.IsType<OkObjectResult>(await controller.Create(new(_account, "fixture", "playlist",
            "jellyfin", "backend", "hybrid", "reconcile"), default));
        var restored = JsonSerializer.SerializeToElement(result.Value);
        Assert.Equal(_link, restored.GetProperty("id").GetGuid());
        Assert.True(restored.GetProperty("enabled").GetBoolean());
        Assert.Equal("keepAll", restored.GetProperty("trackRetention").GetString());
        Assert.Equal("p2", restored.GetProperty("targetPlaylistId").GetString());
        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Single(await verify.PlaylistLinks.ToArrayAsync());
        Assert.Single(await verify.TrackMatches.ToArrayAsync());
        Assert.Single(await verify.PlaylistSourceSnapshots.ToArrayAsync());
        Assert.Single(await verify.AuditEvents.Where(item => item.Action == "added-to-apps").ToArrayAsync());
    }

    [Fact]
    public async Task PlaylistAppActionsDenyCrossUserRequestsAndResumeRequiresReadd()
    {
        await MarkCreatedDestinationAsync();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.PlaylistLinks.SingleAsync()).TargetPlaylistId = "target-created";
            await db.SaveChangesAsync();
        }
        _target.DeleteStatus = BackendPlaylistTargetStatus.Success;
        var controller = AppController(_service);
        Assert.IsType<UnauthorizedObjectResult>(await WithoutSession());
        foreach (var administrator in new[] { false, true })
        {
            SetSession(Guid.CreateVersion7(), administrator);
            Assert.Equal(403, Assert.IsType<ObjectResult>(await controller.RemoveFromApps(_link, new(0), default)).StatusCode);
            Assert.Equal(403, Assert.IsType<ObjectResult>(await controller.AddToApps(_link, new(0), default)).StatusCode);
        }
        SetSession(_user, false);
        var removed = Assert.IsType<OkObjectResult>(await controller.RemoveFromApps(_link, new(0), default));
        var revision = JsonSerializer.SerializeToElement(removed.Value).GetProperty("revision").GetInt64();
        Assert.IsType<ConflictObjectResult>(await controller.SetState(_link, new(revision, true), default));
        var restored = Assert.IsType<OkObjectResult>(await controller.AddToApps(_link, new(revision), default));
        Assert.True(JsonSerializer.SerializeToElement(restored.Value).GetProperty("enabled").GetBoolean());

        async Task<IActionResult> WithoutSession()
        {
            controller.HttpContext.Items.Clear();
            return await controller.RemoveFromApps(_link, new(0), default);
        }
        void SetSession(Guid owner, bool administrator) => controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = "apps-session",
            AllstarrUserId = owner,
            UserId = "principal",
            UserName = "Owner",
            BackendType = "Jellyfin",
            BackendInstanceId = "backend",
            IsAdministrator = administrator,
            JellyfinAccessToken = "fixture",
            ExpiresAtUtc = _now.AddHours(1).UtcDateTime
        };
    }

    private PlaylistLinksController AppController(PlaylistOrchestrationService service, IPlaylistPersistenceService? persistence = null)
    {
        var clock = new Clock(_now);
        var controller = new PlaylistLinksController(_factory, persistence!,
            new(_factory, new TestBackendLibraryAccess(_factory, "music")), _trackMatches, service,
            null!, null!, null!, null!, null!, null!, null!, clock, null!, new(_factory, clock), null!, null!, null!, null!);
        controller.ControllerContext = new() { HttpContext = new DefaultHttpContext() };
        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = "apps-session",
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

    [Fact]
    public async Task PlaylistRemovalKeepsSourceMatchesOverridesAndOtherListeners()
    {
        await MarkCreatedDestinationAsync();
        _source.Snapshot = Snapshot("remove-source", Entry(0, "one", "source-1", "One"));
        await _service.RunAsync(Context(), new(_link, 9101));
        var backend = new BackendPlaylistTargetTests.JellyfinFakeBackend("target-created", "Provider Mix", ["local-1"]);
        var other = Guid.CreateVersion7();
        var otherLink = Guid.CreateVersion7();
        var scheduleId = Guid.CreateVersion7();
        long revision;
        Guid matchId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var link = await db.PlaylistLinks.SingleAsync();
            link.ScheduleId = scheduleId;
            revision = link.Revision;
            matchId = (await db.TrackMatches.SingleAsync()).Id;
            db.ManualTrackOverrides.Add(Override((await db.ExternalMetadataSnapshots.SingleAsync()).Id,
                ManualOverrideDecision.Pin, _trackOne));
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
            db.Users.Add(new()
            {
                Id = other,
                BackendType = "jellyfin",
                BackendInstanceId = "backend",
                BackendPrincipalId = "other",
                DisplayName = "Other listener",
                Enabled = true,
                CreatedAt = _now,
                UpdatedAt = _now,
                LastSeenAt = _now
            });
            var copy = Link();
            copy.Id = otherLink;
            copy.OwnerUserId = other;
            copy.ProviderAccountId = null;
            copy.TargetPlaylistId = "other-copy";
            db.PlaylistLinks.Add(copy);
            await db.SaveChangesAsync();
        }
        var service = JellyfinService(backend);
        var removed = await service.RemoveFromAppsAsync(Context(), _link, revision);
        Assert.False(removed.Enabled);
        Assert.Null(removed.TargetPlaylistId);
        Assert.Empty(backend.Playlists);
        Assert.Contains(backend.Requests, item => item.Path == "Items/target-created");
        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Equal(2, await verify.PlaylistLinks.CountAsync());
        Assert.True((await verify.PlaylistLinks.SingleAsync(item => item.Id == otherLink)).Enabled);
        Assert.Equal("other-copy", (await verify.PlaylistLinks.SingleAsync(item => item.Id == otherLink)).TargetPlaylistId);
        Assert.Equal(matchId, (await verify.TrackMatches.SingleAsync()).Id);
        Assert.Single(await verify.ManualTrackOverrides.ToArrayAsync());
        Assert.Single(await verify.PlaylistSourceSnapshots.ToArrayAsync());
        Assert.False((await verify.JobSchedules.SingleAsync()).Enabled);
        Assert.Null((await verify.JobSchedules.SingleAsync()).NextRunAt);
        Assert.All(await verify.PlaylistTargetMemberships.ToArrayAsync(), item =>
        {
            Assert.False(item.Active);
            Assert.NotNull(item.RemovalConfirmedAt);
        });
        var audit = await verify.AuditEvents.SingleAsync(item => item.Action == "removed-from-apps");
        Assert.Equal(_user, audit.ActorUserId);
        Assert.Equal("Provider Mix was removed from your apps. Its source and saved matches were kept.",
            JsonSerializer.Deserialize<JsonElement>(audit.DetailsJson).GetProperty("message").GetString());
        var virtualized = new PlaylistVirtualizationService(_factory,
            new(_factory, new TestBackendLibraryAccess(_factory, "music")), new TestBackendLibraryAccess(_factory, "music"));
        Assert.Null(await virtualized.ReadAsync(Context(), PlaylistVirtualizationService.CreateProtocolId(_link)));
    }

    [Fact]
    public async Task PlaylistReaddRestoresSnapshotOrderManualSongsMetadataAndSyncOwnership()
    {
        await MarkCreatedDestinationAsync();
        _source.Snapshot = Snapshot("restore-source", Entry(0, "one", "source-1", "One"));
        await _service.RunAsync(Context(), new(_link, 9102));
        var backend = new BackendPlaylistTargetTests.JellyfinFakeBackend("target-created", "My name", ["manual", "local-1", "manual", "local-2"])
        { ExistingArtwork = [1, 2, 3], UseArtworkHashTag = true, EmptyNewPlaylistArtwork = true };
        backend.Playlists["target-created"].Description = "My description";
        var service = JellyfinService(backend);
        long revision;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var link = await db.PlaylistLinks.SingleAsync();
            var schedule = new JobScheduleRecord
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = _user,
                JobType = DurableScheduleEngine.PlaylistSyncJobType,
                CronExpression = "0 4 * * *",
                TimeZoneId = "UTC",
                Enabled = true,
                NextRunAt = _now.AddDays(1),
                CreatedAt = _now,
                UpdatedAt = _now
            };
            db.JobSchedules.Add(schedule);
            link.ScheduleId = schedule.Id;
            revision = link.Revision;
            await db.SaveChangesAsync();
        }
        var preview = await service.PreviewAppRemovalAsync(Context(), _link);
        Assert.Equal(["manual", "manual", "local-2"], preview.UserAddedSongs.Select(item => item.BackendItemId));
        var sourceCalls = _source.CollectCalls;
        var removed = await service.RemoveFromAppsAsync(Context(), _link, revision);
        await using var captured = await _factory.CreateDbContextAsync();
        var snapshot = await captured.ExternalMetadataSnapshots.SingleAsync(item => item.ResourceKind == "playlist-app-removal" && item.ProviderRevision == "removed");
        var payloadBefore = snapshot.PayloadJson;
        var saved = JsonSerializer.Deserialize<PlaylistAppSnapshot>(payloadBefore)!;
        Assert.Equal(["manual", "local-1", "manual", "local-2"], saved.Playlist.Members.Select(item => item.BackendItemId));
        Assert.Equal([false, true, false, false], saved.Entries.Select(item => item.AddedBySync));
        Assert.Equal("My name", saved.Playlist.Name);
        Assert.Equal("My description", saved.Playlist.Description);
        Assert.NotNull(saved.Playlist.ArtworkReference);
        var restored = await service.RestoreToAppsAsync(Context(), _link, removed.Revision);
        Assert.True(restored.Link.Enabled);
        Assert.Equal(_link, restored.Link.Id);
        Assert.Null(restored.JobId);
        Assert.Equal(0, restored.SkippedItemCount);
        Assert.Equal("p2", restored.Link.TargetPlaylistId);
        await using var check = await _factory.CreateDbContextAsync();
        var matchIds = await check.TrackMatches.Select(item => item.Id).ToArrayAsync();
        Assert.Equal(sourceCalls, _source.CollectCalls);
        Assert.Equal(matchIds, await check.TrackMatches.Select(item => item.Id).ToArrayAsync());
        Assert.Single(await check.PlaylistLinks.ToArrayAsync());
        Assert.Single(await check.AuditEvents.Where(item => item.Action == "added-to-apps").ToArrayAsync());
        Assert.Equal(["manual", "local-1", "manual", "local-2"], backend.Playlists.Values.Single().Members);
        Assert.Equal("My name", backend.Playlists.Values.Single().Name);
        Assert.Equal("My description", backend.Playlists.Values.Single().Description);
        Assert.Equal(new byte[] { 1, 2, 3 }, backend.ExistingArtwork);
        Assert.True((await check.PlaylistTargetMemberships.SingleAsync()).Active);
        Assert.True((await check.JobSchedules.SingleAsync()).Enabled);
        Assert.NotNull((await check.JobSchedules.SingleAsync()).NextRunAt);
        Assert.Equal(payloadBefore, (await check.ExternalMetadataSnapshots.AsNoTracking().SingleAsync(item => item.Id == snapshot.Id)).PayloadJson);
        var current = await check.PlaylistLinks.AsNoTracking().SingleAsync();
        var removedAgain = await service.RemoveFromAppsAsync(Context(), _link, current.Revision);
        await service.RestoreToAppsAsync(Context(), _link, removedAgain.Revision);
        Assert.Equal(6, await check.ExternalMetadataSnapshots.CountAsync(item => item.ResourceKind == "playlist-app-removal"));
        Assert.Equal(matchIds, await check.TrackMatches.Select(item => item.Id).ToArrayAsync());
        Assert.True((await check.PlaylistLinks.AsNoTracking().SingleAsync()).Enabled);
        var resumed = await service.RunAsync(Context(), new(_link, 9107));
        Assert.True(resumed.State is PlaylistSyncState.Succeeded or PlaylistSyncState.PartiallySucceeded);
        Assert.Equal(sourceCalls + 1, _source.CollectCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlaylistRemovalAndReaddRejectOtherListenersEvenForAdministrators(bool administrator)
    {
        var backend = new BackendPlaylistTargetTests.JellyfinFakeBackend("native", "Native playlist", []);
        var outsider = Guid.CreateVersion7();
        var context = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "backend", "other",
            new AllstarrPrincipal(outsider, "jellyfin", "backend", "other", "Other", administrator),
            "remove-other", _now.AddMinutes(5), default);
        var service = JellyfinService(backend);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RemoveFromAppsAsync(context, _link, 0));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RestoreToAppsAsync(context, _link, 0));
        Assert.Empty(backend.Requests);
    }

    [Theory]
    [InlineData(PlaylistDestinationMode.New)]
    [InlineData(PlaylistDestinationMode.Existing)]
    public async Task PlaylistRemovalAllowsOwnedSyncedDestinationsAndListAgrees(PlaylistDestinationMode destinationMode)
    {
        var backend = new BackendPlaylistTargetTests.JellyfinFakeBackend("native", "Native playlist", []);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var link = await db.PlaylistLinks.SingleAsync();
            link.TargetPlaylistId = "native";
            link.DestinationMode = destinationMode;
            link.ProviderAccountId = null;
            await db.SaveChangesAsync();
        }
        Assert.True(await ListedCanRemoveAsync());
        var removed = await JellyfinService(backend).RemoveFromAppsAsync(Context(), _link, 0);
        Assert.False(removed.Enabled);
        Assert.Null(removed.TargetPlaylistId);
        Assert.False(await ListedCanRemoveAsync());
    }

    [Fact]
    public async Task PlaylistRemovalRequiresAnOwnedSourceLink()
    {
        var controller = AppController(_service);
        var nativeId = Guid.CreateVersion7();
        Assert.IsType<NotFoundResult>(await controller.PreviewAppRemoval(nativeId, default));
        Assert.IsType<NotFoundResult>(await controller.RemoveFromApps(nativeId, new(0), default));
        Assert.Equal(0, _target.TotalCalls);
        Assert.Equal(0, _target.DeleteCalls);
    }

    [Fact]
    public async Task PlaylistRemovalRejectsStaleRevisions()
    {
        var backend = new BackendPlaylistTargetTests.JellyfinFakeBackend("native", "Native playlist", []);
        await using var setup = await _factory.CreateDbContextAsync();
        var owned = await setup.PlaylistLinks.SingleAsync();
        owned.DestinationMode = PlaylistDestinationMode.New;
        owned.Revision = 1;
        await setup.SaveChangesAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            JellyfinService(backend).RemoveFromAppsAsync(Context(), _link, 0));
        Assert.Empty(backend.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlaylistRemovalRefusesSharedDestinationsAcrossOwnersAndListAgrees(bool otherOwner)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var link = await db.PlaylistLinks.SingleAsync();
        link.ProviderAccountId = null;
        link.TargetPlaylistId = "shared";
        var copy = Link();
        copy.Id = Guid.CreateVersion7();
        copy.SourcePlaylistIdHash = new string('c', 64);
        copy.TargetPlaylistId = "shared";
        if (otherOwner)
        {
            var user = new UserRecord
            {
                Id = Guid.CreateVersion7(),
                BackendType = "jellyfin",
                BackendInstanceId = "backend",
                BackendPrincipalId = "other",
                DisplayName = "Other",
                Enabled = true,
                CreatedAt = _now,
                UpdatedAt = _now,
                LastSeenAt = _now
            };
            db.Users.Add(user);
            copy.OwnerUserId = user.Id;
            copy.ProviderAccountId = null;
        }
        db.PlaylistLinks.Add(copy);
        await db.SaveChangesAsync();
        Assert.False(await ListedCanRemoveAsync());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.RemoveFromAppsAsync(Context(), _link, 0));
        Assert.Contains("Another listener", error.Message);
        Assert.Equal(0, _target.TotalCalls);
        Assert.Equal(0, _target.DeleteCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PlaylistRemovalRefusesUnsyncedImportsAndUnassignedDestinations(bool oneTime)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var link = await db.PlaylistLinks.SingleAsync();
        link.ProviderAccountId = null;
        link.ImportMode = oneTime ? PlaylistImportMode.OneTime : PlaylistImportMode.Linked;
        link.TargetPlaylistId = oneTime ? "native" : null;
        await db.SaveChangesAsync();
        Assert.False(await ListedCanRemoveAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.RemoveFromAppsAsync(Context(), _link, 0));
        Assert.Equal(0, _target.TotalCalls);
        Assert.Equal(0, _target.DeleteCalls);
    }

    [Fact]
    public async Task PlaylistUndoSkipsMissingBackendItemsAndReportsTheirCount()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.PlaylistLinks.SingleAsync()).TargetPlaylistId = "old";
            await db.SaveChangesAsync();
        }
        var backend = new BackendPlaylistTargetTests.JellyfinFakeBackend("old", "My playlist", ["local-2", "missing", "local-1"]);
        var service = JellyfinService(backend);
        var removed = await service.RemoveFromAppsAsync(Context(), _link, 0);
        backend.MissingItemIds.Add("missing");
        var result = Assert.IsType<OkObjectResult>(await AppController(service).AddToApps(_link, new(removed.Revision), default));
        var response = JsonSerializer.SerializeToElement(result.Value);
        Assert.Equal(1, response.GetProperty("skippedItemCount").GetInt32());
        Assert.Equal(["local-2", "local-1"], backend.Playlists.Values.Single().Members);
        Assert.True(response.GetProperty("enabled").GetBoolean());
    }

    private async Task<bool> ListedCanRemoveAsync()
    {
        var persistence = new Mock<IPlaylistPersistenceService>();
        await using var db = await _factory.CreateDbContextAsync();
        persistence.Setup(item => item.ListLinksAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(await db.PlaylistLinks.Where(item => item.Id == _link).ToArrayAsync());
        var response = Assert.IsType<OkObjectResult>(await AppController(_service, persistence.Object).List(default));
        return JsonSerializer.SerializeToElement(response.Value).GetProperty("playlistLinks")[0].GetProperty("canRemoveFromApps").GetBoolean();
    }

    [Fact]
    public async Task PlaylistRemovalCommitsItsSnapshotBeforeDeletingAndCanRetryAfterAnInterruptedDelete()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.PlaylistLinks.SingleAsync()).TargetPlaylistId = "existing";
            await db.SaveChangesAsync();
        }
        _target.SeedMetadata("existing", "My playlist", "Description", ["local-2", "manual", "local-1"]);
        _target.BeforeDelete = async () =>
        {
            await using var verify = await _factory.CreateDbContextAsync();
            var saved = await verify.ExternalMetadataSnapshots.SingleAsync(item => item.ResourceKind == "playlist-app-removal");
            var snapshot = JsonSerializer.Deserialize<PlaylistAppSnapshot>(saved.PayloadJson)!;
            Assert.Equal(["local-2", "manual", "local-1"], snapshot.Playlist.Members.Select(item => item.BackendItemId));
        };
        await Assert.ThrowsAsync<PlaylistAppRemovalException>(() => _service.RemoveFromAppsAsync(Context(), _link, 0));
        _target.BeforeDelete = null;
        _target.ReadStatus = BackendPlaylistTargetStatus.NotFound;
        _target.DeleteStatus = BackendPlaylistTargetStatus.NotFound;
        var removed = await _service.RemoveFromAppsAsync(Context(), _link, 0);
        Assert.False(removed.Enabled);
        Assert.Null(removed.TargetPlaylistId);
    }

    [Fact]
    public async Task PlaylistRemovalFailureKeepsTheLinkAndScheduleAvailableForRetry()
    {
        await MarkCreatedDestinationAsync();
        _source.Snapshot = Snapshot("failed-removal", Entry(0, "one", "source-1", "One"));
        await _service.RunAsync(Context(), new(_link, 9103));
        await using var before = await _factory.CreateDbContextAsync();
        var link = await before.PlaylistLinks.SingleAsync();
        await Assert.ThrowsAsync<PlaylistAppRemovalException>(() =>
            _service.RemoveFromAppsAsync(Context(), _link, link.Revision));
        await using var after = await _factory.CreateDbContextAsync();
        var kept = await after.PlaylistLinks.SingleAsync();
        Assert.True(kept.Enabled);
        Assert.Equal(link.Revision, kept.Revision);
        Assert.Equal("target-created", kept.TargetPlaylistId);
        Assert.Empty(await after.AuditEvents.Where(item => item.Action == "removed-from-apps").ToArrayAsync());
    }

    private async Task MarkCreatedDestinationAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var link = await db.PlaylistLinks.SingleAsync();
        link.DestinationMode = PlaylistDestinationMode.New;
        await db.SaveChangesAsync();
    }
}

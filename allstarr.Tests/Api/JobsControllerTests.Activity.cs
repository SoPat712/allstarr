using System.Text.Json;
using allstarr.Core.Favorites;
using allstarr.Core.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class JobsControllerTests
{
    [Fact]
    public async Task ActivityQueueAndHistory_AreDisjointAndOwnerScoped()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var own = await db.Jobs.SingleAsync(x => x.Id == _ownJobId);
            own.State = DurableJobState.Succeeded;
            own.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        var listener = Controller(Session(_userId));
        using var queue = Json(await listener.List(view: "queue"));
        Assert.Empty(queue.RootElement.GetProperty("jobs").EnumerateArray());
        using var history = Json(await listener.List(view: "history"));
        Assert.Equal(_ownJobId, Assert.Single(history.RootElement.GetProperty("jobs").EnumerateArray()).GetProperty("Id").GetGuid());
        using var household = Json(await Controller(Session(_userId, true)).List(view: "queue", scope: "everyone"));
        Assert.Equal(_otherJobId, Assert.Single(household.RootElement.GetProperty("jobs").EnumerateArray()).GetProperty("Id").GetGuid());
    }

    [Fact]
    public async Task ActivityDeferredFilter_DistinguishesWaitsFromFailures()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var job = await db.Jobs.SingleAsync(x => x.Id == _ownJobId);
            job.State = DurableJobState.RetryScheduled;
            job.LastAttemptOutcome = "deferred";
            job.DeferralCount = 1;
            await db.SaveChangesAsync();
        }
        using var result = Json(await Controller(Session(_userId)).List(state: "Deferred", view: "queue"));
        var row = Assert.Single(result.RootElement.GetProperty("jobs").EnumerateArray());
        Assert.Equal("Deferred", row.GetProperty("state").GetString());
    }

    [Theory]
    [InlineData("42", null)]
    [InlineData(null, "unknown")]
    public async Task ActivityRejectsUnknownFilters(string? state, string? view) =>
        Assert.IsType<BadRequestObjectResult>(await Controller(Session(_userId)).List(state: state, view: view));

    [Fact]
    public async Task ActivityPaging_UsesStableTimestampAndIdTiesWithoutDuplicates()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            foreach (var job in await db.Jobs.ToListAsync())
            {
                job.State = DurableJobState.Succeeded;
                job.CompletedAt = DateTimeOffset.UtcNow.Date;
            }
            await db.SaveChangesAsync();
        }
        var controller = Controller(Session(_userId, true));
        using var first = Json(await controller.List(view: "history", limit: 1, scope: "everyone"));
        Assert.True(first.RootElement.GetProperty("hasMore").GetBoolean());
        var time = first.RootElement.GetProperty("nextCursor").GetDateTimeOffset();
        var id = first.RootElement.GetProperty("nextCursorId").GetGuid();
        using var second = Json(await controller.List(view: "history", limit: 1, before: time, beforeId: id, scope: "everyone"));
        var next = Assert.Single(second.RootElement.GetProperty("jobs").EnumerateArray()).GetProperty("Id").GetGuid();
        Assert.NotEqual(id, next);
        Assert.False(second.RootElement.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task ActivityReads_RedactLegacyErrorsAndRawProgressFields()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var job = await db.Jobs.SingleAsync(x => x.Id == _ownJobId);
            job.LastErrorMessage = "token=fixture-private-value";
            db.AuditEvents.Add(new()
            {
                Id = Guid.CreateVersion7(),
                ActorUserId = _userId,
                Category = "job-progress",
                Action = "copy",
                SubjectType = "job",
                SubjectId = job.Id.ToString("D"),
                CorrelationId = job.CorrelationId,
                Outcome = "running",
                DetailsJson = "{\"completed\":1,\"total\":2,\"message\":\"token=fixture-private-value\",\"rawPayload\":\"fixture-raw-private\"}",
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }
        foreach (var value in new[] { await Controller(Session(_userId)).List(), await Controller(Session(_userId)).Get(_ownJobId) })
        {
            var json = JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(value).Value);
            Assert.DoesNotContain("fixture-private-value", json);
            Assert.DoesNotContain("fixture-raw-private", json);
            Assert.Contains("completed", json);
        }
    }

    [Fact]
    public async Task JobPresentation_UsesReadableFieldsAndExactActionInListAndDetail()
    {
        var availableAt = new DateTimeOffset(2026, 10, 26, 9, 15, 0, TimeSpan.Zero);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var job = await db.Jobs.SingleAsync(x => x.Id == _ownJobId);
            job.Type = "library.index";
            job.PayloadJson = JsonSerializer.Serialize(new
            {
                backendInstanceId = "fixture",
                backendPrincipalId = _userId.ToString("N"),
                backendLibraryId = "music"
            });
            job.State = DurableJobState.RetryScheduled;
            job.LastAttemptOutcome = "deferred";
            job.AvailableAt = availableAt;
            job.LastErrorCode = "library_index_unavailable";
            job.LastErrorMessage = "The library index will retry after the backend is available.";
            await db.SaveChangesAsync();
        }

        using var list = Json(await Controller(Session(_userId)).List(view: "queue"));
        using var detail = Json(await Controller(Session(_userId)).Get(_ownJobId));
        var rows = new[]
        {
            Assert.Single(list.RootElement.GetProperty("jobs").EnumerateArray()),
            detail.RootElement.GetProperty("job")
        };
        foreach (var row in rows)
        {
            Assert.Equal("library.index", row.GetProperty("Type").GetString());
            var displayName = RequiredText(row, "displayName", "DisplayName");
            var summary = RequiredText(row, "summary", "Summary");
            var recovery = RequiredText(row, "recovery", "Recovery");
            Assert.DoesNotContain("library.index", displayName, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(_ownJobId.ToString("N"), summary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("retry", recovery, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("2026-10-26", recovery, StringComparison.Ordinal);
            Assert.Equal($"#/activity?view=jobs&job={_ownJobId:N}", RequiredText(row, "actionHref", "ActionHref"));
            Assert.False(string.IsNullOrWhiteSpace(RequiredText(row, "actionLabel", "ActionLabel")));
        }
    }

    [Theory]
    [InlineData("recording.keep", "Keep a song")]
    [InlineData("playlist.retain-track", "Keep playlist song")]
    [InlineData("playback.signal", "Record a listen")]
    [InlineData("playback.signal.process", "Record a listen")]
    [InlineData("recording.tags.refresh", "Song details updated")]
    [InlineData("recording.musicbrainz.enrich", "Identify recording")]
    [InlineData("recording.odesli.enrich", "Find recording links")]
    [InlineData("listening.musicbrainz.enrich", "Identify listened song")]
    [InlineData("favorite.display-backfill", "Favorite metadata update")]
    [InlineData("favorite.link-union", "Merge linked favorites")]
    [InlineData("health.cleanup", "Health cleanup")]
    [InlineData("recording.kept-cleanup", "Remove unused kept copy")]
    [InlineData("listening-history.import", "History import")]
    [InlineData("downloads.quality-check", "Audio quality check")]
    [InlineData("extension.session.verify", "Verify source sign-in")]
    [InlineData("problem-alert.delivery", "Send problem alert")]
    [InlineData("recommendation.generate", "Generate recommendations")]
    [InlineData("playlist.provider-source-update", "Update source playlist")]
    [InlineData("storage.verify-files", "Verify stored files")]
    [InlineData("configuration.legacy-media", "Import existing media")]
    [InlineData("releases.poll", "Check new releases")]
    public async Task JobPresentation_UsesReadableNamesForKnownJobTypes(string type, string expected)
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var job = await db.Jobs.SingleAsync(item => item.Id == _ownJobId);
            job.Type = type;
            job.State = DurableJobState.Succeeded;
            job.LastAttemptOutcome = "succeeded";
            await db.SaveChangesAsync();
        }
        using var result = Json(await Controller(Session(_userId)).List());
        var displayName = Assert.Single(result.RootElement.GetProperty("jobs").EnumerateArray())
            .GetProperty("displayName").GetString()!;
        Assert.Equal(expected, displayName);
        Assert.DoesNotContain(type, displayName, StringComparison.OrdinalIgnoreCase);
        using var detail = Json(await Controller(Session(_userId)).Get(_ownJobId));
        Assert.Equal(expected, detail.RootElement.GetProperty("job").GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task JobPresentation_DoesNotCallCancellationFailureOrInferFailureFromMissingResult()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var job = await db.Jobs.SingleAsync(x => x.Id == _ownJobId);
            job.Type = "future.operation";
            job.State = DurableJobState.Cancelled;
            job.LastAttemptOutcome = "cancelled";
            job.LastErrorMessage = null;
            await db.SaveChangesAsync();
        }
        using (var cancelled = Json(await Controller(Session(_userId)).Get(_ownJobId)))
        {
            var job = cancelled.RootElement.GetProperty("job");
            Assert.DoesNotContain("failed", RequiredText(job, "summary", "Summary"), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("retry", RequiredText(job, "recovery", "Recovery"), StringComparison.OrdinalIgnoreCase);
        }

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var job = await db.Jobs.SingleAsync(x => x.Id == _ownJobId);
            job.State = DurableJobState.Succeeded;
            job.LastAttemptOutcome = null;
            job.LastErrorMessage = null;
            await db.SaveChangesAsync();
        }
        using var succeeded = Json(await Controller(Session(_userId)).Get(_ownJobId));
        var summary = RequiredText(succeeded.RootElement.GetProperty("job"), "summary", "Summary");
        Assert.Contains("completed", summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("failed", summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FavoriteTargets_DescribeNonTerminalMutationStateWithoutClaimingCompletion()
    {
        var eventId = Guid.CreateVersion7();
        var itemId = "ext-deezer-song-favorite";
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var job = await db.Jobs.SingleAsync(item => item.Id == _ownJobId);
            job.Type = "favorite.process";
            job.PayloadJson = JsonSerializer.Serialize(new { eventId });
            job.UpdatedAt = DateTimeOffset.UtcNow;
            db.Set<FavoriteEventRecord>().Add(new FavoriteEventRecord
            {
                Id = eventId,
                OwnerUserId = _userId,
                Protocol = "jellyfin",
                BackendInstanceId = "fixture",
                BackendPrincipalId = _userId.ToString("N"),
                ItemId = itemId,
                Operation = FavoriteOperation.Favorite,
                SourceRevision = "fixture",
                EventKey = eventId.ToString("N"),
                CorrelationId = job.CorrelationId,
                PolicySnapshotJson = "{}",
                JobId = _ownJobId,
                State = FavoriteEventState.Pending,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                Revision = 1
            });
            db.Set<FavoriteStateRecord>().Add(new FavoriteStateRecord
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = _userId,
                Protocol = "jellyfin",
                BackendInstanceId = "fixture",
                ItemId = itemId,
                DisplayMetadataJson = JsonSerializer.Serialize(
                    new FavoriteDisplaySnapshot(1, itemId, "song", "Loved Song", ["Loved Artist"]),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                IsFavorite = true,
                LastEventId = eventId,
                UpdatedAt = DateTimeOffset.UtcNow,
                Revision = 1
            });
            await db.SaveChangesAsync();
        }

        foreach (var (state, marker) in new[]
        {
            (DurableJobState.Running, "running"),
            (DurableJobState.RetryScheduled, "retry"),
            (DurableJobState.Cancelled, "cancelled")
        })
        {
            await using var db = await _factory.CreateDbContextAsync();
            var job = await db.Jobs.SingleAsync(item => item.Id == _ownJobId);
            job.State = state;
            job.LastAttemptOutcome = state == DurableJobState.RetryScheduled ? "deferred" : null;
            job.UpdatedAt = DateTimeOffset.UtcNow.AddSeconds((int)state);
            await db.SaveChangesAsync();
            using var result = Json(await Controller(Session(_userId)).List());
            var row = Assert.Single(result.RootElement.GetProperty("jobs").EnumerateArray());
            var summary = row.GetProperty("summary").GetString()!;
            Assert.Contains(marker, summary, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("completed", summary, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task PlaylistTargets_AreOwnerBoundAndDoNotClaimCompletionForNonTerminalStates()
    {
        var now = new DateTimeOffset(2026, 10, 26, 10, 0, 0, TimeSpan.Zero);
        var foreignLinkId = Guid.CreateVersion7();
        var foreignSnapshotId = Guid.CreateVersion7();
        var ownedLinkId = Guid.CreateVersion7();
        var ownedSnapshotId = Guid.CreateVersion7();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var ownJob = await db.Jobs.SingleAsync(item => item.Id == _ownJobId);
            var otherJob = await db.Jobs.SingleAsync(item => item.Id == _otherJobId);
            ownJob.Type = otherJob.Type = "playlist.materialize";
            ownJob.State = DurableJobState.Succeeded;
            otherJob.State = DurableJobState.Running;
            ownJob.UpdatedAt = otherJob.UpdatedAt = now;
            db.PlaylistLinks.AddRange(
                Link(foreignLinkId, _otherUserId),
                Link(ownedLinkId, _otherUserId));
            db.PlaylistSourceSnapshots.AddRange(
                Snapshot(foreignSnapshotId, foreignLinkId, _otherUserId, "Foreign playlist"),
                Snapshot(ownedSnapshotId, ownedLinkId, _otherUserId, "Other user's playlist"));
            db.PlaylistSyncRuns.AddRange(
                SyncRun(Guid.CreateVersion7(), foreignLinkId, foreignSnapshotId, _otherUserId, _ownJobId, now),
                SyncRun(Guid.CreateVersion7(), ownedLinkId, ownedSnapshotId, _otherUserId, _otherJobId, now));
            await db.SaveChangesAsync();
        }

        using var household = Json(await Controller(Session(_userId, true)).List(scope: "everyone"));
        var own = household.RootElement.GetProperty("jobs").EnumerateArray()
            .Single(item => item.GetProperty("Id").GetGuid() == _ownJobId);
        var other = household.RootElement.GetProperty("jobs").EnumerateArray()
            .Single(item => item.GetProperty("Id").GetGuid() == _otherJobId);
        Assert.DoesNotContain("Foreign playlist", JsonSerializer.Serialize(own), StringComparison.Ordinal);
        Assert.Equal("Other user's playlist", other.GetProperty("targetName").GetString());

        foreach (var state in new[] { DurableJobState.Running, DurableJobState.RetryScheduled, DurableJobState.Cancelled })
        {
            await using var db = await _factory.CreateDbContextAsync();
            var job = await db.Jobs.SingleAsync(item => item.Id == _otherJobId);
            job.State = state;
            job.LastAttemptOutcome = state == DurableJobState.RetryScheduled ? "deferred" : null;
            job.UpdatedAt = now.AddMinutes((int)state);
            await db.SaveChangesAsync();
            using var result = Json(await Controller(Session(_userId, true)).List(scope: "everyone"));
            var row = result.RootElement.GetProperty("jobs").EnumerateArray()
                .Single(item => item.GetProperty("Id").GetGuid() == _otherJobId);
            Assert.DoesNotContain("completed", row.GetProperty("summary").GetString(), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(DurableJobState.Running, "running")]
    [InlineData(DurableJobState.Failed, "failed")]
    public async Task HealthWorkWithoutACompletedReportKeepsItsKnownExecutionOutcome(DurableJobState state, string expected)
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var job = await db.Jobs.SingleAsync(item => item.Id == _ownJobId);
            job.Type = "health.smoke";
            job.State = state;
            db.HealthSmokeRuns.Add(new() { Id = job.Id, OwnerUserId = _userId, BindingJson = "{}", ArtifactsJson = "{}" });
            await db.SaveChangesAsync();
        }
        using var result = Json(await Controller(Session(_userId)).Get(_ownJobId));
        var row = result.RootElement.GetProperty("job");
        Assert.Equal(expected, row.GetProperty("displayState").GetString());
        Assert.Contains(expected, row.GetProperty("summary").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CompletedHealthWorkWithoutARetainedRunDoesNotClaimChecksPassed()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var job = await db.Jobs.SingleAsync(item => item.Id == _ownJobId);
            job.Type = "health.smoke"; job.State = DurableJobState.Succeeded;
            await db.SaveChangesAsync();
        }
        using var result = Json(await Controller(Session(_userId)).Get(_ownJobId));
        var row = result.RootElement.GetProperty("job");
        Assert.Equal("not_checked", row.GetProperty("displayState").GetString());
        Assert.Contains("unavailable", row.GetProperty("summary").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HouseholdJobsUseEachOwnersPersonalSourceName()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            foreach (var job in await db.Jobs.Where(item => item.Id == _ownJobId || item.Id == _otherJobId).ToArrayAsync())
            {
                var accountId = Guid.CreateVersion7();
                db.ProviderAccounts.Add(new() { Id = accountId, OwnerUserId = job.OwnerUserId, ProviderId = "fixture", DisplayName = job.Id == _ownJobId ? "Own source" : "Personal source" });
                job.ProviderAccountId = accountId;
            }
            await db.SaveChangesAsync();
        }
        using var result = Json(await Controller(Session(_userId, true)).List(scope: "everyone"));
        var rows = result.RootElement.GetProperty("jobs").EnumerateArray().ToArray();
        Assert.Equal("Own source", Assert.Single(rows, item => item.GetProperty("Id").GetGuid() == _ownJobId).GetProperty("source").GetString());
        Assert.Equal("Personal source", Assert.Single(rows, item => item.GetProperty("Id").GetGuid() == _otherJobId).GetProperty("source").GetString());
    }

    [Fact]
    public async Task ActivityAttemptHistory_PagesRetainedAttemptsWithoutRepeatingThem()
    {
        var now = DateTimeOffset.UtcNow;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            for (var i = 1; i <= 3; i++) db.AuditEvents.Add(new()
            {
                Id = Guid.CreateVersion7(),
                ActorUserId = _userId,
                Category = "job-attempt",
                SubjectType = "job",
                SubjectId = _ownJobId.ToString("D"),
                Action = "attempt-completed",
                Outcome = "failed",
                CreatedAt = now.AddSeconds(i),
                DetailsJson = JsonSerializer.Serialize(new { jobId = _ownJobId, attemptNumber = i, startedAtTicks = now.UtcTicks })
            });
            await db.SaveChangesAsync();
        }
        var controller = Controller(Session(_userId));
        using var first = Json(await controller.Get(_ownJobId, limit: 1));
        Assert.Equal(3, Assert.Single(first.RootElement.GetProperty("attempts").EnumerateArray()).GetProperty("AttemptNumber").GetInt32());
        using var second = Json(await controller.Get(_ownJobId, limit: 1,
            before: first.RootElement.GetProperty("nextCursor").GetDateTimeOffset(), beforeId: first.RootElement.GetProperty("nextCursorId").GetGuid()));
        Assert.Equal(2, Assert.Single(second.RootElement.GetProperty("attempts").EnumerateArray()).GetProperty("AttemptNumber").GetInt32());
    }

    [Theory]
    [InlineData(false, "everyone")]
    [InlineData(true, "everyone")]
    [InlineData(true, "invalid")]
    public async Task ActivityScope_IsExplicitAndRequiresAdministrator(bool administrator, string scope)
    {
        var result = await Controller(Session(_userId, administrator)).List(scope: scope);
        if (scope == "invalid") Assert.IsType<BadRequestObjectResult>(result);
        else if (!administrator) Assert.IsType<ForbidResult>(result);
        else
        {
            using var json = Json(result);
            Assert.Equal(2, json.RootElement.GetProperty("jobs").GetArrayLength());
            Assert.All(json.RootElement.GetProperty("jobs").EnumerateArray(), row =>
                Assert.False(Guid.TryParse(row.GetProperty("ownerName").GetString(), out _)));
        }
    }

    private static PlaylistLinkRecord Link(Guid id, Guid owner) => new()
    {
        Id = id,
        OwnerUserId = owner,
        SourceProviderId = "fixture",
        SourcePlaylistId = id.ToString("N"),
        SourcePlaylistIdHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(id.ToByteArray())),
        TargetProtocol = "jellyfin",
        TargetBackendInstanceId = "fixture",
        Mode = PlaylistLinkMode.Hybrid,
        MaterializationMode = PlaylistMaterializationMode.Reconcile,
        RuleVersion = "rules",
        PolicyVersion = "policy",
        Enabled = true,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static PlaylistSourceSnapshotRecord Snapshot(Guid id, Guid linkId, Guid owner, string name) => new()
    {
        Id = id,
        OwnerUserId = owner,
        PlaylistLinkId = linkId,
        SnapshotVersion = 1,
        ProviderRevision = "revision",
        Name = name,
        PayloadSha256 = new string('c', 64),
        CorrelationId = id.ToString("N"),
        RetrievedAt = DateTimeOffset.UtcNow,
        PublishedAt = DateTimeOffset.UtcNow
    };

    private static PlaylistSyncRunRecord SyncRun(Guid id, Guid linkId, Guid snapshotId, Guid owner, Guid jobId, DateTimeOffset now) => new()
    {
        Id = id,
        OwnerUserId = owner,
        PlaylistLinkId = linkId,
        PlaylistSourceSnapshotId = snapshotId,
        JobId = jobId,
        Generation = 1,
        IdempotencyKey = id.ToString("N"),
        RuleVersion = "rules",
        MaterializationMode = PlaylistMaterializationMode.Reconcile,
        State = PlaylistSyncState.Succeeded,
        StartedAt = now,
        CompletedAt = now,
        Revision = 1
    };

    private static JsonDocument Json(IActionResult value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(value).Value));

    private static string RequiredText(JsonElement row, params string[] names)
    {
        foreach (var name in names)
            if (row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()))
                return value.GetString()!;
        throw new Xunit.Sdk.XunitException($"Expected one of [{string.Join(", ", names)}] in {row}");
    }
}

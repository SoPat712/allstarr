using allstarr.Core.Favorites;
using allstarr.Core.Jobs;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace allstarr.Tests;

public sealed partial class FavoriteActionPipelineTests
{
    [Theory]
    [InlineData(FavoriteOperation.Favorite)]
    [InlineData(FavoriteOperation.Unfavorite)]
    public async Task AcceptedMutationSavesCurrentStateBeforeBackgroundWork(FavoriteOperation operation)
    {
        var receipt = await _pipeline.RecordAsync(Request(operation, "immediate"));
        await using var db = await _factory.CreateDbContextAsync();
        var state = Assert.Single(await db.Set<FavoriteStateRecord>().ToArrayAsync());
        Assert.Equal(operation == FavoriteOperation.Favorite, state.IsFavorite);
        Assert.Equal(receipt.EventId, state.LastEventId);
        Assert.Equal(1, state.Revision);
        Assert.Equal(DurableJobState.Pending, (await db.Jobs.SingleAsync()).State);
    }

    [Theory]
    [InlineData(FavoriteOperation.Favorite, FavoriteOperation.Unfavorite)]
    [InlineData(FavoriteOperation.Unfavorite, FavoriteOperation.Favorite)]
    public async Task OlderExecutionCannotOverwriteNewerAcceptedIntent(FavoriteOperation oldOperation, FavoriteOperation currentOperation)
    {
        await _pipeline.RecordAsync(Request(oldOperation, "ordered"));
        var oldClaim = (await _jobs.ClaimNextAsync("older"))!;
        _clock.UtcNow = _clock.UtcNow.AddSeconds(1);
        var current = await _pipeline.RecordAsync(Request(currentOperation, "ordered"));
        var currentClaim = (await _jobs.ClaimNextAsync("current"))!;
        var handler = new FavoriteActionJobHandler(_factory, [], _clock);
        var completed = await handler.ExecuteAsync(new(currentClaim, EmptyServices.Instance), default);
        await _jobs.CompleteAsync(currentClaim, completed);

        var stale = await handler.ExecuteAsync(new(oldClaim, EmptyServices.Instance), default);

        Assert.Equal(DurableJobCompletionKind.Cancelled, stale.Kind);
        await using var db = await _factory.CreateDbContextAsync();
        var state = Assert.Single(await db.Set<FavoriteStateRecord>().ToArrayAsync());
        Assert.Equal(currentOperation == FavoriteOperation.Favorite, state.IsFavorite);
        Assert.Equal(current.EventId, state.LastEventId);
    }

    [Fact]
    public async Task UnfavoriteRequestsCancellationOfAnAlreadyRunningDownload()
    {
        var request = Request(FavoriteOperation.Favorite, "running") with { ItemId = "ext-fixture-song-track-1" };
        var old = await _pipeline.RecordAsync(request);
        var claim = (await _jobs.ClaimNextAsync("running"))!;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.Set<FavoriteEventRecord>().SingleAsync()).State = FavoriteEventState.Running;
            (await db.Set<FavoriteActionRecord>().SingleAsync(item => item.ActionType == "download")).State = FavoriteActionState.Running;
            await db.SaveChangesAsync();
        }
        _clock.UtcNow = _clock.UtcNow.AddSeconds(1);
        var current = await _pipeline.RecordAsync(request with { Operation = FavoriteOperation.Unfavorite });
        await using var verify = await _factory.CreateDbContextAsync();
        var job = await verify.Jobs.SingleAsync(item => item.Id == claim.JobId);
        Assert.NotNull(job.CancellationRequestedAt);
        Assert.Equal(DurableJobState.Running, job.State);
        Assert.False(await _jobs.IsExecutionCurrentAsync(claim));
        var state = Assert.Single(await verify.Set<FavoriteStateRecord>().ToArrayAsync());
        Assert.False(state.IsFavorite);
        Assert.Equal(current.EventId, state.LastEventId);
        Assert.NotEqual(old.EventId, state.LastEventId);
    }

    [Fact]
    public async Task UnfavoriteDuringDownloadCannotBeTurnedBackIntoSuccess()
    {
        var request = Request(FavoriteOperation.Favorite, "in-flight") with { ItemId = "ext-fixture-song-track-1" };
        var older = await _pipeline.RecordAsync(request);
        var claim = (await _jobs.ClaimNextAsync("in-flight"))!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new AwaitedDownload(async () =>
        {
            entered.TrySetResult();
            await release.Task;
            return FavoriteActionExecutionResult.Success();
        });
        var handler = new FavoriteActionJobHandler(_factory, [executor], _clock);
        var execution = handler.ExecuteAsync(new(claim, EmptyServices.Instance), default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            _clock.UtcNow = _clock.UtcNow.AddSeconds(1);
            var current = await _pipeline.RecordAsync(request with { Operation = FavoriteOperation.Unfavorite });
            release.TrySetResult();
            var completed = await execution;
            Assert.Equal(DurableJobCompletionKind.Cancelled, completed.Kind);
            await using var db = await _factory.CreateDbContextAsync();
            var state = Assert.Single(await db.Set<FavoriteStateRecord>().ToArrayAsync());
            Assert.False(state.IsFavorite);
            Assert.Equal(current.EventId, state.LastEventId);
            Assert.DoesNotContain(await db.Set<FavoriteActionRecord>().Where(item => item.EventId == older.EventId &&
                item.ActionType == "download").ToArrayAsync(), item => item.State == FavoriteActionState.Succeeded);
        }
        finally
        {
            release.TrySetResult();
            await execution;
        }
    }

    [Fact]
    public async Task PreviousProcessExecutionCannotMutateFavoriteActionsAfterRecovery()
    {
        var receipt = await _pipeline.RecordAsync(Request(FavoriteOperation.Favorite, "generation"));
        var oldClaim = (await _jobs.ClaimNextAsync("before-restart"))!;
        Assert.Equal(1, await _jobs.RecoverInterruptedAsync());
        Assert.Equal(2, (await _jobs.ClaimNextAsync("after-restart"))!.AttemptNumber);
        var handler = new FavoriteActionJobHandler(_factory, [], _clock);
        var result = await handler.ExecuteAsync(new(oldClaim, EmptyServices.Instance), default);
        Assert.Equal(DurableJobCompletionKind.Cancelled, result.Kind);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(FavoriteEventState.Pending, (await db.Set<FavoriteEventRecord>().SingleAsync(item => item.Id == receipt.EventId)).State);
        Assert.All(await db.Set<FavoriteActionRecord>().ToArrayAsync(), item => Assert.Equal(0, item.AttemptCount));
    }

    [Fact]
    public async Task PreUpgradeEventWithNoStateStillAppliesItsCurrentIntent()
    {
        var receipt = await _pipeline.RecordAsync(Request(FavoriteOperation.Favorite, "legacy"));
        await using (var db = await _factory.CreateDbContextAsync())
            await db.Set<FavoriteStateRecord>().ExecuteDeleteAsync();
        var claim = (await _jobs.ClaimNextAsync("legacy"))!;
        var handler = new FavoriteActionJobHandler(_factory, [], _clock);
        var result = await handler.ExecuteAsync(new(claim, EmptyServices.Instance), default);
        Assert.Equal(DurableJobCompletionKind.Succeeded, result.Kind);
        await using var verify = await _factory.CreateDbContextAsync();
        var state = Assert.Single(await verify.Set<FavoriteStateRecord>().ToArrayAsync());
        Assert.True(state.IsFavorite);
        Assert.Equal(receipt.EventId, state.LastEventId);
        Assert.Equal(1, state.Revision);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MutationRequiresCurrentEnabledBackendPrincipal(bool disable)
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var user = await db.Users.SingleAsync(item => item.Id == _userId);
            if (disable) user.Enabled = false;
            else user.BackendPrincipalId = "changed-principal";
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _pipeline.RecordAsync(Request(FavoriteOperation.Favorite, "authority")));
        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Empty(await verify.Set<FavoriteEventRecord>().ToArrayAsync());
        Assert.Empty(await verify.Set<FavoriteStateRecord>().ToArrayAsync());
        Assert.Empty(await verify.Jobs.ToArrayAsync());
    }

    [Fact]
    public async Task FavoriteLifecycleFollowsAcceptedOrderWhenClockMovesBackwards()
    {
        var request = Request(FavoriteOperation.Favorite, "clock-order");
        var first = await _pipeline.RecordAsync(request);
        _clock.UtcNow = _clock.UtcNow.AddHours(-1);
        await _pipeline.RecordAsync(request with { Operation = FavoriteOperation.Unfavorite });
        _clock.UtcNow = _clock.UtcNow.AddHours(-1);
        var current = await _pipeline.RecordAsync(request);
        var repeated = await _pipeline.RecordAsync(request);
        Assert.NotEqual(first.EventId, current.EventId);
        Assert.Equal(current.EventId, repeated.EventId);
        Assert.False(repeated.Created);
        await using var db = await _factory.CreateDbContextAsync();
        var state = Assert.Single(await db.Set<FavoriteStateRecord>().ToArrayAsync());
        Assert.True(state.IsFavorite);
        Assert.Equal(current.EventId, state.LastEventId);
    }

    private sealed class AwaitedDownload(Func<Task<FavoriteActionExecutionResult>> execute) : IFavoriteActionExecutor
    {
        public string ActionType => "download";
        public Task<FavoriteActionExecutionResult> ExecuteAsync(FavoriteEventRecord favoriteEvent, FavoriteActionRecord action,
            int executionAttempt, CancellationToken cancellationToken) => execute();
    }

    [Fact]
    public async Task CanonicalFavoriteReusesOnlyAnAccessibleLocalRecording()
    {
        var recordingId = Guid.CreateVersion7();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.CanonicalRecordings.Add(new()
            {
                Id = recordingId,
                CreatedByUserId = _userId,
                Title = "Local recording",
                PublicId = CanonicalRecordingRecord.DefaultPublicId(recordingId),
                CreatedAt = _clock.UtcNow,
                UpdatedAt = _clock.UtcNow
            });
            db.LibraryTracks.Add(new()
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = _userId,
                CanonicalRecordingId = recordingId,
                BackendLibraryId = "music",
                Protocol = "jellyfin",
                BackendInstanceId = "jellyfin-main",
                BackendItemId = "local-canonical",
                FilePath = "/source/never-written.flac",
                Title = "Local recording",
                Artist = "Artist",
                IndexedAt = _clock.UtcNow,
                SourceModifiedAt = _clock.UtcNow,
                UpdatedAt = _clock.UtcNow
            });
            await db.SaveChangesAsync();
        }
        var favorite = new FavoriteEventRecord
        {
            OwnerUserId = _userId,
            Protocol = "jellyfin",
            BackendInstanceId = "jellyfin-main",
            ItemId = CanonicalRecordingRecord.DefaultPublicId(recordingId)
        };
        var access = new TestBackendLibraryAccess(_factory, "music");
        Assert.True(await FavoriteDownloadActionExecutor.HasLocalMatchAsync(_factory, access, favorite, default));
        access.Permissions[_userId] = BackendLibraryAccess.Unavailable;
        Assert.False(await FavoriteDownloadActionExecutor.HasLocalMatchAsync(_factory, access, favorite, default));
    }

    [Theory]
    [InlineData("event")]
    [InlineData("action-start")]
    [InlineData("action-complete")]
    [InlineData("legacy-state")]
    public async Task RecoveryBetweenGuardAndSaveCannotMutateFavoriteRows(string boundary)
    {
        var receipt = await _pipeline.RecordAsync(Request(FavoriteOperation.Favorite, "save-fence"));
        if (boundary == "legacy-state")
        {
            await using var db = await _factory.CreateDbContextAsync();
            await db.Set<FavoriteStateRecord>().ExecuteDeleteAsync();
        }
        var claim = (await _jobs.ClaimNextAsync("save-before-recovery"))!;
        string? before = null;
        var interceptor = new RecoveryAtFavoriteSave(boundary, async () =>
        {
            before = await FavoriteRowsAsync(receipt.EventId);
            Assert.Equal(1, await _jobs.RecoverInterruptedAsync());
            Assert.Equal(2, (await _jobs.ClaimNextAsync("save-after-recovery"))!.AttemptNumber);
        });
        var factory = new TestFactory(new DbContextOptionsBuilder<AllstarrDbContext>(_database.Options)
            .AddInterceptors(interceptor).Options);
        var handler = new FavoriteActionJobHandler(factory, [], _clock);

        var result = await handler.ExecuteAsync(new(claim, EmptyServices.Instance), default);

        Assert.True(interceptor.Triggered);
        Assert.Equal(before, await FavoriteRowsAsync(receipt.EventId));
        Assert.Equal(DurableJobCompletionKind.Cancelled, result.Kind);
    }

    private async Task<string> FavoriteRowsAsync(Guid eventId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            Event = await db.Set<FavoriteEventRecord>().AsNoTracking().SingleAsync(item => item.Id == eventId),
            Actions = await db.Set<FavoriteActionRecord>().AsNoTracking().Where(item => item.EventId == eventId)
                .OrderBy(item => item.Id).ToArrayAsync(),
            States = await db.Set<FavoriteStateRecord>().AsNoTracking().OrderBy(item => item.Id).ToArrayAsync()
        });
    }

    [Fact]
    public async Task ProgressWriteBetweenGuardAndSaveDoesNotCancelCurrentFavorite()
    {
        await _pipeline.RecordAsync(Request(FavoriteOperation.Favorite, "progress-fence"));
        var claim = (await _jobs.ClaimNextAsync("progress-fence"))!;
        var interceptor = new RecoveryAtFavoriteSave("event", async () =>
        {
            await using var db = await _factory.CreateDbContextAsync();
            var job = await db.Jobs.SingleAsync(item => item.Id == claim.JobId);
            job.Revision++;
            await db.SaveChangesAsync();
        });
        var factory = new TestFactory(new DbContextOptionsBuilder<AllstarrDbContext>(_database.Options)
            .AddInterceptors(interceptor).Options);
        var handler = new FavoriteActionJobHandler(factory, [], _clock);

        var result = await handler.ExecuteAsync(new(claim, EmptyServices.Instance), default);

        Assert.True(interceptor.Triggered);
        Assert.Equal(DurableJobCompletionKind.Succeeded, result.Kind);
        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Equal(FavoriteEventState.Succeeded, (await verify.Set<FavoriteEventRecord>().SingleAsync()).State);
        Assert.Equal(FavoriteActionState.Succeeded, (await verify.Set<FavoriteActionRecord>().SingleAsync()).State);
    }

    private sealed class RecoveryAtFavoriteSave(string boundary, Func<Task> recover) : SaveChangesInterceptor
    {
        public bool Triggered { get; private set; }
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var entries = eventData.Context!.ChangeTracker.Entries().Where(item =>
                item.State is EntityState.Added or EntityState.Modified).ToArray();
            var matches = boundary switch
            {
                "event" => entries.Any(item => item.Entity is FavoriteEventRecord { State: FavoriteEventState.Running }),
                "action-start" => entries.Any(item => item.Entity is FavoriteActionRecord { State: FavoriteActionState.Running }),
                "action-complete" => entries.Any(item => item.Entity is FavoriteActionRecord { State: FavoriteActionState.Succeeded }),
                "legacy-state" => entries.Any(item => item.Entity is FavoriteStateRecord),
                _ => false
            };
            if (!Triggered && matches)
            {
                Triggered = true;
                await recover();
            }
            return result;
        }
    }
}

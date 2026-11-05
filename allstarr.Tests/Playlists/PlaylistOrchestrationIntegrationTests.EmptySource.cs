using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Playlists;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class PlaylistOrchestrationIntegrationTests
{
    private DateTimeOffset? _confirmedEmptyAt;

    private async Task ConfirmEmptySourceAsync()
    {
        await Assert.ThrowsAsync<PlaylistSourceEmptyException>(() => _service.RefreshAsync(Context(), _link));
        _confirmedEmptyAt = (_confirmedEmptyAt ?? _now).AddDays(1);
        _service = SourceServiceAt(_confirmedEmptyAt.Value);
    }

    [Theory]
    [InlineData(PlaylistLinkMode.Virtual)]
    [InlineData(PlaylistLinkMode.Materialized)]
    [InlineData(PlaylistLinkMode.Hybrid)]
    public async Task Empty_source_keeps_published_songs_and_records_attention(PlaylistLinkMode mode)
    {
        await SetLink(mode: mode);
        var previous = await SeedNonemptySourceAsync();
        await _service.RunAsync(Context(), new(_link, 1));
        var writes = _target.WriteCalls;
        _source.Snapshot = Snapshot("empty");

        var failure = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => _service.RunAsync(Context(), new(_link, 2)));

        Assert.Equal("The playlist source returned an empty list, so the previous songs were kept. The next scheduled refresh will check again.", failure.Message);
        Assert.Equal(writes, _target.WriteCalls);
        await AssertPublishedSourceAsync(previous.SnapshotId, 1);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Single(await db.PlaylistSourceSnapshots.ToArrayAsync());
        var attention = await db.AuditEvents.SingleAsync(item => item.Action == "source-empty-kept");
        Assert.Equal(_user, attention.ActorUserId);
        Assert.Equal(_link.ToString("N"), attention.SubjectId);
        Assert.Equal("conflicted", attention.Outcome);
        using var details = JsonDocument.Parse(attention.DetailsJson);
        Assert.Equal(failure.Message, details.RootElement.GetProperty("message").GetString());
        Assert.Equal("Provider Mix", details.RootElement.GetProperty("playlistName").GetString());
    }

    [Fact]
    public async Task Empty_source_requires_a_full_scheduled_interval_even_after_manual_retries_and_restart()
    {
        var previous = await SeedNonemptySourceAsync();
        await SetSourceScheduleAsync("0 9 * * *");
        _source.Snapshot = Snapshot("empty");
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => SourceServiceAt(_now).RefreshAsync(Context(), _link));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => SourceServiceAt(_now.AddHours(23)).RefreshAsync(Context(), _link));
        await AssertPublishedSourceAsync(previous.SnapshotId, 1);

        var confirmed = await SourceServiceAt(_now.AddDays(1)).RefreshAsync(Context(), _link);

        Assert.NotEqual(previous.SnapshotId, confirmed.SnapshotId);
        await AssertPublishedSourceAsync(confirmed.SnapshotId, 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Source_recovery_or_failure_resets_empty_confirmation(bool failedRead)
    {
        var previous = await SeedNonemptySourceAsync();
        await SetSourceScheduleAsync("0 9 * * *");
        _source.Snapshot = Snapshot("empty");
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => _service.RefreshAsync(Context(), _link));
        if (failedRead)
        {
            _source.FailureCode = "source_unavailable";
            await Assert.ThrowsAsync<PlaylistSourceUnavailableException>(() =>
                SourceServiceAt(_now.AddHours(12)).RefreshAsync(Context(), _link));
            _source.FailureCode = null;
        }
        else
        {
            _source.Snapshot = Snapshot("recovered", Entry(0, "one", "source-1", "One"));
            await SourceServiceAt(_now.AddHours(12)).RefreshAsync(Context(), _link);
        }
        _source.Snapshot = Snapshot("empty-again");

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            SourceServiceAt(_now.AddDays(1)).RefreshAsync(Context(), _link));

        await AssertPublishedSourceAsync(previous.SnapshotId, 1);
        var confirmed = await SourceServiceAt(_now.AddDays(2)).RefreshAsync(Context(), _link);
        await AssertPublishedSourceAsync(confirmed.SnapshotId, 0);
    }

    [Fact]
    public async Task Empty_source_without_a_schedule_uses_a_daily_confirmation_interval()
    {
        await SeedNonemptySourceAsync();
        _source.Snapshot = Snapshot("empty");
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => _service.RefreshAsync(Context(), _link));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            SourceServiceAt(_now.AddHours(1)).RefreshAsync(Context(), _link));

        var confirmed = await SourceServiceAt(_now.AddDays(1)).RefreshAsync(Context(), _link);

        await AssertPublishedSourceAsync(confirmed.SnapshotId, 0);
    }

    [Fact]
    public async Task Weekly_source_requires_a_week_between_empty_reads()
    {
        await SeedNonemptySourceAsync();
        await SetSourceScheduleAsync("0 9 * * 1");
        _source.Snapshot = Snapshot("empty");
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => _service.RefreshAsync(Context(), _link));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            SourceServiceAt(_now.AddDays(6)).RefreshAsync(Context(), _link));

        var confirmed = await SourceServiceAt(_now.AddDays(7)).RefreshAsync(Context(), _link);

        await AssertPublishedSourceAsync(confirmed.SnapshotId, 0);
    }

    [Fact]
    public async Task Initial_empty_source_can_publish_and_the_next_nonempty_refresh_repopulates_it()
    {
        _source.Snapshot = Snapshot("initial-empty");
        var empty = await _service.RefreshAsync(Context(), _link);
        await AssertPublishedSourceAsync(empty.SnapshotId, 0);
        _source.Snapshot = Snapshot("recovered", Entry(0, "one", "source-1", "One"));

        var recovered = await _service.RunAsync(Context(), new(_link, 1));

        Assert.Single(recovered.Plan.Entries);
        await using var db = await _factory.CreateDbContextAsync();
        var current = await db.PlaylistSourceSnapshots.OrderByDescending(item => item.SnapshotVersion).FirstAsync();
        await AssertPublishedSourceAsync(current.Id, 1);
    }

    [Fact]
    public async Task Scheduled_refresh_republishes_historical_songs_after_an_empty_snapshot()
    {
        var previous = await SeedNonemptySourceAsync();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.PlaylistSourceSnapshots.Add(new PlaylistSourceSnapshotRecord
            {
                Id = Guid.CreateVersion7(), OwnerUserId = _user, PlaylistLinkId = _link,
                ProviderAccountId = _account, SnapshotVersion = previous.SnapshotVersion + 1,
                ProviderRevision = "old-empty-read", Name = "Provider Mix", PayloadSha256 = Hash("old-empty-read"),
                RetrievedAt = _now, PublishedAt = _now, CorrelationId = "old-empty-read"
            });
            await db.SaveChangesAsync();
        }
        await SetLink(mode: PlaylistLinkMode.Virtual);

        await _service.RunAsync(Context(), new(_link, 1));

        await using var verify = await _factory.CreateDbContextAsync();
        var current = await verify.PlaylistSourceSnapshots.OrderByDescending(item => item.SnapshotVersion).FirstAsync();
        Assert.Equal(previous.SnapshotVersion + 2, current.SnapshotVersion);
        await AssertPublishedSourceAsync(current.Id, 1);
    }

    [Fact]
    public async Task Failed_source_read_keeps_songs_and_schedules_backoff()
    {
        var previous = await SeedNonemptySourceAsync();
        _source.FailureCode = "source_unavailable";
        _source.FailureError = new(ProviderErrorKind.RateLimited, TimeSpan.FromSeconds(17));
        var queue = RematchQueue();
        await queue.EnqueueAsync(new DurableJobEnqueueRequest<PlaylistMaterializationJobPayload>(
            DurableScheduleEngine.PlaylistSyncJobType, "failed-source", new(_link, 1),
            _user, ProviderAccountId: _account, Capability: "playlist"));
        var claim = Assert.IsType<DurableJobClaim>(await queue.ClaimNextAsync("source-worker"));

        var completion = await new PlaylistMaterializationJobHandler(_factory, _service, new Clock(_now))
            .ExecuteAsync(new DurableJobExecutionContext(claim, EmptyServices.Instance), default);

        Assert.Equal(DurableJobCompletionKind.Retry, completion.Kind);
        Assert.Equal("rate-limited", completion.ErrorCode);
        Assert.Equal(TimeSpan.FromSeconds(17), completion.RetryDelay);
        await queue.CompleteAsync(claim, completion);
        await using var db = await _factory.CreateDbContextAsync();
        var retry = await db.Jobs.SingleAsync(item => item.Id == claim.JobId);
        Assert.Equal(DurableJobState.RetryScheduled, retry.State);
        Assert.Equal(_now.AddSeconds(17), retry.AvailableAt);
        await AssertPublishedSourceAsync(previous.SnapshotId, 1);
    }

    [Fact]
    public async Task Scheduled_empty_source_finishes_with_attention_and_waits_for_the_normal_schedule()
    {
        await SetLink(mode: PlaylistLinkMode.Virtual);
        var previous = await SeedNonemptySourceAsync();
        await SetSourceScheduleAsync("0 9 * * *");
        _source.Snapshot = Snapshot("empty");
        var queue = RematchQueue();
        await queue.EnqueueAsync(new DurableJobEnqueueRequest<PlaylistMaterializationJobPayload>(
            DurableScheduleEngine.PlaylistSyncJobType, "empty-source", new(_link, 1),
            _user, ProviderAccountId: _account, Capability: "playlist"));
        var claim = Assert.IsType<DurableJobClaim>(await queue.ClaimNextAsync("source-worker"));

        var completion = await new PlaylistMaterializationJobHandler(_factory, _service, new Clock(_now))
            .ExecuteAsync(new DurableJobExecutionContext(claim, EmptyServices.Instance), default);

        Assert.Equal(DurableJobCompletionKind.Failed, completion.Kind);
        Assert.Equal("playlist_source_empty_kept", completion.ErrorCode);
        Assert.Contains("previous songs were kept", completion.SafeMessage);
        await queue.CompleteAsync(claim, completion);
        Assert.Null(await queue.ClaimNextAsync("source-worker"));
        await AssertPublishedSourceAsync(previous.SnapshotId, 1);
    }

    private Task<PlaylistRefreshResult> SeedNonemptySourceAsync()
    {
        _source.Snapshot = Snapshot("nonempty", Entry(0, "one", "source-1", "One"));
        return _service.RefreshAsync(Context(), _link);
    }

    private async Task SetSourceScheduleAsync(string cron)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var schedule = new JobScheduleRecord
        {
            Id = Guid.CreateVersion7(), OwnerUserId = _user, JobType = DurableScheduleEngine.PlaylistSyncJobType,
            CronExpression = cron, TimeZoneId = "UTC", Enabled = true, CreatedAt = _now, UpdatedAt = _now
        };
        db.JobSchedules.Add(schedule);
        (await db.PlaylistLinks.SingleAsync(item => item.Id == _link)).ScheduleId = schedule.Id;
        await db.SaveChangesAsync();
    }

    private PlaylistOrchestrationService SourceServiceAt(DateTimeOffset at) => new(
        _factory, _source, new FakeTargetResolver(_target), new PlaylistMaterializationPlanner(),
        new TrackMatchDecisionEngine(), _trackMatches, new Clock(at), new KeyedAsyncLock(),
        new TestBackendLibraryAccess(_factory, "music"), new CollectingLogger<PlaylistOrchestrationService>(_logs));

    private async Task AssertPublishedSourceAsync(Guid expected, int count)
    {
        var projection = await new DurablePlaylistProjectionReader(_factory,
            new TestBackendLibraryAccess(_factory, "music")).ReadByLinkIdAsync(_user, _link);
        Assert.NotNull(projection);
        Assert.Equal(expected, projection.SnapshotId);
        Assert.Equal(count, projection.TotalCount);
    }
}

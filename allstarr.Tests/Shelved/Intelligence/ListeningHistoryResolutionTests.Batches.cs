using System.Data.Common;
using System.Text.Json;
using allstarr.Core.Intelligence;
using allstarr.Core.Jobs;
using allstarr.Core.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace allstarr.Tests;

public sealed partial class ListeningHistoryResolutionTests
{
    [Fact]
    public async Task LargeLocalImport_ReleasesWriterBetweenBatchesAndPreservesResults()
    {
        var tracks = await SeedLocalImport(325, 12);
        await Add("Song 0", source: "protocol");
        await ListeningHistoryResolution.EnqueueAsync(factory, jobs, Scope, clock.UtcNow, default);
        var probe = ObserveWrites();
        var claim = Assert.IsType<DurableJobClaim>(await jobs.ClaimNextAsync("history-test", [ListeningHistoryResolution.JobTypeName]));
        using var cancellation = new CancellationTokenSource();
        var running = Handler().ExecuteAsync(new(claim, services), cancellation.Token);
        try
        {
            for (var batch = 1; batch <= 3; batch++)
            {
                await AwaitLocalPause(running);
                Assert.False(running.IsCompleted);
                await AssertSavedProgress(batch * 100, batch * 1200, 3900);
                Assert.Equal(batch, probe.CursorWrites);
                Assert.Equal(0, probe.TrackedEvents);
                await using var writer = new AllstarrDbContext(database.Options);
                writer.Database.SetCommandTimeout(1);
                await writer.Database.OpenConnectionAsync();
                await writer.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=50;");
                Assert.Equal(1, await writer.Users.Where(item => item.Id == owner)
                    .ExecuteUpdateAsync(update => update.SetProperty(item => item.DisplayName, "Writer " + batch)));
                time.Advance(TimeSpan.FromMilliseconds(250));
            }
            Assert.Equal(DurableJobCompletionKind.Succeeded, (await running).Kind);
            Assert.Equal(4, probe.CursorWrites);
            await AssertSavedProgress(325, 3900, 3900);
            await using var db = factory.CreateDbContext();
            Assert.All(await db.ListeningEvents.Where(item => item.SourceKind == "import").ToArrayAsync(), item =>
            {
                var track = tracks[item.Title!];
                Assert.Equal(track.CanonicalRecordingId, item.CanonicalRecordingId);
                Assert.Equal(track.Id, item.LibraryTrackId);
                Assert.Equal(track.BackendItemId, item.TrackReference);
                Assert.Equal(1, item.Revision);
                Assert.Equal(item.State == ListeningEventState.Skipped ? "original-isrc" : track.Isrc, item.Isrc);
                Assert.Equal(clock.UtcNow.AddDays(-1), item.UpdatedAt);
                Assert.Equal("spotify", item.ProviderId);
                Assert.Null(item.ProviderTrackIdentityId);
            });
            var live = await db.ListeningEvents.SingleAsync(item => item.SourceKind == "protocol");
            Assert.Null(live.CanonicalRecordingId);
            Assert.Equal(0, live.Revision);
        }
        finally
        {
            cancellation.Cancel();
            try { await running; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task InterruptedLocalBatch_RollsBackCursorAndCountersAndResumesOnce()
    {
        await SeedLocalImport(225, 4);
        await ListeningHistoryResolution.EnqueueAsync(factory, jobs, Scope, clock.UtcNow, default);
        using var cancellation = new CancellationTokenSource();
        var probe = ObserveWrites();
        probe.AfterEventWrite = count =>
        {
            if (count != 107) return;
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
        };
        var claim = Assert.IsType<DurableJobClaim>(await jobs.ClaimNextAsync("history-test", [ListeningHistoryResolution.JobTypeName]));
        var running = Handler().ExecuteAsync(new(claim, services), cancellation.Token);
        await AwaitLocalPause(running);
        time.Advance(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        await AssertSavedProgress(100, 400, 900);
        await using (var db = factory.CreateDbContext())
        {
            Assert.Equal(400, await db.ListeningEvents.CountAsync(item => item.CanonicalRecordingId != null));
            Assert.All(await db.ListeningEvents.ToArrayAsync(), item => Assert.Equal(item.CanonicalRecordingId == null ? 0 : 1, item.Revision));
        }
        clock.UtcNow += TimeSpan.FromHours(1);
        Assert.Equal(1, await jobs.RecoverInterruptedAsync());
        probe.AfterEventWrite = null;
        Assert.Equal(DurableJobCompletionKind.Succeeded, (await Run()).Kind);
        await AssertSavedProgress(225, 900, 900);
        await using var read = factory.CreateDbContext();
        Assert.All(await read.ListeningEvents.ToArrayAsync(), item => Assert.Equal(1, item.Revision));
    }

    [Fact]
    public async Task BusyLocalBatch_RetriesWholeTransactionWithoutDoubleCounting()
    {
        await SeedLocalImport(25, 4);
        await ListeningHistoryResolution.EnqueueAsync(factory, jobs, Scope, clock.UtcNow, default);
        var probe = ObserveWrites();
        probe.AfterEventWrite = count =>
        {
            if (count == 7) throw new DbUpdateException("Temporary write conflict.", new SqliteException("Database is busy.", 5));
        };
        Assert.Equal(DurableJobCompletionKind.Succeeded, (await Run()).Kind);
        Assert.Equal(2, probe.CursorWrites);
        await AssertSavedProgress(25, 100, 100);
        await using var db = factory.CreateDbContext();
        Assert.All(await db.ListeningEvents.ToArrayAsync(), item =>
        {
            Assert.NotNull(item.CanonicalRecordingId);
            Assert.Equal(1, item.Revision);
        });
    }

    [Fact]
    public async Task RepeatedLocalSong_ResolvesEveryIdChunkWithOneCursorIncrement()
    {
        var tracks = await SeedLocalImport(1, 1201);
        await ListeningHistoryResolution.EnqueueAsync(factory, jobs, Scope, clock.UtcNow, default);
        var probe = ObserveWrites();
        var writes = 0;
        probe.AfterEventWrite = count => writes = count;
        Assert.Equal(DurableJobCompletionKind.Succeeded, (await Run()).Kind);
        Assert.Equal(1, probe.CursorWrites);
        Assert.Equal(3, writes);
        await AssertSavedProgress(1, 1201, 1201);
        await using var db = factory.CreateDbContext();
        Assert.All(await db.ListeningEvents.ToArrayAsync(), item =>
        {
            Assert.Equal(tracks[item.Title!].CanonicalRecordingId, item.CanonicalRecordingId);
            Assert.Equal(1, item.Revision);
        });
    }

    private async Task AwaitLocalPause(Task<DurableJobCompletion> running)
    {
        var pause = time.NextTimerAsync();
        Assert.Same(pause, await Task.WhenAny(pause, running));
        Assert.Equal(TimeSpan.FromMilliseconds(250), await pause);
    }

    private async Task AssertSavedProgress(int cursor, int resolved, int total)
    {
        await using var db = factory.CreateDbContext();
        var job = await db.Jobs.SingleAsync(item => item.Type == ListeningHistoryResolution.JobTypeName);
        using var payload = JsonDocument.Parse(job.PayloadJson);
        Assert.Equal(cursor, payload.RootElement.GetProperty("Cursor").GetInt32());
        var import = await db.ListeningHistoryImports.SingleAsync(item => item.Id == importId);
        Assert.Equal(total, import.ImportedRows);
        Assert.Equal(resolved, import.ResolvedRows);
        Assert.Equal(total - resolved, import.UnresolvedRows);
        Assert.Equal(cursor, import.Revision);
    }

    private async Task<Dictionary<string, LibraryTrackRecord>> SeedLocalImport(int groups, int occurrences)
    {
        await using var db = factory.CreateDbContext();
        var tracks = new Dictionary<string, LibraryTrackRecord>();
        for (var group = 0; group < groups; group++)
        {
            var recording = new CanonicalRecordingRecord { Id = Guid.NewGuid(), CreatedByUserId = owner };
            var track = new LibraryTrackRecord
            {
                Id = Guid.NewGuid(),
                OwnerUserId = owner,
                Protocol = Scope.Protocol,
                BackendInstanceId = Scope.BackendInstanceId,
                BackendLibraryId = "music",
                BackendItemId = "library-" + group,
                CanonicalRecordingId = recording.Id,
                Title = "Song " + group,
                Artist = "Artist",
                Album = "Album",
                DurationMilliseconds = 180000,
                Isrc = "library-isrc"
            };
            db.CanonicalRecordings.Add(recording);
            db.LibraryTracks.Add(track);
            tracks.Add(track.Title, track);
            for (var occurrence = 0; occurrence < occurrences; occurrence++) db.ListeningEvents.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = owner,
                Protocol = Scope.Protocol,
                BackendInstanceId = Scope.BackendInstanceId,
                SourceKind = "import",
                OccurrenceKey = Guid.NewGuid().ToString(),
                Title = track.Title,
                Artist = track.Artist,
                Album = track.Album,
                DurationMilliseconds = track.DurationMilliseconds,
                TrackReference = "history:" + track.Title,
                State = occurrence == 0 ? ListeningEventState.Skipped : ListeningEventState.Completed,
                Isrc = occurrence == 0 ? "original-isrc" : null,
                ProviderId = "spotify",
                ImportProvenance = $"history-import:{importId:N}:spotify:{occurrence}",
                ListenedAt = clock.UtcNow.AddDays(-1),
                UpdatedAt = clock.UtcNow.AddDays(-1)
            });
        }
        var import = await db.ListeningHistoryImports.SingleAsync();
        import.ImportedRows = import.UnresolvedRows = groups * occurrences;
        await db.SaveChangesAsync();
        return tracks;
    }

    private ResolutionWrites ObserveWrites()
    {
        var probe = new ResolutionWrites();
        factory = new(new DbContextOptionsBuilder<AllstarrDbContext>(database.Options).AddInterceptors(probe).Options);
        return probe;
    }

    private sealed class ResolutionWrites : DbCommandInterceptor
    {
        public int CursorWrites { get; private set; }
        public int TrackedEvents { get; private set; }
        private int eventWrites;
        public Action<int>? AfterEventWrite { get; set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("UPDATE \"durable_jobs\"", StringComparison.Ordinal))
            {
                CursorWrites++;
                TrackedEvents = Math.Max(TrackedEvents, eventData.Context!.ChangeTracker.Entries<ListeningEventRecord>().Count());
            }
            return ValueTask.FromResult(result);
        }

        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("UPDATE \"listening_events\"", StringComparison.Ordinal))
                AfterEventWrite?.Invoke(++eventWrites);
            return ValueTask.FromResult(result);
        }
    }
}

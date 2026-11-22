using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Intelligence;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed partial class TrackIdentityServiceTests
{
    [Fact]
    public async Task Source_with_a_known_isrc_joins_the_recording_that_already_carries_it()
    {
        var actor = Actor(_userA);
        var first = await _service.RememberSourceAsync(Context(actor, "deezer"), Track("deezer", "first"),
            "ext-deezer-song-first", ProviderIdentityScope.Catalog);
        // A song first remembered without its ISRC learns it on a later play.
        Assert.Equal(first, await _service.RememberSourceAsync(Context(actor, "deezer"), Track("deezer", "first"),
            "ext-deezer-song-first", ProviderIdentityScope.Catalog, "USAAA2600010"));
        var second = await _service.RememberSourceAsync(Context(actor, "qobuz"), Track("qobuz", "second"),
            "ext-qobuz-song-second", ProviderIdentityScope.Catalog, "US-AAA-26-00010");
        Assert.Equal(first, second);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(["isrc:USAAA2600010"], await IdentifierPairs(db, first));
        Assert.Single(await db.CanonicalRecordings.Where(item => item.MergedIntoId == null).ToArrayAsync());
    }

    [Fact]
    public async Task Shared_isrc_job_merges_a_group_into_its_library_copy_and_a_second_run_does_nothing()
    {
        var actor = Actor(_userA);
        var older = await _service.RememberSourceAsync(Context(actor, "deezer"), Track("deezer", "album"),
            "ext-deezer-song-album", ProviderIdentityScope.Catalog);
        _clock.Advance(TimeSpan.FromMinutes(1));
        var library = await _service.RememberSourceAsync(Context(actor, "qobuz"), Track("qobuz", "single"),
            "ext-qobuz-song-single", ProviderIdentityScope.Catalog);
        var alone = await _service.RememberSourceAsync(Context(actor, "qobuz"), Track("qobuz", "other"),
            "ext-qobuz-song-other", ProviderIdentityScope.Catalog);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            for (var index = 0; index < 3; index++) db.ListeningEvents.Add(Play(older, "usaaa2600020", 200000, $"older-{index}"));
            db.ListeningEvents.Add(Play(library, "USAAA2600020", 203000, "library-0"));
            db.ListeningEvents.Add(Play(alone, "USAAA2600021", 180000, "alone-0"));
            db.LibraryTracks.Add(LibraryCopy(library, 201000));
            await db.SaveChangesAsync();
        }

        var details = await RunIsrcMergeAsync();
        Assert.Equal(1, details.GetProperty("groupsFound").GetInt32());
        Assert.Equal(1, details.GetProperty("groupsMerged").GetInt32());
        Assert.Equal(1, details.GetProperty("recordingsMerged").GetInt32());
        Assert.Equal(1, details.GetProperty("isrcsRecorded").GetInt32());
        await using (var verify = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(library, (await verify.CanonicalRecordings.SingleAsync(item => item.Id == older)).MergedIntoId);
            Assert.Equal(4, await verify.ListeningEvents.CountAsync(item => item.CanonicalRecordingId == library));
            Assert.Equal(library, (await verify.LibraryTracks.SingleAsync()).CanonicalRecordingId);
            Assert.Equal(["isrc:USAAA2600020"], await IdentifierPairs(verify, library));
            Assert.Equal(["isrc:USAAA2600021"], await IdentifierPairs(verify, alone));
        }
        // A heart is saved by the song's id, which now names the kept recording.
        Assert.Equal(library, (await _service.ResolveRecordingAsync(actor, "ext-deezer-song-album"))?.Id);
        Assert.Equal(library, (await _service.ResolveAsync(Context(actor, "deezer"), Track("deezer", "album")))?.CanonicalRecordingId);

        var options = new DurableJobOptions();
        var jobs = new DurableJobQueue(_factory, options, new JobPayloadPolicy(options), _clock);
        Assert.Equal(0, await new IsrcRecordingMergeQueue(_factory, jobs).EnqueuePendingAsync(default));
        await using var after = await _factory.CreateDbContextAsync();
        Assert.Single(await after.AuditEvents.Where(item => item.Action == IsrcRecordingMergeQueue.CompletionAction).ToArrayAsync());
    }

    [Fact]
    public async Task Shared_isrc_job_skips_and_counts_refused_and_length_mismatched_groups()
    {
        var actor = Actor(_userA);
        var first = (await _service.CreateRecordingAsync(actor, "first", null, "16ba7915-2acf-42b2-8c87-ed67090dca91")).Recording.Id;
        var second = (await _service.CreateRecordingAsync(actor, "second", null, "26ba7915-2acf-42b2-8c87-ed67090dca92")).Recording.Id;
        var edit = (await _service.CreateRecordingAsync(actor, "edit")).Recording.Id;
        var full = (await _service.CreateRecordingAsync(actor, "full")).Recording.Id;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.ListeningEvents.AddRange(Play(first, "USAAA2600030", null, "first"), Play(second, "USAAA2600030", null, "second"),
                Play(edit, "USAAA2600031", 180000, "edit"), Play(full, "USAAA2600031", 180000 + IsrcRecordingMergeJobHandler.LengthToleranceMilliseconds + 1000, "full"));
            await db.SaveChangesAsync();
        }

        var details = await RunIsrcMergeAsync();
        Assert.Equal(2, details.GetProperty("groupsFound").GetInt32());
        Assert.Equal(0, details.GetProperty("groupsMerged").GetInt32());
        Assert.Equal(1, details.GetProperty("skipped").GetProperty("conflicts").GetInt32());
        Assert.Equal(1, details.GetProperty("skipped").GetProperty("lengths").GetInt32());
        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Equal(4, await verify.CanonicalRecordings.CountAsync(item => item.MergedIntoId == null));
        Assert.Empty(await verify.RecordingIdentifiers.Where(item => item.Kind == RecordingIdentifierKinds.Isrc).ToArrayAsync());
    }

    [Fact]
    public async Task Shared_isrc_job_continues_where_an_interrupted_run_stopped()
    {
        var actor = Actor(_userA);
        var pairs = new List<(Guid Kept, Guid Merged)>();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            for (var index = 0; index < 2; index++)
            {
                var kept = (await _service.CreateRecordingAsync(actor, $"kept-{index}")).Recording.Id;
                _clock.Advance(TimeSpan.FromSeconds(1));
                var merged = (await _service.CreateRecordingAsync(actor, $"merged-{index}")).Recording.Id;
                db.ListeningEvents.AddRange(Play(kept, $"USAAA260004{index}", 200000, $"kept-{index}"),
                    Play(merged, $"USAAA260004{index}", 200000, $"merged-{index}"));
                pairs.Add((kept, merged));
            }
            await db.SaveChangesAsync();
        }
        var options = new DurableJobOptions();
        var jobs = new DurableJobQueue(_factory, options, new JobPayloadPolicy(options), _clock);
        Assert.Equal(1, await new IsrcRecordingMergeQueue(_factory, jobs).EnqueuePendingAsync(default));
        var claim = await jobs.ClaimNextAsync("isrc-merge-worker", [IsrcRecordingMergeQueue.JobType]);
        var execution = new DurableJobExecutionContext(claim!, Mock.Of<IServiceProvider>());
        var first = await new IsrcRecordingMergeJobHandler(_service, _factory, _clock, batchSize: 1).ExecuteAsync(execution, default);
        Assert.Equal(DurableJobCompletionKind.Deferred, first.Kind);
        await using (var midway = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(pairs[0].Kept, (await midway.CanonicalRecordings.SingleAsync(item => item.Id == pairs[0].Merged)).MergedIntoId);
            Assert.Null((await midway.CanonicalRecordings.SingleAsync(item => item.Id == pairs[1].Merged)).MergedIntoId);
        }

        // The server restarts: a new handler picks up from the saved progress and keeps the counts so far.
        var restarted = new IsrcRecordingMergeJobHandler(new TrackIdentityService(_factory, _storageState, _clock), _factory, _clock, batchSize: 1);
        DurableJobCompletion result;
        var rounds = 0;
        do result = await restarted.ExecuteAsync(execution, default);
        while (result.Kind == DurableJobCompletionKind.Deferred && ++rounds < 10);
        Assert.Equal(DurableJobCompletionKind.Succeeded, result.Kind);
        await using var verify = await _factory.CreateDbContextAsync();
        Assert.All(pairs, pair => Assert.Equal(pair.Kept,
            verify.CanonicalRecordings.Single(item => item.Id == pair.Merged).MergedIntoId));
        var audit = await verify.AuditEvents.SingleAsync(item => item.Action == IsrcRecordingMergeQueue.CompletionAction);
        using var details = JsonDocument.Parse(audit.DetailsJson);
        Assert.Equal(2, details.RootElement.GetProperty("groupsFound").GetInt32());
        Assert.Equal(2, details.RootElement.GetProperty("groupsMerged").GetInt32());
    }

    private async Task<JsonElement> RunIsrcMergeAsync()
    {
        var options = new DurableJobOptions();
        var jobs = new DurableJobQueue(_factory, options, new JobPayloadPolicy(options), _clock);
        Assert.Equal(1, await new IsrcRecordingMergeQueue(_factory, jobs).EnqueuePendingAsync(default));
        var claim = await jobs.ClaimNextAsync("isrc-merge-worker", [IsrcRecordingMergeQueue.JobType]);
        Assert.NotNull(claim);
        var execution = new DurableJobExecutionContext(claim!, Mock.Of<IServiceProvider>());
        var handler = new IsrcRecordingMergeJobHandler(_service, _factory, _clock);
        DurableJobCompletion result;
        var rounds = 0;
        do result = await handler.ExecuteAsync(execution, default);
        while (result.Kind == DurableJobCompletionKind.Deferred && ++rounds < 10);
        Assert.Equal(DurableJobCompletionKind.Succeeded, result.Kind);
        await using var db = await _factory.CreateDbContextAsync();
        var audits = await db.AuditEvents.CountAsync();
        // Running it again after it finished changes nothing.
        Assert.Equal(DurableJobCompletionKind.Succeeded, (await handler.ExecuteAsync(execution, default)).Kind);
        Assert.Equal(audits, await db.AuditEvents.CountAsync());
        var audit = await db.AuditEvents.AsNoTracking().SingleAsync(item => item.Action == IsrcRecordingMergeQueue.CompletionAction);
        return JsonDocument.Parse(audit.DetailsJson).RootElement.Clone();
    }

    private ListeningEventRecord Play(Guid recordingId, string isrc, long? duration, string key) => new()
    {
        Id = Guid.CreateVersion7(),
        OwnerUserId = _userA,
        Protocol = "jellyfin",
        BackendInstanceId = "fixture",
        OccurrenceKey = key,
        CanonicalRecordingId = recordingId,
        TrackReference = recordingId.ToString("N"),
        Isrc = isrc,
        DurationMilliseconds = duration,
        UpdatedAt = _clock.UtcNow
    };

    private LibraryTrackRecord LibraryCopy(Guid recordingId, long duration) => new()
    {
        Id = Guid.CreateVersion7(),
        OwnerUserId = _userA,
        Protocol = "jellyfin",
        BackendInstanceId = "fixture",
        BackendLibraryId = "music",
        BackendItemId = "native",
        CanonicalRecordingId = recordingId,
        Title = "Recording",
        Artist = "Artist",
        DurationMilliseconds = duration,
        IndexedAt = _clock.UtcNow,
        UpdatedAt = _clock.UtcNow,
        SourceModifiedAt = _clock.UtcNow
    };
}

using System.Data.Common;
using allstarr.Core.Capabilities;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace allstarr.Tests;

public sealed partial class TrackIdentityServiceTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task LibraryRecordingLink_LargeBackfillAllowsAConcurrentDurableWrite(int boundaryKind)
    {
        await SeedLinkBatchAsync(76, 3000);
        var boundary = new LibraryLinkBoundary();
        var factory = LinkBatchFactory(boundaryKind == 2
            ? new LibraryLinkCommitBarrier(boundary) : new LibraryLinkReadBarrier(boundary, boundaryKind));
        var service = new TrackIdentityService(factory, _storageState, _clock);
        var options = new DurableJobOptions();
        var jobs = new DurableJobQueue(_factory, options, new JobPayloadPolicy(options), _clock);
        await jobs.EnqueueAsync(new DurableJobEnqueueRequest<int>("fixture.write", "warmup", 1, _userA));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var backfill = Task.Run(() => service.LinkLibraryRecordingsAsync(Actor(_userA), "large-backfill",
            cancellationToken: stop.Token), stop.Token);
        Task<DurableJobEnqueueResult>? write = null;
        try
        {
            await boundary.Opened.Task.WaitAsync(TimeSpan.FromSeconds(5), stop.Token);
            write = Task.Run(() => jobs.EnqueueAsync(new DurableJobEnqueueRequest<int>("fixture.write", "concurrent", 2, _userA), stop.Token));
            Assert.True((await write.WaitAsync(TimeSpan.FromSeconds(5), stop.Token)).Created);
            Assert.False(backfill.IsCompleted);
            if (boundaryKind == 2)
            {
                await using var db = await _factory.CreateDbContextAsync();
                Assert.Equal(25, await db.LibraryTracks.CountAsync(item => item.CanonicalRecordingId != null));
            }
        }
        finally
        {
            boundary.Resume.TrySetResult();
            await backfill;
            if (write != null) await write;
        }
        Assert.Equal(new LibraryRecordingLinkResult(76, 76, 0), await backfill);
    }

    [Theory]
    [InlineData(" us-rc1-00-00000 ", null)]
    [InlineData(null, "{61DC4FE7-F5E1-43B6-90D4-B568655ABA87}")]
    [InlineData(null, "{0x61dc4fe7,0xf5e1,0x43b6,{0x90,0xd4,0xb5,0x68,0x65,0x5a,0xba,0x87}}")]
    public async Task LibraryRecordingLink_BackfillRetainsNormalizedIdentifierEvidence(string? isrc, string? mbid)
    {
        await SeedLinkBatchAsync(1, 1);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var local = await db.LibraryTracks.SingleAsync();
            local.Isrc = isrc;
            local.MusicBrainzRecordingId = mbid;
            if (mbid != null)
                db.RecordingIdentifiers.Add(new()
                {
                    Id = Guid.CreateVersion7(),
                    RecordingId = (await db.CanonicalRecordings.SingleAsync()).Id,
                    Kind = RecordingIdentifierKinds.MusicBrainz,
                    Value = Guid.Parse(mbid).ToString("D"),
                    Source = "fixture",
                    CreatedAt = _clock.UtcNow
                });
            await db.SaveChangesAsync();
        }

        Assert.Equal(1, (await _service.LinkLibraryRecordingsAsync(Actor(_userA), "normalized-backfill")).Linked);
    }

    [Theory]
    [InlineData("Don't Stop")]
    [InlineData("Élan")]
    public async Task LibraryRecordingLink_RechecksNewNormalizedTitleCompetitors(string title)
    {
        var source = await SeedLinkSourceAsync();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var link = await db.ProviderTrackIdentities.SingleAsync();
            var payload = System.Text.Json.JsonSerializer.Serialize(new { Title = title, Artist = "Jung Kook", DurationMilliseconds = 210000 });
            var snapshot = await db.ExternalMetadataSnapshots.FirstAsync();
            db.ExternalMetadataSnapshots.Add(new()
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = _userA,
                ProviderTrackIdentityId = link.Id,
                BackendInstanceId = "fixture",
                BackendPrincipalId = _userA.ToString("N"),
                Protocol = "jellyfin",
                ProviderId = link.ProviderId,
                ResourceKind = "track",
                ExternalIdHash = link.ExternalIdHash,
                SnapshotVersion = 2,
                ProviderRevision = "fixture",
                PayloadJson = payload,
                PayloadSha256 = CanonicalCatalogKeys.Hash(payload),
                CorrelationId = "title",
                RetrievedAt = snapshot.RetrievedAt.AddSeconds(1)
            });
            await db.SaveChangesAsync();
        }
        await SeedLinkLibraryTrackAsync("Jung Kook", null, 210000, title: title);
        var boundary = new BeforeLibraryLinkWriter(async () =>
            await SeedLinkLibraryTrackAsync("Jung Kook", null, 210000, title: title));
        var service = new TrackIdentityService(LinkBatchFactory(boundary), _storageState, _clock);

        var result = await service.LinkLibraryRecordingsAsync(Actor(_userA), "new-title-competitor", source);

        Assert.Equal(new LibraryRecordingLinkResult(1, 0, 1), result);
        await using var verify = await _factory.CreateDbContextAsync();
        Assert.All(await verify.LibraryTracks.ToArrayAsync(), item => Assert.Null(item.CanonicalRecordingId));
    }

    [Fact]
    public async Task LibraryRecordingLink_DoesNotCountLaterRecordingsMergedWithoutAnInitialPair()
    {
        await SeedLinkBatchAsync(2, 1);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var later = await db.ProviderTrackIdentities.SingleAsync(item => item.ExternalId == "batch-1");
            (await db.CanonicalRecordings.SingleAsync(item => item.Id == later.CanonicalRecordingId)).CreatedAt = _clock.UtcNow.AddMinutes(1);
            (await db.LibraryTracks.SingleAsync()).CanonicalRecordingId = later.CanonicalRecordingId;
            await db.SaveChangesAsync();
        }

        Assert.Equal(new LibraryRecordingLinkResult(1, 1, 0),
            await _service.LinkLibraryRecordingsAsync(Actor(_userA), "merged-later"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(25)]
    [InlineData(26)]
    [InlineData(76)]
    public async Task LibraryRecordingLink_SavesAtMostTwentyFiveRecordingsPerTransaction(int count)
    {
        await SeedLinkBatchAsync(count, count);
        var batches = new LibraryLinkSaveObserver();
        var service = new TrackIdentityService(LinkBatchFactory(batches), _storageState, _clock);

        Assert.Equal(new LibraryRecordingLinkResult(count, count, 0),
            await service.LinkLibraryRecordingsAsync(Actor(_userA), "bounded-backfill"));

        Assert.Equal((count + 24) / 25, batches.Sizes.Count);
        Assert.All(batches.Sizes, size => Assert.InRange(size, 1, 25));
        Assert.Equal(count, batches.Sizes.Sum());
        Assert.Equal(batches.Sizes.Count, batches.Transactions.Distinct().Count());
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(count, await db.AuditEvents.CountAsync(item => item.Action == "canonical-recording.library-link"));
        Assert.Equal(0, (await service.LinkLibraryRecordingsAsync(Actor(_userA), "repeat-backfill")).Linked);
    }

    [Theory]
    [InlineData("manual")]
    [InlineData("metadata")]
    [InlineData("identity")]
    [InlineData("ambiguity")]
    public async Task LibraryRecordingLink_RevalidatesDecisionsAndEvidenceBeforeWriting(string change)
    {
        await SeedLinkBatchAsync(1, 1);
        var boundary = new BeforeLibraryLinkWriter(async () =>
        {
            await using var db = await _factory.CreateDbContextAsync();
            var local = await db.LibraryTracks.SingleAsync();
            if (change == "manual")
            {
                var snapshot = new ExternalMetadataSnapshotRecord
                {
                    Id = Guid.CreateVersion7(),
                    OwnerUserId = _userA,
                    BackendInstanceId = "fixture",
                    BackendPrincipalId = _userA.ToString("N"),
                    Protocol = "jellyfin",
                    ProviderId = "apple-download",
                    ResourceKind = "track",
                    ExternalIdHash = CanonicalCatalogKeys.Hash("batch-0"),
                    SnapshotVersion = 1,
                    ProviderRevision = "fixture",
                    PayloadJson = "{}",
                    PayloadSha256 = CanonicalCatalogKeys.Hash("{}"),
                    CorrelationId = "manual",
                    RetrievedAt = _clock.UtcNow
                };
                db.ExternalMetadataSnapshots.Add(snapshot);
                db.ManualTrackOverrides.Add(new()
                {
                    Id = Guid.CreateVersion7(),
                    OwnerUserId = _userA,
                    ExternalSnapshotId = snapshot.Id,
                    SourceProviderId = "apple-download",
                    SourceExternalIdHash = CanonicalCatalogKeys.Hash("batch-0"),
                    LibraryTrackId = local.Id,
                    Decision = ManualOverrideDecision.Reject,
                    Reason = "Saved choice",
                    DecisionVersion = 1,
                    CreatedAt = _clock.UtcNow
                });
            }
            else if (change == "metadata") local.Isrc = "USRC19999999";
            else if (change == "identity")
                (await db.ProviderTrackIdentities.SingleAsync()).Verification = ProviderIdentityVerification.Pinned;
            else
            {
                local.MusicBrainzRecordingId = Guid.NewGuid().ToString("D");
                db.LibraryTracks.Add(new()
                {
                    Id = Guid.CreateVersion7(),
                    OwnerUserId = _userA,
                    BackendLibraryId = "music",
                    Protocol = "jellyfin",
                    BackendInstanceId = "fixture",
                    BackendItemId = "competitor",
                    FilePath = "/music/competitor.flac",
                    Title = local.Title,
                    Artist = local.Artist,
                    Isrc = local.Isrc,
                    MusicBrainzRecordingId = Guid.NewGuid().ToString("D"),
                    IndexedAt = _clock.UtcNow,
                    SourceModifiedAt = _clock.UtcNow,
                    UpdatedAt = _clock.UtcNow
                });
            }
            local.Revision++;
            await db.SaveChangesAsync();
        });
        var service = new TrackIdentityService(LinkBatchFactory(boundary), _storageState, _clock);

        Assert.Equal(0, (await service.LinkLibraryRecordingsAsync(Actor(_userA), "revalidate")).Linked);

        Assert.InRange(boundary.Calls, 1, 2);
        await using var verify = await _factory.CreateDbContextAsync();
        Assert.All(await verify.LibraryTracks.ToArrayAsync(), item => Assert.Null(item.CanonicalRecordingId));
        Assert.Empty(await verify.AuditEvents.Where(item => item.Action == "canonical-recording.library-link").ToArrayAsync());
    }

    private TestDbContextFactory LinkBatchFactory(params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<AllstarrDbContext>().UseSqlite(_database.ConnectionString)
            .AddInterceptors(new SqlitePragmaInterceptor()).AddInterceptors(interceptors).Options);

    private async Task SeedLinkBatchAsync(int recordings, int tracks)
    {
        await using var db = await _factory.CreateDbContextAsync();
        for (var i = 0; i < recordings; i++)
        {
            var recording = TrackIdentityService.CreateProvisionalRecording(_userA, _clock.UtcNow,
                $"USRC1{i:0000000}");
            db.CanonicalRecordings.Add(recording);
            db.ProviderTrackIdentities.Add(new()
            {
                Id = Guid.CreateVersion7(),
                CanonicalRecordingId = recording.Id,
                ProviderId = "apple-download",
                ResourceKind = ProviderResourceKind.Track,
                Scope = ProviderIdentityScope.Catalog,
                ExternalId = $"batch-{i}",
                ExternalIdHash = CanonicalCatalogKeys.Hash($"batch-{i}"),
                Verification = ProviderIdentityVerification.Verified,
                VerificationMethod = "source-use",
                DecisionVersion = 1,
                VerifiedAt = _clock.UtcNow,
                CreatedAt = _clock.UtcNow,
                UpdatedAt = _clock.UtcNow
            });
        }
        for (var i = 0; i < tracks; i++)
            db.LibraryTracks.Add(new()
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = _userA,
                BackendLibraryId = "music",
                Protocol = "jellyfin",
                BackendInstanceId = "fixture",
                BackendItemId = $"local-{i}",
                FilePath = $"/music/{i}.flac",
                Title = $"Track {i}",
                Artist = "Fixture",
                Isrc = $"USRC1{i:0000000}",
                IndexedAt = _clock.UtcNow,
                SourceModifiedAt = _clock.UtcNow,
                UpdatedAt = _clock.UtcNow
            });
        await db.SaveChangesAsync();
    }

    private sealed class LibraryLinkBoundary
    {
        public TaskCompletionSource Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class LibraryLinkReadBarrier(LibraryLinkBoundary boundary, int readsToSkip) : DbCommandInterceptor
    {
        private int entered;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"library_tracks\"", StringComparison.Ordinal) &&
                Interlocked.Increment(ref entered) == readsToSkip + 1)
            {
                boundary.Opened.TrySetResult();
                await boundary.Resume.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    private sealed class LibraryLinkCommitBarrier(LibraryLinkBoundary boundary) : DbTransactionInterceptor
    {
        private int entered;

        public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref entered, 1) == 0)
            {
                boundary.Opened.TrySetResult();
                await boundary.Resume.Task.WaitAsync(cancellationToken);
            }
        }
    }

    private sealed class LibraryLinkSaveObserver : SaveChangesInterceptor
    {
        public List<int> Sizes { get; } = [];
        public List<Guid> Transactions { get; } = [];

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var db = (AllstarrDbContext)eventData.Context!;
            var count = db.ChangeTracker.Entries<AuditEventRecord>().Count(item => item.State == EntityState.Added &&
                item.Entity.Action == "canonical-recording.library-link");
            if (count > 0)
            {
                Sizes.Add(count);
                Transactions.Add(db.Database.CurrentTransaction!.TransactionId);
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class BeforeLibraryLinkWriter(Func<Task> change) : DbTransactionInterceptor
    {
        public int Calls { get; private set; }

        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection,
            TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            if (++Calls == 1) await change();
            return result;
        }
    }
}

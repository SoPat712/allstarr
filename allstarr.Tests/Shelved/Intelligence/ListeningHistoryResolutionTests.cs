using System.Collections.Immutable;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Enrichment;
using allstarr.Core.Intelligence;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Music;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Releases;
using allstarr.Core.Routing;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed class ListeningHistoryResolutionTests : IAsyncLifetime
{
    private SqliteTestDatabase database = null!;
    private Factory factory = null!;
    private DurableJobQueue jobs = null!;
    private readonly Clock clock = new();
    private readonly Guid owner = Guid.NewGuid(), importId = Guid.NewGuid();
    private readonly Mock<IProtocolProviderGateway> gateway = new();
    private readonly Mock<IBackendLibraryAccessResolver> access = new();
    private readonly Mock<ITrackIdentityService> identities = new();
    private readonly Mock<IEffectiveProviderPolicyResolver> matchingPolicies = new();
    private readonly ServiceProvider services = new ServiceCollection().BuildServiceProvider();
    private readonly ManualTimeProvider time = new();
    private IntelligenceScope Scope => new(owner, "jellyfin", "fixture");

    public async Task InitializeAsync()
    {
        database = await SqliteTestDatabase.CreateAsync();
        factory = new(database.Options);
        var options = new DurableJobOptions();
        jobs = new(factory, options, new(options), clock);
        await using var db = factory.CreateDbContext();
        db.Users.Add(new() { Id = owner, BackendType = Scope.Protocol, BackendInstanceId = Scope.BackendInstanceId, BackendPrincipalId = "listener" });
        db.ListeningHistoryImports.Add(new()
        {
            Id = importId,
            OwnerUserId = owner,
            Protocol = Scope.Protocol,
            BackendInstanceId = Scope.BackendInstanceId,
            SizeBytes = 1,
            ContentSha256 = new string('a', 64),
            PreviewRevision = new string('b', 64),
            State = ListeningHistoryImportState.Completed,
            ExpiresAt = clock.UtcNow.AddDays(1)
        });
        await db.SaveChangesAsync();
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming)).Returns(["deezer"]);
        gateway.Setup(item => item.SearchAutomaticSongsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 15)).ReturnsAsync([]);
        access.Setup(item => item.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackendLibraryAccess(true, ["music"]));
        matchingPolicies.Setup(item => item.ResolveForUserAsync(owner, It.IsAny<CancellationToken>())).ReturnsAsync(new EffectiveProviderPolicySnapshot(
            ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty, ImmutableHashSet.Create("spotify"), "Lossless", .07));
    }

    private ListeningHistoryResolution Handler() => new(factory, jobs,
        new(gateway.Object, new(), NullLogger<TrackMatchPlayableSearch>.Instance, matchingPolicies.Object), new(), gateway.Object,
        access.Object, identities.Object, Mixes(), clock, new(new DurableJobOptions()), time);

    private PersonalMixService Mixes()
    {
        var tags = new SharedTagStore(factory, clock);
        return new(factory, clock, tags, new(factory, tags, clock), new(gateway.Object), gateway.Object,
            new NewReleasesService(factory, clock, new Mock<IProviderRouter>().Object, new Mock<IDurableRuntimeSettings>().Object),
            new Mock<IDurableRuntimeSettings>().Object, new Mock<IEffectiveProviderPolicyResolver>().Object, access.Object);
    }

    private async Task Add(string title, int plays = 1, string source = "import", Guid? listener = null, string? providerReference = null)
    {
        await using var db = factory.CreateDbContext();
        for (var index = 0; index < plays; index++) db.ListeningEvents.Add(new()
        {
            Id = Guid.NewGuid(),
            OwnerUserId = listener ?? owner,
            Protocol = Scope.Protocol,
            BackendInstanceId = Scope.BackendInstanceId,
            OccurrenceKey = Guid.NewGuid().ToString(),
            State = ListeningEventState.Completed,
            Title = title,
            Artist = "Artist",
            Album = "Album",
            TrackReference = "history:" + title,
            SourceKind = source,
            ProviderId = "spotify",
            ProviderTrackReference = providerReference,
            ImportProvenance = $"history-import:{importId:N}:spotify:1",
            ListenedAt = clock.UtcNow.AddDays(-1),
            UpdatedAt = clock.UtcNow.AddDays(-1)
        });
        var import = await db.ListeningHistoryImports.SingleAsync();
        if (source == "import" && listener == null) { import.ImportedRows += plays; import.UnresolvedRows += plays; }
        await db.SaveChangesAsync();
    }

    private async Task<LibraryTrackRecord> Library(string title, string library = "music")
    {
        await using var db = factory.CreateDbContext();
        var recording = new CanonicalRecordingRecord { Id = Guid.NewGuid(), CreatedByUserId = owner };
        db.CanonicalRecordings.Add(recording);
        var track = new LibraryTrackRecord
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner,
            Protocol = Scope.Protocol,
            BackendInstanceId = Scope.BackendInstanceId,
            BackendLibraryId = library,
            BackendItemId = Guid.NewGuid().ToString(),
            CanonicalRecordingId = recording.Id,
            Title = title,
            Artist = "Artist",
            Album = "Album",
            DurationMilliseconds = 180000
        };
        db.LibraryTracks.Add(track);
        await db.SaveChangesAsync();
        return track;
    }

    private async Task<DurableJobCompletion> Run()
    {
        var claim = await jobs.ClaimNextAsync("history-test", [ListeningHistoryResolution.JobTypeName]);
        Assert.NotNull(claim);
        var running = Handler().ExecuteAsync(new(claim, services), default);
        while (!running.IsCompleted) { time.Advance(TimeSpan.FromSeconds(2)); await Task.Delay(1); }
        var result = await running;
        await jobs.CompleteAsync(claim, result);
        return result;
    }

    [Fact]
    public async Task LibraryFirst_ResolvesAllOccurrencesAndCountersWithoutTouchingOtherEvents()
    {
        var track = await Library("Song");
        await Add("SONG", 3, providerReference: "spotify:track:1111111111111111111111");
        await Add("Song", source: "protocol");
        var secondImportId = Guid.NewGuid();
        await using (var write = factory.CreateDbContext())
        {
            var firstImport = await write.ListeningHistoryImports.SingleAsync();
            firstImport.ImportedRows--;
            firstImport.UnresolvedRows--;
            write.ListeningHistoryImports.Add(new()
            {
                Id = secondImportId,
                OwnerUserId = owner,
                Protocol = Scope.Protocol,
                BackendInstanceId = Scope.BackendInstanceId,
                SizeBytes = 1,
                ContentSha256 = new string('c', 64),
                PreviewRevision = new string('d', 64),
                State = ListeningHistoryImportState.Completed,
                ImportedRows = 1,
                UnresolvedRows = 1
            });
            var skipped = await write.ListeningEvents.FirstAsync(item => item.SourceKind == "import");
            skipped.State = ListeningEventState.Skipped;
            skipped.ListenedAt = null;
            skipped.ImportProvenance = $"history-import:{secondImportId:N}:spotify:1";
            await write.SaveChangesAsync();
        }
        await ListeningHistoryResolution.EnqueueAsync(factory, jobs, Scope, clock.UtcNow, default);
        Assert.Equal(DurableJobCompletionKind.Succeeded, (await Run()).Kind);
        await using var db = factory.CreateDbContext();
        Assert.All(await db.ListeningEvents.Where(item => item.SourceKind == "import").ToArrayAsync(), item =>
        {
            Assert.Equal(track.CanonicalRecordingId, item.CanonicalRecordingId);
            Assert.Equal(track.Id, item.LibraryTrackId);
            Assert.Equal(track.BackendItemId, item.TrackReference);
        });
        Assert.Single(await db.ListeningEvents.Where(item => item.State == ListeningEventState.Skipped).ToArrayAsync());
        var live = await db.ListeningEvents.SingleAsync(item => item.SourceKind == "protocol");
        Assert.Null(live.CanonicalRecordingId);
        Assert.Equal("history:Song", live.TrackReference);
        Assert.Equal(0, live.Revision);
        var import = await db.ListeningHistoryImports.SingleAsync(item => item.Id == importId);
        Assert.Equal(2, import.ResolvedRows);
        Assert.Equal(0, import.UnresolvedRows);
        var second = await db.ListeningHistoryImports.SingleAsync(item => item.Id == secondImportId);
        Assert.Equal(1, second.ResolvedRows);
        Assert.Equal(0, second.UnresolvedRows);
        gateway.Verify(item => item.SearchAutomaticSongsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 15), Times.Never);
        Assert.Empty(await db.FavoriteStates.ToArrayAsync());
        Assert.Empty(await db.PlaylistLinks.ToArrayAsync());
        Assert.Empty(await db.ManualTrackOverrides.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderMatch_UsesExistingSearchAndPersistsPlayableIdentityForEveryOccurrence(bool sharedAccount)
    {
        await Add("Song", 3);
        var song = new Song
        {
            Id = "ext-deezer-song-found",
            ExternalProvider = "deezer",
            ExternalId = "found",
            Title = "Song",
            Artist = "Artist",
            Album = "Album",
            Duration = 180
        };
        gateway.Setup(item => item.SearchAutomaticSongsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 15)).ReturnsAsync([song]);
        gateway.Setup(item => item.RememberTrackAsync(It.IsAny<ProtocolExecutionContext>(), song.Id)).Returns(async () =>
        {
            await using var db = factory.CreateDbContext();
            Guid? accountId = sharedAccount ? Guid.NewGuid() : null;
            if (accountId != null) db.ProviderAccounts.Add(new() { Id = accountId.Value, ProviderId = "deezer", Enabled = true });
            song.CanonicalRecordingId = Guid.NewGuid();
            db.CanonicalRecordings.Add(new() { Id = song.CanonicalRecordingId.Value, CreatedByUserId = owner });
            db.ProviderTrackIdentities.Add(new()
            {
                Id = Guid.NewGuid(),
                CanonicalRecordingId = song.CanonicalRecordingId.Value,
                ProviderId = "deezer",
                ResourceKind = ProviderResourceKind.Track,
                ExternalId = "found",
                ExternalIdHash = ListeningHistoryImportService.Hash("found"),
                Scope = sharedAccount ? ProviderIdentityScope.Account : ProviderIdentityScope.Catalog,
                ProviderAccountId = accountId,
                Verification = ProviderIdentityVerification.Verified,
                VerificationMethod = "source-use",
                DecisionVersion = 1
            });
            await db.SaveChangesAsync();
        });
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "found")).ReturnsAsync(() => song);
        await ListeningHistoryResolution.EnqueueAsync(factory, jobs, Scope, clock.UtcNow, default);
        await Run();
        await using var read = factory.CreateDbContext();
        Assert.All(await read.ListeningEvents.ToArrayAsync(), item =>
        {
            Assert.Equal(song.CanonicalRecordingId, item.CanonicalRecordingId);
            Assert.Equal(song.Id, item.TrackReference);
            Assert.NotNull(item.ProviderTrackIdentityId);
            Assert.Equal("deezer", item.ProviderId);
            Assert.Equal(sharedAccount, item.ProviderAccountId != null);
        });
        gateway.Verify(item => item.RememberTrackAsync(It.IsAny<ProtocolExecutionContext>(), song.Id), Times.Once);
    }

    [Theory]
    [InlineData("Song (Live)")]
    [InlineData("Song (Remix)")]
    [InlineData("Song (Clean)")]
    public async Task EditionMismatch_RemainsUnresolved(string edition)
    {
        await Library("Song");
        await Add(edition);
        gateway.Setup(item => item.SearchAutomaticSongsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 15))
            .ReturnsAsync([new Song { ExternalProvider = "deezer", ExternalId = "studio", Title = "Song", Artist = "Artist", Album = "Album", Duration = 180 }]);
        await ListeningHistoryResolution.EnqueueAsync(factory, jobs, Scope, clock.UtcNow, default);
        await Run();
        await using var db = factory.CreateDbContext();
        Assert.Null((await db.ListeningEvents.SingleAsync()).CanonicalRecordingId);
    }

    [Fact]
    public async Task UnmatchedSong_IsRecordedAndRetriedOnlyAfterOneWeek()
    {
        await Add("Missing");
        await ListeningHistoryResolution.EnqueueAsync(factory, jobs, Scope, clock.UtcNow, default);
        await Run();
        gateway.Invocations.Clear();
        clock.UtcNow += TimeSpan.FromDays(6);
        await Handler().RecoverAsync(default);
        await using (var db = factory.CreateDbContext())
        {
            Assert.Single(await db.ExternalMetadataSnapshots.Where(item => item.ResourceKind == "history-resolution").ToArrayAsync());
            Assert.Single(await db.Jobs.Where(item => item.Type == ListeningHistoryResolution.JobTypeName).ToArrayAsync());
        }
        clock.UtcNow += TimeSpan.FromDays(1);
        await Handler().RecoverAsync(default);
        await Run();
        gateway.Verify(item => item.SearchAutomaticSongsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 15), Times.AtLeastOnce);
    }

    [Fact]
    public async Task RunCap_ResumesSavedCursorWithMostPlayedFirstAfterRestart()
    {
        for (var index = 0; index < 101; index++) await Add("Missing " + index, index == 100 ? 3 : 1);
        await ListeningHistoryResolution.EnqueueAsync(factory, jobs, Scope, clock.UtcNow, default);
        Assert.Equal(DurableJobCompletionKind.Deferred, (await Run()).Kind);
        await using (var db = factory.CreateDbContext())
        {
            var job = await db.Jobs.SingleAsync();
            Assert.Equal(100, JsonDocument.Parse(job.PayloadJson).RootElement.GetProperty("Cursor").GetInt32());
        }
        Assert.Contains("Missing 100", gateway.Invocations.First(item => item.Method.Name == "SearchAutomaticSongsAsync").Arguments[1].ToString());
        gateway.Invocations.Clear();
        clock.UtcNow += TimeSpan.FromMinutes(2);
        Assert.Equal(DurableJobCompletionKind.Succeeded, (await Run()).Kind);
        Assert.True(gateway.Invocations.Count(item => item.Method.Name == "SearchAutomaticSongsAsync") < 6);
    }

    [Fact]
    public async Task StartupBackfill_QueuesOnlyCurrentListenersWithUnresolvedImportsOnce()
    {
        await Add("Missing");
        await using (var db = factory.CreateDbContext())
        {
            foreach (var kind in new[] { "live", "resolved", "wrong-backend", "disabled", "empty" })
            {
                var user = new UserRecord
                {
                    Id = Guid.NewGuid(),
                    BackendType = Scope.Protocol,
                    BackendInstanceId = Scope.BackendInstanceId,
                    BackendPrincipalId = kind,
                    Enabled = kind != "disabled"
                };
                db.Users.Add(user);
                if (kind == "empty") continue;
                Guid? recording = null;
                if (kind == "resolved")
                {
                    recording = Guid.NewGuid();
                    db.CanonicalRecordings.Add(new() { Id = recording.Value, CreatedByUserId = user.Id });
                }
                db.ListeningEvents.Add(new()
                {
                    Id = Guid.NewGuid(),
                    OwnerUserId = user.Id,
                    Protocol = Scope.Protocol,
                    BackendInstanceId = kind == "wrong-backend" ? "other" : Scope.BackendInstanceId,
                    SourceKind = kind == "live" ? "protocol" : "import",
                    CanonicalRecordingId = recording,
                    State = ListeningEventState.Completed,
                    Title = "Song",
                    Artist = "Artist",
                    TrackReference = "history:song",
                    OccurrenceKey = Guid.NewGuid().ToString()
                });
            }
            await db.SaveChangesAsync();
        }
        await Handler().RecoverAsync(default);
        await Handler().RecoverAsync(default);
        await using var read = factory.CreateDbContext();
        var job = Assert.Single(await read.Jobs.ToArrayAsync());
        Assert.Equal(owner, job.OwnerUserId);
        Assert.Equal(ListeningHistoryResolution.JobTypeName, job.Type);
    }

    [Fact]
    public async Task InaccessibleLibrarySong_IsNotUsedForResolution()
    {
        await Library("Song", "private");
        await Add("Song");
        await Handler().RecoverAsync(default);
        await Run();
        await using var db = factory.CreateDbContext();
        Assert.Null((await db.ListeningEvents.SingleAsync()).LibraryTrackId);
        Assert.Null((await db.ListeningEvents.SingleAsync()).CanonicalRecordingId);
    }

    [Fact]
    public async Task UnavailableLibraryAccess_DefersWithoutProviderWorkOrNegativeReceipt()
    {
        await Library("Song");
        await Add("Song");
        access.Setup(item => item.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BackendLibraryAccess.Unavailable);
        await Handler().RecoverAsync(default);
        Assert.Equal(DurableJobCompletionKind.Deferred, (await Run()).Kind);
        await using var db = factory.CreateDbContext();
        Assert.Null((await db.ListeningEvents.SingleAsync()).CanonicalRecordingId);
        Assert.Empty(await db.ExternalMetadataSnapshots.ToArrayAsync());
        gateway.Verify(item => item.SearchAutomaticSongsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 15), Times.Never);
    }

    [Fact]
    public async Task LibraryWithoutCanonicalRecording_UsesExistingIdentityCreationAndKeepsOneRecording()
    {
        var track = await Library("Song");
        await using (var db = factory.CreateDbContext())
        {
            (await db.LibraryTracks.SingleAsync()).CanonicalRecordingId = null;
            await db.SaveChangesAsync();
        }
        var storage = new DurableStorageState(database.StorageOptions);
        storage.Set(DurableStorageReadiness.Ready, "fixture");
        var real = new TrackIdentityService(factory, storage, clock);
        identities.Setup(item => item.CreateRecordingAsync(It.IsAny<ProviderActorContext>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((ProviderActorContext actor, string correlation, string? isrc, string? mbid, CancellationToken token) =>
                real.CreateRecordingAsync(actor, correlation, isrc, mbid, token));
        await Add("Song", providerReference: "spotify:track:1111111111111111111111");
        await Handler().RecoverAsync(default);
        await Run();
        await Add("Song", providerReference: "spotify:track:2222222222222222222222");
        await Handler().RecoverAsync(default);
        await Run();
        await using var read = factory.CreateDbContext();
        var plays = await read.ListeningEvents.ToArrayAsync();
        Assert.All(plays, item => Assert.Equal(track.Id, item.LibraryTrackId));
        Assert.NotNull(plays[0].CanonicalRecordingId);
        Assert.Single(plays.Select(item => item.CanonicalRecordingId).Distinct());
    }

    public async Task DisposeAsync() { services.Dispose(); await database.DisposeAsync(); }
    private sealed class Clock : IPlatformClock { public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2026-10-29T12:00:00Z"); }
    private sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}

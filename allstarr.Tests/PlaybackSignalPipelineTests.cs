using allstarr.Core.Identity;
using allstarr.Core.Intelligence;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Playback;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace allstarr.Tests;

public sealed class PlaybackSignalPipelineTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "allstarr-playback", Guid.NewGuid().ToString("N"));
    private readonly Guid tenant = Guid.CreateVersion7();
    private readonly Guid user = Guid.CreateVersion7();
    private Factory factory = null!;
    private DurableJobQueue jobs = null!;
    private Clock clock = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(root); var options = new DbContextOptionsBuilder<AllstarrDbContext>().UseSqlite($"Data Source={Path.Combine(root, "playback.db")}").Options;
        factory = new(options); await using var db = await factory.CreateDbContextAsync(); await db.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow; db.Tenants.Add(new() { Id = tenant, Slug = "playback", Name = "Playback", CreatedAt = now });
        db.Users.Add(new() { Id = user, TenantId = tenant, DisplayName = "User", Status = PlatformUserStatus.Active, CreatedAt = now, UpdatedAt = now }); await db.SaveChangesAsync();
        var identity = Guid.CreateVersion7(); db.BackendIdentities.Add(new() { Id = identity, TenantId = tenant, UserId = user, BackendType = "jellyfin", BackendInstanceId = "backend", PrincipalId = "principal", CreatedAt = now, LastSeenAt = now });
        db.LibraryTracks.Add(new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant,
            OwnerUserId = user,
            BackendIdentityId = identity,
            LibraryScopeId = "music",
            Protocol = "jellyfin",
            BackendInstanceId = "backend",
            BackendItemId = "track-1",
            FilePath = "/media/track.flac",
            Title = "Track",
            Artist = "Artist",
            DurationMilliseconds = 120000,
            ProviderIdsJson = "{}",
            IndexedAt = now,
            SourceModifiedAt = now,
            UpdatedAt = now
        }); await db.SaveChangesAsync();
        clock = new() { UtcNow = now }; var jobOptions = new DurableJobOptions(); jobs = new(factory, jobOptions, new JobPayloadPolicy(jobOptions), clock);
    }

    [Fact]
    public async Task RepeatedAndRestartedSignalCreatesOneExactScopeJob()
    {
        var pipeline = new PlaybackSignalPipeline(jobs); var request = Signal(PlaybackTransition.Start, "track-1", 0);
        Assert.True(await pipeline.RecordAsync(request));
        Assert.False(await pipeline.RecordAsync(request));
        Assert.False(await new PlaybackSignalPipeline(jobs).RecordAsync(request));
        await using var db = await factory.CreateDbContextAsync(); var job = Assert.Single(await db.Jobs.Where(x => x.Type == PlaybackSignalPipeline.JobType).ToListAsync());
        Assert.Equal(tenant, job.TenantId); Assert.Equal(user, job.OwnerUserId); Assert.Equal("music", job.LibraryScopeId);
    }

    [Fact]
    public async Task MissingProtocolLibraryScope_ResolvesTheUniqueIndexedTrackScope()
    {
        var execution = Signal(PlaybackTransition.Start, "track-1", 0).ExecutionContext;
        var unscoped = new ProtocolExecutionContext(
            execution.Protocol, execution.BackendInstanceId, execution.VerifiedBackendPrincipalId,
            execution.Principal, execution.CorrelationId, execution.Deadline, default);
        var pipeline = new PlaybackSignalPipeline(jobs, new ProtocolLibraryScopeResolver(factory));

        Assert.True(await pipeline.RecordAsync(new(
            unscoped, PlaybackTransition.Start, "track-1", "device", "session", 0, clock.UtcNow)));

        await using var db = await factory.CreateDbContextAsync();
        Assert.Equal("music", Assert.Single(await db.Jobs.ToListAsync()).LibraryScopeId);
    }

    [Fact]
    public async Task MissingProtocolLibraryScope_RejectsAnAmbiguousItem()
    {
        await using (var db = await factory.CreateDbContextAsync())
        {
            var original = await db.LibraryTracks.AsNoTracking().SingleAsync();
            db.LibraryTracks.Add(new()
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenant,
                OwnerUserId = user,
                BackendIdentityId = original.BackendIdentityId,
                LibraryScopeId = "other",
                Protocol = "jellyfin",
                BackendInstanceId = "backend",
                BackendItemId = "track-1",
                FilePath = "/media/other.flac",
                Title = "Track",
                Artist = "Artist",
                DurationMilliseconds = 120000,
                ProviderIdsJson = "{}",
                IndexedAt = clock.UtcNow,
                SourceModifiedAt = clock.UtcNow,
                UpdatedAt = clock.UtcNow
            });
            await db.SaveChangesAsync();
        }
        var execution = Signal(PlaybackTransition.Start, "track-1", 0).ExecutionContext;
        var unscoped = new ProtocolExecutionContext(
            execution.Protocol, execution.BackendInstanceId, execution.VerifiedBackendPrincipalId,
            execution.Principal, execution.CorrelationId, execution.Deadline, default);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ProtocolLibraryScopeResolver(factory).ResolveAsync(unscoped, "track-1"));
    }

    [Fact]
    public async Task EmptyDurableIndex_UsesConfiguredJellyfinLibraryScope()
    {
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.LibraryTracks.RemoveRange(db.LibraryTracks);
            await db.SaveChangesAsync();
        }
        var execution = Signal(PlaybackTransition.Start, "backend-track", 0).ExecutionContext;
        var unscoped = new ProtocolExecutionContext(
            execution.Protocol, execution.BackendInstanceId, execution.VerifiedBackendPrincipalId,
            execution.Principal, execution.CorrelationId, execution.Deadline, default);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Jellyfin:LibraryId"] = "configured-music" }).Build();

        var resolved = await new ProtocolLibraryScopeResolver(factory, configuration)
            .ResolveAsync(unscoped, "backend-track");

        Assert.Equal("configured-music", resolved.LibraryScopeId);
    }

    [Fact]
    public async Task EmptyDurableIndex_ResolvesPlaybackTrackFromBackendMetadata()
    {
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.LibraryTracks.RemoveRange(db.LibraryTracks);
            await db.SaveChangesAsync();
        }
        var metadata = new BackendMetadataResolver(new(
            "Backend title", "Backend artist", "Backend album", "/art", 180));
        var resolver = new PlaybackTrackResolver(factory, [metadata]);

        var resolved = await resolver.ResolveAsync(Payload() with { ItemId = "backend-track" });

        Assert.NotNull(resolved);
        Assert.Equal("Backend title", resolved.Title);
        Assert.Equal(180_000, resolved.DurationMilliseconds);
    }

    [Fact]
    public async Task MissingPlaySession_DedupesRetriesButAllowsALaterOccurrence()
    {
        var pipeline = new PlaybackSignalPipeline(jobs);
        var first = Signal(PlaybackTransition.Start, "track-1", 0) with
        {
            PlaySessionId = null,
            ObservedAt = clock.UtcNow
        };
        var later = first with { ObservedAt = clock.UtcNow.AddSeconds(31) };

        Assert.True(await pipeline.RecordAsync(first));
        Assert.False(await pipeline.RecordAsync(first));
        Assert.True(await pipeline.RecordAsync(later));
    }

    [Fact]
    public async Task ProgressUsesStableTenSecondBucketsAndOrdersDistinctTransitions()
    {
        var pipeline = new PlaybackSignalPipeline(jobs);
        Assert.True(await pipeline.RecordAsync(Signal(PlaybackTransition.Progress, "track-1", TimeSpan.FromSeconds(11).Ticks)));
        Assert.False(await pipeline.RecordAsync(Signal(PlaybackTransition.Progress, "track-1", TimeSpan.FromSeconds(19).Ticks)));
        Assert.True(await pipeline.RecordAsync(Signal(PlaybackTransition.Progress, "track-1", TimeSpan.FromSeconds(21).Ticks)));
        Assert.True(await pipeline.RecordAsync(Signal(PlaybackTransition.Stop, "track-1", TimeSpan.FromSeconds(21).Ticks)));
    }

    [Fact]
    public async Task HandlerRejectsCrossTenantClaimBeforeAnySideEffect()
    {
        var writer = new Writer(); var scrobbles = new Scrobbles(); var lyrics = new Lyrics();
        var handler = new PlaybackSignalJobHandler(writer, scrobbles, lyrics);
        var payload = Signal(PlaybackTransition.Start, "track-1", 0);
        await new PlaybackSignalPipeline(jobs).RecordAsync(payload);
        var claim = await jobs.ClaimNextAsync("worker", [PlaybackSignalPipeline.JobType]); Assert.NotNull(claim);
        var badPayload = new PlaybackSignalPayload(new(Guid.CreateVersion7(), user, "jellyfin", "backend", "music"), PlaybackTransition.Start, "track-1", "device", "session", 0, clock.UtcNow, new string('a', 64));
        var badClaim = claim! with { Payload = System.Text.Json.JsonSerializer.SerializeToElement(badPayload) };
        var result = await handler.ExecuteAsync(new(badClaim, EmptyServices.Instance), default);
        Assert.Equal(DurableJobCompletionKind.Failed, result.Kind); Assert.Equal(0, writer.Calls + scrobbles.Calls + lyrics.Calls);
    }

    [Fact]
    public async Task RetryAfterSignalWriteDoesNotDuplicateHabitWeightOrSuccessfulScrobble()
    {
        await new PlaybackSignalPipeline(jobs).RecordAsync(Signal(PlaybackTransition.Start, "track-1", 0));
        var claim = await jobs.ClaimNextAsync("worker", [PlaybackSignalPipeline.JobType]); Assert.NotNull(claim);
        var writer = new Writer(); var scrobbles = new Scrobbles { FailFirst = true }; var lyrics = new Lyrics();
        var handler = new PlaybackSignalJobHandler(writer, scrobbles, lyrics);
        Assert.Equal(DurableJobCompletionKind.Retry, (await handler.ExecuteAsync(new(claim!, EmptyServices.Instance), default)).Kind);
        Assert.Equal(DurableJobCompletionKind.Succeeded, (await handler.ExecuteAsync(new(claim!, EmptyServices.Instance), default)).Kind);
        Assert.Equal(1, writer.Calls);
        Assert.Equal(1, scrobbles.Successes);
    }

    [Theory]
    [InlineData(true, false, 1)]
    [InlineData(false, true, 1)]
    [InlineData(false, false, 0)]
    public async Task ScopedDeliverySkipsMissingOptionalAccounts(bool lastFm, bool listenBrainz, int expected)
    {
        var targets = new[] { new Target("lastfm", lastFm), new Target("listenbrainz", listenBrainz) };
        var delivery = new ScopedPlaybackScrobbleDelivery(factory, targets, new Checkpoints());
        await delivery.DeliverAsync(Payload(), default);
        Assert.Equal(expected, targets.Sum(x => x.Successes));
    }

    [Fact]
    public async Task ScopedDeliveryCheckpointResumesAfterLaterTargetFailureWithoutRepeatingFirst()
    {
        var first = new Target("lastfm", true); var second = new Target("listenbrainz", true) { FailFirst = true };
        var checkpoints = new Checkpoints(); var delivery = new ScopedPlaybackScrobbleDelivery(factory, [first, second], checkpoints);
        await Assert.ThrowsAsync<IOException>(() => delivery.DeliverAsync(Payload(), default));
        await delivery.DeliverAsync(Payload(), default);
        Assert.Equal(1, first.Successes); Assert.Equal(1, second.Successes);
    }

    [Fact]
    public async Task ScopedDelivery_FirstUnauthorizedProviderDoesNotBlockHealthyProvider()
    {
        var rejected = new Target("lastfm", true) { Reject = true };
        var healthy = new Target("listenbrainz", true);
        var checkpoints = new Checkpoints();
        var delivery = new ScopedPlaybackScrobbleDelivery(factory, [rejected, healthy], checkpoints);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => delivery.DeliverAsync(Payload(), default));
        Assert.Equal(0, rejected.Successes);
        Assert.Equal(1, healthy.Successes);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => delivery.DeliverAsync(Payload(), default));
        Assert.Equal(1, healthy.Successes);
    }

    [Theory]
    [InlineData(59, 0)]
    [InlineData(60, 1)]
    public async Task CompletedScrobbleHonorsHalfTrackThreshold(int playedSeconds, int expected)
    {
        var target = new Target("lastfm", true); var delivery = new ScopedPlaybackScrobbleDelivery(factory, [target], new Checkpoints());
        await delivery.DeliverAsync(Payload() with { PositionTicks = TimeSpan.FromSeconds(playedSeconds).Ticks }, default);
        Assert.Equal(expected, target.Successes);
    }

    [Fact]
    public async Task EligibleProgressScrobblesOnceAndStopDoesNotDuplicateIt()
    {
        var target = new Target("lastfm", true);
        var activity = new allstarr.Services.Common.PlaybackDeliveryActivityStore();
        var delivery = new ScopedPlaybackScrobbleDelivery(
            factory,
            [target],
            new Checkpoints(),
            activity: activity);
        var progress = Payload() with
        {
            Transition = PlaybackTransition.Progress,
            PositionTicks = TimeSpan.FromSeconds(60).Ticks
        };

        await delivery.DeliverAsync(progress, default);
        await delivery.DeliverAsync(progress with { Transition = PlaybackTransition.Stop }, default);

        Assert.Equal(1, target.Successes);
        Assert.True(activity.WasDelivered(progress.ItemId, progress.DeviceId!));
    }

    [Fact]
    public async Task ExplicitSubsonicSubmission_BypassesPlaybackThreshold()
    {
        var target = new Target("lastfm", true);
        var delivery = new ScopedPlaybackScrobbleDelivery(factory, [target], new Checkpoints());

        await delivery.DeliverAsync(Payload() with
        {
            Transition = PlaybackTransition.Submission,
            PositionTicks = null
        }, default);

        Assert.Equal(1, target.Successes);
    }

    [Fact]
    public async Task Submission_WritesCompletedHabitSignal()
    {
        await new PlaybackSignalPipeline(jobs).RecordAsync(
            Signal(PlaybackTransition.Submission, "track-1", 0));
        var claim = await jobs.ClaimNextAsync("worker", [PlaybackSignalPipeline.JobType]);
        var writer = new Writer();
        var handler = new PlaybackSignalJobHandler(writer, new Scrobbles(), new Lyrics());

        var result = await handler.ExecuteAsync(new(claim!, EmptyServices.Instance), default);

        Assert.Equal(DurableJobCompletionKind.Succeeded, result.Kind);
        Assert.Equal(1, writer.Calls);
    }

    [Theory]
    [InlineData(10, "skip")]
    [InlineData(60, "complete")]
    public async Task StopSignal_ClassifiesShortAndCompletedPlayback(int playedSeconds, string expectedType)
    {
        await new PlaybackSignalPipeline(jobs).RecordAsync(
            Signal(PlaybackTransition.Stop, "track-1", TimeSpan.FromSeconds(playedSeconds).Ticks));
        var claim = await jobs.ClaimNextAsync("worker", [PlaybackSignalPipeline.JobType]);
        var writer = new Writer();
        var handler = new PlaybackSignalJobHandler(
            writer,
            new Scrobbles(),
            new Lyrics(),
            new PlaybackTrackResolver(factory));

        var result = await handler.ExecuteAsync(new(claim!, EmptyServices.Instance), default);

        Assert.Equal(DurableJobCompletionKind.Succeeded, result.Kind);
        Assert.Equal(expectedType, writer.LastType);
    }

    [Fact]
    public async Task ProviderTrackId_ResolvesForScopedScrobbleDelivery()
    {
        await using (var db = await factory.CreateDbContextAsync())
        {
            var track = await db.LibraryTracks.SingleAsync();
            track.ProviderIdsJson = "{\"deezer\":\"provider-track\"}";
            await db.SaveChangesAsync();
        }
        var target = new Target("lastfm", true);
        var delivery = new ScopedPlaybackScrobbleDelivery(factory, [target], new Checkpoints());

        await delivery.DeliverAsync(Payload() with
        {
            ItemId = "deezer:provider-track",
            Transition = PlaybackTransition.Submission,
            PositionTicks = null
        }, default);

        Assert.Equal(1, target.Successes);
    }

    [Theory]
    [InlineData(29_000, 29, false)]
    [InlineData(600_000, 239, false)]
    [InlineData(600_000, 240, true)]
    public void CompletedScrobbleHonorsMinimumDurationAndFourMinuteCap(long durationMs, int playedSeconds, bool expected) =>
        Assert.Equal(expected, ScopedPlaybackScrobbleDelivery.EligibleForCompletedScrobble(durationMs, TimeSpan.FromSeconds(playedSeconds).Ticks));

    private PlaybackSignalRequest Signal(PlaybackTransition transition, string item, long ticks) => new(
        new(ProtocolKind.Jellyfin, "backend", "principal", new AllstarrPrincipal(tenant, user, "jellyfin", "backend", "principal", "User", false),
        "correlation", clock.UtcNow.AddMinutes(1), default, libraryScopeId: "music"), transition, item, "device", "session", ticks, clock.UtcNow);
    private PlaybackSignalPayload Payload() => new(new(tenant, user, "jellyfin", "backend", "music"), PlaybackTransition.Stop,
        "track-1", "device", "session", TimeSpan.FromSeconds(100).Ticks, clock.UtcNow, new string('a', 64));
    public Task DisposeAsync() { try { Directory.Delete(root, true); } catch { } return Task.CompletedTask; }
    private sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext> { public AllstarrDbContext CreateDbContext() => new(options); public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken token = default) => Task.FromResult(new AllstarrDbContext(options)); }
    private sealed class Clock : IPlatformClock { public DateTimeOffset UtcNow { get; set; } }
    private sealed class Writer : IIdempotentRecommendationSignalWriter { private readonly HashSet<string> keys = []; public int Calls; public string? LastType; public Task<bool> WriteAsync(IntelligenceScope s, string t, string k, double v, DateTimeOffset o, CancellationToken c = default) { Calls++; LastType = t; return Task.FromResult(true); } public Task<bool> WriteIdempotentAsync(IntelligenceScope s, string t, string k, double v, DateTimeOffset o, string key, Guid job, CancellationToken c = default) { if (keys.Add(key)) { Calls++; LastType = t; } return Task.FromResult(true); } }
    private sealed class Scrobbles : IScopedPlaybackScrobbleDelivery { public int Calls; public int Successes; public bool FailFirst; public Task DeliverAsync(PlaybackSignalPayload p, CancellationToken c) { Calls++; if (FailFirst) { FailFirst = false; throw new IOException(); } Successes++; return Task.CompletedTask; } }
    private sealed class Lyrics : IPlaybackLyricsPrefetch { public int Calls; public Task PrefetchAsync(PlaybackSignalPayload p, CancellationToken c) { Calls++; return Task.CompletedTask; } }
    private sealed class Target(string id, bool configured) : IExactScopePlaybackScrobbleTarget { public string ProviderId => id; public int Successes; public bool FailFirst; public bool Reject; public Task<bool> IsConfiguredAsync(IntelligenceScope s, CancellationToken c) => Task.FromResult(configured); public Task DeliverAsync(IntelligenceScope s, PlaybackTransition t, ScopedPlaybackTrack track, long? p, DateTimeOffset o, string key, CancellationToken c) { if (Reject) throw new UnauthorizedAccessException(); if (FailFirst) { FailFirst = false; throw new IOException(); } Successes++; return Task.CompletedTask; } }
    private sealed class Checkpoints : IPlaybackDeliveryCheckpointStore { private readonly HashSet<string> values = []; public Task<bool> IsCompletedAsync(Guid t, Guid u, string k, string target, CancellationToken c) => Task.FromResult(values.Contains(k + target)); public Task MarkCompletedAsync(Guid t, Guid u, string k, string target, CancellationToken c) { values.Add(k + target); return Task.CompletedTask; } }
    private sealed class EmptyServices : IServiceProvider { public static readonly EmptyServices Instance = new(); public object? GetService(Type t) => null; }
    private sealed class BackendMetadataResolver(allstarr.Services.Common.PlaybackTrackMetadata metadata)
        : allstarr.Services.Common.IPlaybackMetadataResolver
    {
        public Task<allstarr.Services.Common.PlaybackTrackMetadata?> ResolveAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult<allstarr.Services.Common.PlaybackTrackMetadata?>(metadata);

        public Task<allstarr.Services.Common.PlaybackArtwork?> ResolveArtworkAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult<allstarr.Services.Common.PlaybackArtwork?>(null);
    }
}

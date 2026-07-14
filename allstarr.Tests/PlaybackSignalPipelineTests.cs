using allstarr.Core.Identity;
using allstarr.Core.Intelligence;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Playback;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

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
        db.LibraryTracks.Add(new() { Id = Guid.CreateVersion7(), TenantId = tenant, OwnerUserId = user, BackendIdentityId = identity,
            LibraryScopeId = "music", Protocol = "jellyfin", BackendInstanceId = "backend", BackendItemId = "track-1",
            FilePath = "/media/track.flac", Title = "Track", Artist = "Artist", DurationMilliseconds = 120000,
            ProviderIdsJson = "{}", IndexedAt = now, SourceModifiedAt = now, UpdatedAt = now }); await db.SaveChangesAsync();
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

    [Theory]
    [InlineData(59, 0)]
    [InlineData(60, 1)]
    public async Task CompletedScrobbleHonorsHalfTrackThreshold(int playedSeconds, int expected)
    {
        var target = new Target("lastfm", true); var delivery = new ScopedPlaybackScrobbleDelivery(factory, [target], new Checkpoints());
        await delivery.DeliverAsync(Payload() with { PositionTicks = TimeSpan.FromSeconds(playedSeconds).Ticks }, default);
        Assert.Equal(expected, target.Successes);
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
    private sealed class Writer : IIdempotentRecommendationSignalWriter { private readonly HashSet<string> keys = []; public int Calls; public Task<bool> WriteAsync(IntelligenceScope s, string t, string k, double v, DateTimeOffset o, CancellationToken c = default) { Calls++; return Task.FromResult(true); } public Task<bool> WriteIdempotentAsync(IntelligenceScope s, string t, string k, double v, DateTimeOffset o, string key, Guid job, CancellationToken c = default) { if (keys.Add(key)) Calls++; return Task.FromResult(true); } }
    private sealed class Scrobbles : IScopedPlaybackScrobbleDelivery { public int Calls; public int Successes; public bool FailFirst; public Task DeliverAsync(PlaybackSignalPayload p, CancellationToken c) { Calls++; if (FailFirst) { FailFirst = false; throw new IOException(); } Successes++; return Task.CompletedTask; } }
    private sealed class Lyrics : IPlaybackLyricsPrefetch { public int Calls; public Task PrefetchAsync(PlaybackSignalPayload p, CancellationToken c) { Calls++; return Task.CompletedTask; } }
    private sealed class Target(string id, bool configured) : IExactScopePlaybackScrobbleTarget { public string ProviderId => id; public int Successes; public bool FailFirst; public Task<bool> IsConfiguredAsync(IntelligenceScope s, CancellationToken c) => Task.FromResult(configured); public Task DeliverAsync(IntelligenceScope s, PlaybackTransition t, ScopedPlaybackTrack track, long? p, DateTimeOffset o, string key, CancellationToken c) { if (FailFirst) { FailFirst = false; throw new IOException(); } Successes++; return Task.CompletedTask; } }
    private sealed class Checkpoints : IPlaybackDeliveryCheckpointStore { private readonly HashSet<string> values = []; public Task<bool> IsCompletedAsync(Guid t, Guid u, string k, string target, CancellationToken c) => Task.FromResult(values.Contains(k + target)); public Task MarkCompletedAsync(Guid t, Guid u, string k, string target, CancellationToken c) { values.Add(k + target); return Task.CompletedTask; } }
    private sealed class EmptyServices : IServiceProvider { public static readonly EmptyServices Instance = new(); public object? GetService(Type t) => null; }
}

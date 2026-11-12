using allstarr.Core.Providers;
using allstarr.Core.Jobs;
using allstarr.Core.Music;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using allstarr.Core.Settings;
using allstarr.Core.Protocols;
using allstarr.Core.Health;
using allstarr.Core.Capabilities;
using allstarr.Core.Configuration;
using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed class LyricsRefreshScheduleTests : IAsyncLifetime
{
    private SqliteTestDatabase _database = null!;
    private Factory _factory = null!;
    private readonly TestClock _clock = new()
    {
        UtcNow = new(2026, 11, 6, 2, 0, 0, TimeSpan.Zero)
    };

    public async Task InitializeAsync()
    {
        _database = await SqliteTestDatabase.CreateAsync();
        _factory = new(_database.Options);
    }

    public async Task DisposeAsync() => await _database.DisposeAsync();

    [Fact]
    public async Task EnsureCreatesTheHouseholdDailyUtcSchedule()
    {
        var helper = Helper();

        await helper.EnsureAsync();

        await using var db = await _factory.CreateDbContextAsync();
        var schedule = await db.JobSchedules.SingleAsync(item => item.Id == LyricsRefreshSchedule.ScheduleId);
        Assert.Equal(LyricsRefreshService.Type, schedule.JobType);
        Assert.Null(schedule.OwnerUserId);
        Assert.True(schedule.Enabled);
        Assert.Equal("0 8 * * *", schedule.CronExpression);
        Assert.Equal("UTC", schedule.TimeZoneId);
        Assert.Equal(ScheduleOverlapPolicy.Skip, schedule.OverlapPolicy);
        Assert.Equal(ScheduleMisfirePolicy.RunOnce, schedule.MisfirePolicy);
        Assert.NotNull(schedule.NextRunAt);
        Assert.Equal(8, schedule.NextRunAt!.Value.Hour);
        Assert.Equal(TimeSpan.Zero, schedule.NextRunAt.Value.Offset);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task DailyTickEnqueuesTheRegisteredLyricsTask()
    {
        var engine = new DurableScheduleEngine(_factory, Queue(), _clock, ReleaseComposition.Development, lyrics: Helper());
        Assert.Equal(0, (await engine.TickAsync()).Enqueued);
        _clock.UtcNow = _clock.UtcNow.AddHours(8);
        Assert.Equal(1, (await engine.TickAsync()).Enqueued);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(LyricsRefreshService.Type, (await db.Jobs.SingleAsync()).Type);
    }

    [Fact]
    public async Task RunNowEnqueuesAProviderFreeHouseholdJobThatReauthorizes()
    {
        var helper = Helper();

        var queued = await helper.RunAsync();

        await using var db = await _factory.CreateDbContextAsync();
        var job = await db.Jobs.SingleAsync(item => item.Id == queued.JobId);
        Assert.Equal(LyricsRefreshService.Type, job.Type);
        Assert.Null(job.OwnerUserId);
        Assert.Null(job.ProviderAccountId);
        Assert.Null(job.ProviderCapability);

        var queue = Queue();
        var claim = await queue.ClaimNextAsync("lyrics-schedule-test", [LyricsRefreshService.Type]);
        Assert.NotNull(claim);
        Assert.Equal(queued.JobId, claim!.JobId);
        Assert.True((await queue.ReauthorizeAsync(claim)).Authorized);
    }

    [Fact]
    public async Task EnablingAnAutomaticSourceAfterRestartQueuesOnlyThatSourceForItsListener()
    {
        var owner = Guid.NewGuid();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.Users.Add(new() { Id = owner, BackendType = "jellyfin", BackendInstanceId = "fixture", BackendPrincipalId = "listener", Enabled = true });
            await db.SaveChangesAsync();
        }
        var policy = new EffectiveProviderPolicySnapshot(
            ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty.Add(ProviderCapabilityKind.Lyrics, ["lyricsplus"]),
            ImmutableHashSet<string>.Empty, "lossless", 0)
        { ProviderModes = ImmutableDictionary<string, ProviderUseMode>.Empty.Add("lyricsplus", ProviderUseMode.Off) };
        var policies = new Mock<IEffectiveProviderPolicyResolver>();
        policies.Setup(item => item.ResolveForUserAsync(owner, It.IsAny<CancellationToken>())).ReturnsAsync(() => policy);
        var providers = new Mock<IProtocolProviderGateway>();
        providers.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Lyrics)).Returns(["lyricsplus"]);
        var triggers = new LyricsRefreshTriggers(_factory, policies.Object, providers.Object, Queue(), new RuntimeSettingsChangeSignal(),
            new ProviderRuntimeHealth(), new DurableStorageState(_database.StorageOptions), new ManualTimeProvider(_clock.UtcNow),
            NullLogger<LyricsRefreshTriggers>.Instance);
        await triggers.CheckSettingsAsync(default);
        policy = policy with { ProviderModes = ImmutableDictionary<string, ProviderUseMode>.Empty };
        var restarted = new LyricsRefreshTriggers(_factory, policies.Object, providers.Object, Queue(), new RuntimeSettingsChangeSignal(),
            new ProviderRuntimeHealth(), new DurableStorageState(_database.StorageOptions), new ManualTimeProvider(_clock.UtcNow),
            NullLogger<LyricsRefreshTriggers>.Instance);
        await restarted.CheckSettingsAsync(default);
        await restarted.CheckSettingsAsync(default);
        await using var verify = await _factory.CreateDbContextAsync();
        var job = await verify.Jobs.SingleAsync(item => item.Type == LyricsRefreshService.Type);
        var payload = System.Text.Json.JsonSerializer.Deserialize<LyricsRefreshRequest>(job.PayloadJson)!;
        Assert.Equal("lyricsplus", payload.SourceId);
        Assert.Equal(owner, payload.OwnerUserId);
    }

    [Fact]
    public void LyricsRecoverySignalsOnlyTheRecoveredCapabilityAndAccount()
    {
        var health = new ProviderRuntimeHealth(clock: _clock);
        var account = Guid.NewGuid();
        var key = ProviderRuntimeStatusKey.CreateManaged("lyricsplus", ProviderCapabilities.Lyrics, account);
        health.Record(key, 1, true, 5, null);
        var recovered = new List<(string, Guid?)>();
        health.LyricsRecovered += (source, id) => recovered.Add((source, id));
        health.Record(key, 1, false, 5, "transient-failure", retryAfter: TimeSpan.FromMinutes(1));
        health.Record(key, 1, true, 5, null);
        health.Record(key, 1, true, 5, null);
        health.Record(ProviderRuntimeStatusKey.CreateAccountFree("lyricsplus", ProviderCapabilities.Streaming), 1, true, 5, null);
        Assert.Equal(("lyricsplus", (Guid?)account), Assert.Single(recovered));
    }

    [Fact]
    public async Task NewlyEnabledLyricsAccountQueuesItsOwnScope()
    {
        var owner = Guid.NewGuid();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.Users.Add(new() { Id = owner, BackendType = "jellyfin", BackendInstanceId = "fixture", BackendPrincipalId = "listener", Enabled = true });
            await db.SaveChangesAsync();
        }
        var policy = new EffectiveProviderPolicySnapshot(
            ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty.Add(ProviderCapabilityKind.Lyrics, ["lyricsplus"]),
            ImmutableHashSet<string>.Empty, "lossless", 0);
        var policies = new Mock<IEffectiveProviderPolicyResolver>();
        policies.Setup(item => item.ResolveForUserAsync(owner, It.IsAny<CancellationToken>())).ReturnsAsync(policy);
        var providers = new Mock<IProtocolProviderGateway>();
        providers.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Lyrics)).Returns(["lyricsplus"]);
        var triggers = new LyricsRefreshTriggers(_factory, policies.Object, providers.Object, Queue(), new RuntimeSettingsChangeSignal(),
            new ProviderRuntimeHealth(), new DurableStorageState(_database.StorageOptions), new ManualTimeProvider(_clock.UtcNow),
            NullLogger<LyricsRefreshTriggers>.Instance);
        await triggers.CheckSettingsAsync(default);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.ProviderAccounts.Add(new() { Id = Guid.NewGuid(), OwnerUserId = owner, ProviderId = "lyricsplus", Enabled = true, Revision = 1 });
            await db.SaveChangesAsync();
        }
        await triggers.CheckSettingsAsync(default);
        await using var verify = await _factory.CreateDbContextAsync();
        var payload = System.Text.Json.JsonSerializer.Deserialize<LyricsRefreshRequest>((await verify.Jobs.SingleAsync()).PayloadJson)!;
        Assert.Equal(("lyricsplus", (Guid?)owner), (payload.SourceId, payload.OwnerUserId));
    }

    [Fact]
    public async Task RecoveryBurstsAreCoalescedWithoutDroppingSources()
    {
        var triggers = new LyricsRefreshTriggers(_factory, Mock.Of<IEffectiveProviderPolicyResolver>(),
            Mock.Of<IProtocolProviderGateway>(), Queue(), new RuntimeSettingsChangeSignal(), new ProviderRuntimeHealth(),
            new DurableStorageState(_database.StorageOptions), new ManualTimeProvider(_clock.UtcNow),
            NullLogger<LyricsRefreshTriggers>.Instance);
        for (var index = 0; index < 65; index++)
        {
            triggers.SourceRecovered("lyrics" + index, null);
            triggers.SourceRecovered("lyrics" + index, null);
        }
        await triggers.DrainRecoveriesAsync(default);
        await triggers.DrainRecoveriesAsync(default);
        await triggers.DrainRecoveriesAsync(default);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(130, await db.Jobs.CountAsync());
        Assert.Equal(65, await db.Jobs.CountAsync(job => job.IdempotencyKey.EndsWith(":ready")));
    }

    private LyricsRefreshSchedule Helper() => new(_factory, Queue(), _clock);

    private DurableJobQueue Queue()
    {
        var options = new DurableJobOptions();
        return new(_factory, options, new JobPayloadPolicy(options), _clock,
            new DurableJobContextAuthorizer(_factory));
    }

    private sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AllstarrDbContext(options));
    }

    private sealed class TestClock : IPlatformClock
    {
        public DateTimeOffset UtcNow { get; set; }
    }
}

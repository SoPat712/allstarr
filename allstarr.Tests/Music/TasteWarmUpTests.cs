using System.Collections.Immutable;
using System.Data.Common;
using allstarr.Core.AudioMuse;
using allstarr.Core.Capabilities;
using allstarr.Core.ClientApi;
using allstarr.Core.Enrichment;
using allstarr.Core.Music;
using allstarr.Core.Operations;
using allstarr.Core.Playback;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace allstarr.Tests;

public sealed class TasteWarmUpTests
{
    [Fact]
    public async Task SoundProfileRequestDoesNotJoinBlockedWarmUp()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var gate = new FirstReadGate();
        var factory = new Factory(new DbContextOptionsBuilder<AllstarrDbContext>(database.Options).AddInterceptors(gate).Options);
        var clock = new Clock(new ManualTimeProvider());
        var analysis = new AudioMuseDiscovery(factory, null!, null!, null!, null!, null!, null!, null!, null!, null!);
        var sound = new SoundProfileService(factory, new Mock<IEffectiveProviderPolicyResolver>().Object, clock, analysis);
        var user = Guid.NewGuid();
        var warming = sound.WarmAsync(user, default);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(await sound.GetAsync(user, default).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(warming.IsCompleted);
        }
        finally
        {
            gate.Release.TrySetResult();
            await warming;
        }
    }

    [Fact]
    public async Task StartupWaitsForRestorationThenWarmsOnlyRecentListenersOneAtATime()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-11-09T12:00:00Z"));
        var clock = new Clock(time);
        var factory = new Factory(database.Options);
        var recent = Guid.NewGuid();
        var boundary = Guid.NewGuid();
        var cutoff = clock.UtcNow.AddSeconds(1).AddDays(-14);
        await using (var db = factory.CreateDbContext())
        {
            foreach (var (id, at, enabled, synthetic) in new[]
            {
                (recent, clock.UtcNow, true, false), (boundary, cutoff, true, false),
                (Guid.NewGuid(), cutoff.AddTicks(-1), true, false),
                (Guid.NewGuid(), clock.UtcNow, false, false), (Guid.NewGuid(), clock.UtcNow, true, true)
            })
                await SqliteTestDatabase.InsertHistoricalUserAsync(db, new()
                {
                    Id = id,
                    BackendType = "jellyfin",
                    BackendInstanceId = "main",
                    BackendPrincipalId = id.ToString(),
                    DisplayName = "Listener",
                    LastSeenAt = at,
                    CreatedAt = at,
                    UpdatedAt = at,
                    Enabled = enabled,
                    IsSynthetic = synthetic
                });
        }
        var tags = new SharedTagStore(factory, clock);
        var taste = new TasteService(factory, tags, clock);
        using var stats = new ListeningStatsService(factory, clock);
        var policies = new Mock<IEffectiveProviderPolicyResolver>();
        policies.Setup(item => item.ResolveForUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(
            new EffectiveProviderPolicySnapshot(ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
                ImmutableHashSet<string>.Empty, "lossless", .07));
        var sound = new SoundProfileService(factory, policies.Object, clock);
        var gateway = new Mock<IProtocolProviderGateway>().Object;
        var access = new Mock<IBackendLibraryAccessResolver>().Object;
        using var radio = new MusicMixService(gateway);
        var mixes = new PersonalMixService(factory, clock, tags, taste, radio, gateway, null!,
            new Mock<IDurableRuntimeSettings>().Object, policies.Object, access);
        using var index = new LyricsSearchIndex(database.StorageOptions);
        var lyrics = new LyricsSearchReader(index, factory, access, policies.Object, gateway,
            new Mock<IProviderRouter>().Object, null!, null!, Microsoft.Extensions.Logging.Abstractions.NullLogger<LyricsSearchReader>.Instance,
            new LyricsSearchChanges(), new RuntimeSettingsChangeSignal());
        using var services = new ServiceCollection().AddScoped(_ => lyrics).BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var storage = new DurableStorageState(database.StorageOptions);
        storage.Set(DurableStorageReadiness.Ready);
        using var lifetime = new Lifetime();
        var restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logger = new Logger();
        using var worker = new TasteWarmUp(taste, factory, storage, logger, sound, stats, mixes, services.GetRequiredService<IServiceScopeFactory>(),
            lifetime, null!, clock, time);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var restoringRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var warming = worker.WarmAfterStartupAsync(() => { restoringRead.TrySetResult(); return restored.Task; }, stop.Token);
        Assert.False(restoringRead.Task.IsCompleted);
        Assert.False(warming.IsCompleted);
        lifetime.Started.Cancel();
        await restoringRead.Task.WaitAsync(stop.Token);
        Assert.False(warming.IsCompleted);
        await stats.GetAsync(Execution(recent, clock), "30d", null, stop.Token).WaitAsync(stop.Token);
        Assert.False(warming.IsCompleted);
        await using (var db = factory.CreateDbContext()) Assert.Empty(await db.TasteProfiles.ToArrayAsync());
        restored.SetResult();
        Assert.Equal(TimeSpan.FromSeconds(1), await time.NextTimerAsync());
        await using (var db = factory.CreateDbContext()) Assert.Empty(await db.TasteProfiles.ToArrayAsync());
        time.Advance(TimeSpan.FromSeconds(1));
        foreach (var owner in new[] { recent, boundary })
        {
            for (var read = 0; read < 8; read++)
            {
                Assert.Equal(TimeSpan.FromMilliseconds(100), await time.NextTimerAsync());
                if (read == 5)
                {
                    await using var db = factory.CreateDbContext();
                    Assert.Contains(await db.TasteProfiles.Select(item => item.OwnerUserId).ToArrayAsync(), item => item == owner);
                    if (owner == recent) Assert.Single(await db.TasteProfiles.ToArrayAsync());
                }
                time.Advance(TimeSpan.FromMilliseconds(100));
            }
            Assert.Equal(TimeSpan.FromMilliseconds(250), await time.NextTimerAsync());
            time.Advance(TimeSpan.FromMilliseconds(250));
        }
        await warming.WaitAsync(stop.Token);
        Assert.Empty(logger.Messages);
        await using (var db = factory.CreateDbContext())
            Assert.Equal(new[] { recent, boundary }.Order(), (await db.TasteProfiles.Select(item => item.OwnerUserId).ToArrayAsync()).Order());
        using var timing = RequestTiming.Begin();
        await stats.GetAsync(Execution(recent, clock), "7d", null, stop.Token);
        Assert.Equal("hit", timing.Snapshot("stats").Cache);
    }

    private static ProtocolExecutionContext Execution(Guid user, IPlatformClock clock) => new(ProtocolKind.Jellyfin, "main", user.ToString(),
        new(user, "jellyfin", "main", user.ToString(), "Listener", false), "warm-test", clock.UtcNow.AddMinutes(5), default);

    private sealed class Clock(TimeProvider time) : IPlatformClock { public DateTimeOffset UtcNow => time.GetUtcNow(); }
    private sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
    private sealed class Lifetime : IHostApplicationLifetime, IDisposable
    {
        public CancellationTokenSource Started { get; } = new();
        public CancellationToken ApplicationStarted => Started.Token;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
        public void Dispose() => Started.Dispose();
    }
    private sealed class Logger : ILogger<TasteWarmUp>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
    private sealed class FirstReadGate : DbCommandInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int reads;
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref reads) == 1)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }
}

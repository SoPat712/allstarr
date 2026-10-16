using allstarr.Core.Operations;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed class ProviderCheckServiceTests
{
    [Fact]
    public async Task Schedule_UsesInitialDelayAndIntervalWithoutOverlappingAndStopsOnCancellation()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new Factory(database.Options);
        var time = new ManualTimeProvider();
        var runner = new Runner();
        var activity = new BackgroundTaskActivity(factory, time, NullLogger<BackgroundTaskActivity>.Instance);
        var service = new ProviderCheckService(runner, activity, new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ProviderChecks:IntervalHours"] = "2", ["ProviderChecks:InitialDelaySeconds"] = "120" }).Build(),
            time, NullLogger<ProviderCheckService>.Instance);
        await service.StartAsync(default);
        Assert.Equal(TimeSpan.FromMinutes(2), await time.NextTimerAsync());
        Assert.Equal(time.GetUtcNow().AddMinutes(2), Assert.Single(await activity.ReadAsync(default)).NextRunAt);
        time.Advance(TimeSpan.FromSeconds(119));
        Assert.Equal(0, runner.Runs);
        time.Advance(TimeSpan.FromSeconds(1));
        await runner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        time.Advance(TimeSpan.FromHours(12));
        Assert.Equal(1, runner.Runs);
        runner.Release.TrySetResult();
        Assert.Equal(TimeSpan.FromHours(2), await time.NextTimerAsync());
        time.Advance(TimeSpan.FromHours(2));
        Assert.Equal(TimeSpan.FromHours(2), await time.NextTimerAsync());
        Assert.Equal(2, runner.Runs);
        await service.StopAsync(default);
        time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(2, runner.Runs);
        Assert.Equal(1, runner.MaximumConcurrent);
        service.Dispose();
    }

    [Fact]
    public async Task Schedule_CancelsAnActiveSweep()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new Factory(database.Options);
        var time = new ManualTimeProvider();
        var runner = new Runner();
        using var service = new ProviderCheckService(runner,
            new BackgroundTaskActivity(factory, time, NullLogger<BackgroundTaskActivity>.Instance),
            new ConfigurationBuilder().Build(), time, NullLogger<ProviderCheckService>.Instance);
        await service.StartAsync(default);
        await time.NextTimerAsync();
        time.Advance(TimeSpan.FromMinutes(2));
        await runner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await service.StopAsync(default);
        Assert.True(runner.Canceled);
    }

    [Fact]
    public async Task Schedule_ClampsAnOverlyFrequentInterval()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new Factory(database.Options);
        var time = new ManualTimeProvider();
        var runner = new Runner();
        runner.Release.TrySetResult();
        using var service = new ProviderCheckService(runner,
            new BackgroundTaskActivity(factory, time, NullLogger<BackgroundTaskActivity>.Instance),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["ProviderChecks:IntervalHours"] = "0" }).Build(), time, NullLogger<ProviderCheckService>.Instance);
        await service.StartAsync(default);
        Assert.Equal(TimeSpan.FromMinutes(2), await time.NextTimerAsync());
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(TimeSpan.FromHours(1), await time.NextTimerAsync());
        await service.StopAsync(default);
    }

    [Fact]
    public async Task CompletedSweep_WithUnhealthyChecksDoesNotCreateRepeatedTaskFailureNotices()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new Factory(database.Options);
        var time = new ManualTimeProvider();
        var runner = new Runner { Result = false };
        runner.Release.TrySetResult();
        var activity = new BackgroundTaskActivity(factory, time, NullLogger<BackgroundTaskActivity>.Instance);
        using var service = new ProviderCheckService(runner, activity, new ConfigurationBuilder().Build(),
            time, NullLogger<ProviderCheckService>.Instance);
        await service.StartAsync(default);
        await time.NextTimerAsync();
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(TimeSpan.FromHours(6), await time.NextTimerAsync());
        time.Advance(TimeSpan.FromHours(6));
        Assert.Equal(TimeSpan.FromHours(6), await time.NextTimerAsync());
        await service.StopAsync(default);
        await using var db = factory.CreateDbContext();
        var completed = await db.AuditEvents.Where(row => row.Category == "background-task").ToListAsync();
        Assert.Equal(2, completed.Count);
        Assert.All(completed, row => Assert.Equal("success", row.Outcome));
        Assert.Equal("Scheduled", Assert.Single(await activity.ReadAsync(default)).State);
    }

    private sealed class Runner : IProviderCheckRunner
    {
        public int Runs;
        public int MaximumConcurrent;
        private int concurrent;
        public bool Canceled;
        public bool Result = true;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task RestoreAsync(CancellationToken token) => Task.CompletedTask;
        public async Task<bool> SweepAsync(CancellationToken token)
        {
            Runs++;
            MaximumConcurrent = Math.Max(MaximumConcurrent, Interlocked.Increment(ref concurrent));
            Entered.TrySetResult();
            try { await Release.Task.WaitAsync(token); return Result; }
            catch (OperationCanceledException) { Canceled = true; throw; }
            finally { Interlocked.Decrement(ref concurrent); }
        }
    }
    internal sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}

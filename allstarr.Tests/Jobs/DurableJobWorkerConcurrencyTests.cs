using allstarr.Core.Identity;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed partial class DurableJobWorkerConcurrencyTests : IAsyncLifetime
{
    private readonly Guid _userId = Guid.CreateVersion7();
    private SqliteTestDatabase _database = null!;
    private TestDbContextFactory _factory = null!;
    private FixtureClock _clock = null!;
    private DurableJobOptions _options = null!;
    private DurableJobQueue _queue = null!;
    private DurableStorageState _storageState = null!;
    private readonly List<DurableJobWorker> _workers = [];
    private readonly List<Action> _cleanupActions = [];

    public async Task InitializeAsync()
    {
        _database = await SqliteTestDatabase.CreateAsync();
        _factory = new TestDbContextFactory(_database.Options);
        var now = DateTimeOffset.UtcNow;
        await using (var context = await _factory.CreateDbContextAsync())
        {
            context.Users.Add(new UserRecord
            {
                Id = _userId,
                BackendType = "jellyfin",
                BackendInstanceId = "worker-fixture",
                BackendPrincipalId = "worker-user",
                DisplayName = "Worker fixture",
                Enabled = true,
                CreatedAt = now,
                UpdatedAt = now,
                LastSeenAt = now
            });
            await context.SaveChangesAsync();
        }

        _options = new DurableJobOptions
        {
            PollIntervalMilliseconds = 50,
            MaxPayloadBytes = 64 * 1024
        };
        _clock = new FixtureClock(now);
        _queue = new DurableJobQueue(
            _factory,
            _options,
            new JobPayloadPolicy(_options),
            _clock);
        _storageState = new DurableStorageState(_database.StorageOptions);
        _storageState.Set(DurableStorageReadiness.Ready, "worker-fixture");
    }

    [Fact]
    public async Task StartupRecoversRunningWorkBeforeDispatchingWithoutClockAdvance()
    {
        var queued = await EnqueueAsync("recovery", "recovery-before-dispatch");
        var originalClaim = await _queue.ClaimNextAsync("fixture-before-restart", ["recovery"]);
        Assert.NotNull(originalClaim);

        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var firstServices = new ServiceCollection().BuildServiceProvider();
        var firstWorker = CreateWorker(
            [new BlockingHandler("recovery", firstStarted)],
            firstServices);

        await firstWorker.StartAsync(CancellationToken.None);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await firstWorker.StopAsync(CancellationToken.None);

        var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var secondServices = new ServiceCollection().BuildServiceProvider();
        var secondWorker = CreateWorker(
            [new CompletingHandler("recovery", resumed)],
            secondServices);

        await secondWorker.StartAsync(CancellationToken.None);
        await resumed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForStateAsync(queued.JobId, DurableJobState.Succeeded);
        await secondWorker.StopAsync(CancellationToken.None);

        await using var context = await _factory.CreateDbContextAsync();
        var job = await context.Jobs.SingleAsync(item => item.Id == queued.JobId);
        Assert.Equal(DurableJobState.Succeeded, job.State);
        Assert.Equal(3, job.AttemptCount);
    }

    [Fact]
    public async Task StartupRecoveryRetriesAfterOneTransientStorageFailure()
    {
        var queued = await EnqueueAsync("recovery-retry", "recovery-transient");
        Assert.NotNull(await _queue.ClaimNextAsync("fixture-recovery-retry", ["recovery-retry"]));

        var faultingFactory = new FaultingDbContextFactory(_factory);
        faultingFactory.FailNext();
        var recoveryQueue = new DurableJobQueue(
            faultingFactory,
            _options,
            new JobPayloadPolicy(_options),
            _clock);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new CompletingHandler("recovery-retry", completed);
        using var services = new ServiceCollection().BuildServiceProvider();
        var worker = CreateWorker(recoveryQueue, [handler], services);

        await worker.StartAsync(CancellationToken.None);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForStateAsync(queued.JobId, DurableJobState.Succeeded);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(1, faultingFactory.FailuresObserved);
        Assert.Equal(1, handler.InvocationCount);
    }

    [Fact]
    public async Task WorkerRetriesProducedCompletionWithoutRerunningHandler()
    {
        var queued = await EnqueueAsync("completion-retry", "completion-transient");
        var faultingFactory = new FaultingDbContextFactory(_factory);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new CompletionFailureHandler(
            "completion-retry",
            faultingFactory,
            completed);
        var completionQueue = new DurableJobQueue(
            faultingFactory,
            _options,
            new JobPayloadPolicy(_options),
            _clock);
        using var services = new ServiceCollection().BuildServiceProvider();
        var worker = CreateWorker(completionQueue, [handler], services);

        await worker.StartAsync(CancellationToken.None);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForStateAsync(queued.JobId, DurableJobState.Succeeded);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(1, faultingFactory.FailuresObserved);
        Assert.Equal(1, handler.InvocationCount);
    }

    [Fact]
    public async Task StartupWaitsForStorageReadinessBeforeRecoveryOrDispatch()
    {
        var queued = await EnqueueAsync("readiness", "readiness-gate");
        Assert.NotNull(await _queue.ClaimNextAsync("fixture-readiness", ["readiness"]));
        _storageState.Set(DurableStorageReadiness.Initializing, "worker-fixture");

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var services = new ServiceCollection().BuildServiceProvider();
        var worker = CreateWorker(
            [new CompletingHandler("readiness", started)],
            services);

        await worker.StartAsync(CancellationToken.None);
        await using (var context = await _factory.CreateDbContextAsync())
        {
            var job = await context.Jobs.SingleAsync(item => item.Id == queued.JobId);
            Assert.Equal(DurableJobState.Running, job.State);
            Assert.Equal(1, job.AttemptCount);
        }
        Assert.False(started.Task.IsCompleted);

        _storageState.Set(DurableStorageReadiness.Ready, "worker-fixture");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForStateAsync(queued.JobId, DurableJobState.Succeeded);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WorkerHonorsGlobalAndConfiguredTypeConcurrencyCaps()
    {
        _options.MaximumConcurrentJobs = 2;
        _options.TypeConcurrencyLimits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["limited"] = 1
        };
        var firstLimited = await EnqueueAsync("limited", "limited-one");
        var secondLimited = await EnqueueAsync("limited", "limited-two");
        var other = await EnqueueAsync("other", "other-one");
        var limitedHandler = new GateHandler("limited", expectedInvocations: 2);
        var otherHandler = new GateHandler("other", expectedInvocations: 1);

        using var services = new ServiceCollection().BuildServiceProvider();
        var worker = CreateWorker([limitedHandler, otherHandler], services);

        await worker.StartAsync(CancellationToken.None);
        await Task.WhenAll(
            limitedHandler.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)),
            otherHandler.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, limitedHandler.InvocationCount);
        Assert.Equal(1, otherHandler.InvocationCount);
        Assert.False(limitedHandler.SecondStarted.Task.IsCompleted);

        limitedHandler.Release();
        await limitedHandler.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        otherHandler.Release();
        await WaitForStateAsync(firstLimited.JobId, DurableJobState.Succeeded);
        await WaitForStateAsync(secondLimited.JobId, DurableJobState.Succeeded);
        await WaitForStateAsync(other.JobId, DurableJobState.Succeeded);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WorkerUsesOneSlotForUnconfiguredTypes()
    {
        _options.MaximumConcurrentJobs = 3;
        var first = await EnqueueAsync("defaulted", "defaulted-one");
        var second = await EnqueueAsync("defaulted", "defaulted-two");
        var other = await EnqueueAsync("independent", "independent-one");
        var defaulted = new GateHandler("defaulted", expectedInvocations: 2);
        var independent = new GateHandler("independent", expectedInvocations: 1);

        using var services = new ServiceCollection().BuildServiceProvider();
        var worker = CreateWorker([defaulted, independent], services);

        await worker.StartAsync(CancellationToken.None);
        await Task.WhenAll(
            defaulted.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)),
            independent.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, defaulted.InvocationCount);
        Assert.False(defaulted.SecondStarted.Task.IsCompleted);

        defaulted.Release();
        await defaulted.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        independent.Release();
        await WaitForStateAsync(first.JobId, DurableJobState.Succeeded);
        await WaitForStateAsync(second.JobId, DurableJobState.Succeeded);
        await WaitForStateAsync(other.JobId, DurableJobState.Succeeded);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WorkerCapsTotalActiveHandlersEvenAcrossIndependentTypes()
    {
        _options.MaximumConcurrentJobs = 2;
        _options.TypeConcurrencyLimits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["alpha"] = 2,
            ["beta"] = 2,
            ["gamma"] = 2
        };
        var alpha = await EnqueueAsync("alpha", "global-alpha", priority: 3);
        var beta = await EnqueueAsync("beta", "global-beta", priority: 2);
        var gamma = await EnqueueAsync("gamma", "global-gamma", priority: 1);
        var alphaHandler = new GateHandler("alpha", expectedInvocations: 1);
        var betaHandler = new GateHandler("beta", expectedInvocations: 1);
        var gammaHandler = new GateHandler("gamma", expectedInvocations: 1);

        using var services = new ServiceCollection().BuildServiceProvider();
        var worker = CreateWorker([alphaHandler, betaHandler, gammaHandler], services);

        await worker.StartAsync(CancellationToken.None);
        await Task.WhenAny(
            alphaHandler.FirstStarted.Task,
            betaHandler.FirstStarted.Task,
            gammaHandler.FirstStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(
            alphaHandler.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)),
            betaHandler.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, gammaHandler.InvocationCount);

        alphaHandler.Release();
        betaHandler.Release();
        await gammaHandler.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        gammaHandler.Release();
        await WaitForStateAsync(alpha.JobId, DurableJobState.Succeeded);
        await WaitForStateAsync(beta.JobId, DurableJobState.Succeeded);
        await WaitForStateAsync(gamma.JobId, DurableJobState.Succeeded);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WorkerCancelsExecutionWhenCurrentClaimIsRevoked()
    {
        var queued = await EnqueueAsync("revocable", "revocable-job");
        var handler = new CancellationHandler("revocable");
        using var services = new ServiceCollection().BuildServiceProvider();
        var worker = CreateWorker([handler], services);

        await worker.StartAsync(CancellationToken.None);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await _queue.RequestCancellationAsync(queued.JobId, _userId));
        await handler.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForStateAsync(queued.JobId, DurableJobState.Cancelled);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WorkerShutdownAwaitsAllTrackedHandlers()
    {
        _options.MaximumConcurrentJobs = 2;
        _options.TypeConcurrencyLimits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["shutdown"] = 2
        };
        var first = await EnqueueAsync("shutdown", "shutdown-one");
        var second = await EnqueueAsync("shutdown", "shutdown-two");
        var handler = new ShutdownHandler("shutdown", expectedInvocations: 2);
        _cleanupActions.Add(handler.ReleaseCompletion);
        using var services = new ServiceCollection().BuildServiceProvider();
        var worker = CreateWorker([handler], services);

        await worker.StartAsync(CancellationToken.None);
        await handler.AllStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = worker.StopAsync(CancellationToken.None);
        await handler.AllCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(stopping.IsCompleted);

        handler.ReleaseCompletion();
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEqual(DurableJobState.Succeeded, await ReadStateAsync(first.JobId));
        Assert.NotEqual(DurableJobState.Succeeded, await ReadStateAsync(second.JobId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(33)]
    public void OptionsRejectInvalidGlobalConcurrency(int maximumConcurrentJobs)
    {
        _options.MaximumConcurrentJobs = maximumConcurrentJobs;
        Assert.Throws<InvalidOperationException>(() => _options.Validate());
    }

    [Fact]
    public void OptionsRejectInvalidTypeConcurrencyConfiguration()
    {
        _options.MaximumConcurrentJobs = 2;
        _options.TypeConcurrencyLimits = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [" Work "] = 1,
            ["work"] = 1
        };
        Assert.Throws<InvalidOperationException>(() => _options.Validate());
    }

    private Task<DurableJobEnqueueResult> EnqueueAsync(string type, string key, int priority = 0) =>
        _queue.EnqueueAsync(new DurableJobEnqueueRequest<object>(
            type,
            key,
            new { item = key },
            _userId,
            Priority: priority));

    private DurableJobWorker CreateWorker(
        IEnumerable<IDurableJobHandler> handlers,
        IServiceProvider services) =>
        CreateWorker(_queue, handlers, services);

    private DurableJobWorker CreateWorker(
        DurableJobQueue queue,
        IEnumerable<IDurableJobHandler> handlers,
        IServiceProvider services)
    {
        var worker = new DurableJobWorker(
            queue,
            _options,
            _storageState,
            services,
            handlers,
            NullLogger<DurableJobWorker>.Instance);
        _workers.Add(worker);
        return worker;
    }

    private async Task<DurableJobState> ReadStateAsync(Guid jobId)
    {
        await using var context = await _factory.CreateDbContextAsync();
        return await context.Jobs
            .Where(item => item.Id == jobId)
            .Select(item => item.State)
            .SingleAsync();
    }

    private async Task WaitForStateAsync(Guid jobId, DurableJobState expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var state = await ReadStateAsync(jobId);
            if (state == expected)
                return;
            await Task.Delay(20, timeout.Token);
        }
    }

    public async Task DisposeAsync()
    {
        foreach (var cleanup in _cleanupActions)
            cleanup();

        foreach (var worker in _workers.AsEnumerable().Reverse())
        {
            try
            {
                await worker.StopAsync(CancellationToken.None);
            }
            catch (Exception)
            {
                // A failed test must not leave a worker using the fixture database.
            }
        }

        await _database.DisposeAsync();
    }

    private sealed class FixtureClock(DateTimeOffset now) : IPlatformClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class TestDbContextFactory(DbContextOptions<AllstarrDbContext> options)
        : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);

        public Task<AllstarrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(new AllstarrDbContext(options));
    }

    private sealed class FaultingDbContextFactory(
        IDbContextFactory<AllstarrDbContext> inner) : IDbContextFactory<AllstarrDbContext>
    {
        private int _failNext;
        private int _failuresObserved;

        public int FailuresObserved => Volatile.Read(ref _failuresObserved);

        public void FailNext() => Interlocked.Exchange(ref _failNext, 1);

        public AllstarrDbContext CreateDbContext()
        {
            ThrowIfArmed();
            return inner.CreateDbContext();
        }

        public async Task<AllstarrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default)
        {
            ThrowIfArmed();
            return await inner.CreateDbContextAsync(cancellationToken);
        }

        private void ThrowIfArmed()
        {
            if (Interlocked.Exchange(ref _failNext, 0) != 1)
                return;

            Interlocked.Increment(ref _failuresObserved);
            throw new InvalidOperationException("synthetic storage fault");
        }
    }

    private sealed class BlockingHandler(string jobType, TaskCompletionSource started) : IDurableJobHandler
    {
        public string JobType => jobType;

        public async Task<DurableJobCompletion> ExecuteAsync(
            DurableJobExecutionContext context,
            CancellationToken cancellationToken)
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return DurableJobCompletion.Success();
        }
    }

    private sealed class CompletingHandler(string jobType, TaskCompletionSource completed) : IDurableJobHandler
    {
        private int _invocationCount;

        public string JobType => jobType;
        public int InvocationCount => Volatile.Read(ref _invocationCount);

        public Task<DurableJobCompletion> ExecuteAsync(
            DurableJobExecutionContext context,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _invocationCount);
            completed.TrySetResult();
            return Task.FromResult(DurableJobCompletion.Success());
        }
    }

    private sealed class CompletionFailureHandler(
        string jobType,
        FaultingDbContextFactory factory,
        TaskCompletionSource completed) : IDurableJobHandler
    {
        private int _invocationCount;

        public string JobType => jobType;
        public int InvocationCount => Volatile.Read(ref _invocationCount);

        public Task<DurableJobCompletion> ExecuteAsync(
            DurableJobExecutionContext context,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _invocationCount);
            factory.FailNext();
            completed.TrySetResult();
            return Task.FromResult(DurableJobCompletion.Success());
        }
    }

    private sealed class GateHandler(string jobType, int expectedInvocations) : IDurableJobHandler
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _invocationCount;

        public string JobType => jobType;
        public int InvocationCount => Volatile.Read(ref _invocationCount);
        public TaskCompletionSource FirstStarted { get; } = NewSignal();
        public TaskCompletionSource SecondStarted { get; } = NewSignal();

        public async Task<DurableJobCompletion> ExecuteAsync(
            DurableJobExecutionContext context,
            CancellationToken cancellationToken)
        {
            var invocation = Interlocked.Increment(ref _invocationCount);
            if (invocation == 1)
                FirstStarted.TrySetResult();
            else if (invocation == 2)
                SecondStarted.TrySetResult();
            else if (invocation > expectedInvocations)
                throw new InvalidOperationException("Unexpected fixture invocation.");
            await _release.Task.WaitAsync(cancellationToken);
            return DurableJobCompletion.Success();
        }

        public void Release() => _release.TrySetResult();

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class CancellationHandler(string jobType) : IDurableJobHandler
    {
        public string JobType => jobType;
        public TaskCompletionSource Started { get; } = NewSignal();
        public TaskCompletionSource CancellationObserved { get; } = NewSignal();

        public async Task<DurableJobCompletion> ExecuteAsync(
            DurableJobExecutionContext context,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return DurableJobCompletion.Success();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult();
                throw;
            }
        }

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ShutdownHandler(string jobType, int expectedInvocations) : IDurableJobHandler
    {
        private readonly TaskCompletionSource _allowCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _started;
        private int _cancelled;

        public string JobType => jobType;
        public TaskCompletionSource AllStarted { get; } = NewSignal();
        public TaskCompletionSource AllCancellationObserved { get; } = NewSignal();

        public async Task<DurableJobCompletion> ExecuteAsync(
            DurableJobExecutionContext context,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _started) == expectedInvocations)
                AllStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return DurableJobCompletion.Success();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (Interlocked.Increment(ref _cancelled) == expectedInvocations)
                    AllCancellationObserved.TrySetResult();
                await _allowCompletion.Task;
                return DurableJobCompletion.Cancelled();
            }
        }

        public void ReleaseCompletion() => _allowCompletion.TrySetResult();

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

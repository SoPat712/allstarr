using allstarr.Core.Jobs;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace allstarr.Tests;

public sealed partial class DurableJobWorkerConcurrencyTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CompletionRecoveryRetriesAfterPersistingOutcomeWithoutRerunningWork(bool failRecovery, bool exhaustRetry)
    {
        var receipt = await _queue.EnqueueAsync(new DurableJobEnqueueRequest<object>(
            "completion-resource", "completed-resource", new { }, _userId, MaxAttempts: 1));
        int? savedAuditCount = null;
        var handler = new CompletionRecoveryHandler(failRecovery, exhaustRetry, async (claim, _) =>
        {
            await using var db = await _factory.CreateDbContextAsync();
            var job = await db.Jobs.SingleAsync(item => item.Id == receipt.JobId);
            Assert.Equal(DurableJobState.Failed, job.State);
            Assert.Equal(claim.AttemptNumber, job.AttemptCount);
            Assert.NotNull(job.CompletedAt);
            var count = await db.AuditEvents.CountAsync();
            savedAuditCount ??= count;
            Assert.Equal(savedAuditCount.Value, count);
        });
        using var services = new ServiceCollection().BuildServiceProvider();
        var worker = CreateWorker([handler], services);
        await worker.StartAsync(CancellationToken.None);
        await handler.Recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(CancellationToken.None);
        Assert.Equal(1, handler.Executions);
        Assert.Equal(failRecovery ? 2 : 1, handler.Recoveries);
        await using var fresh = await _factory.CreateDbContextAsync();
        Assert.Equal(1, (await fresh.Jobs.SingleAsync(item => item.Id == receipt.JobId)).AttemptCount);
        Assert.Equal(savedAuditCount, await fresh.AuditEvents.CountAsync());
    }

    [Fact]
    public async Task ShutdownDuringCompletionRecoveryPreservesTheOutcomeForStartupRecovery()
    {
        var receipt = await EnqueueAsync("completion-resource", "shutdown-resource");
        var recoveryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new CompletionRecoveryHandler(false, false, async (_, token) =>
        {
            recoveryStarted.TrySetResult();
            await never.Task.WaitAsync(token);
        });
        using var services = new ServiceCollection().BuildServiceProvider();
        var firstWorker = CreateWorker([first], services);
        await firstWorker.StartAsync(default);
        await recoveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await firstWorker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new CompletionRecoveryHandler(false, false, (_, _) => Task.CompletedTask, async () =>
        {
            await using var db = await _factory.CreateDbContextAsync();
            var job = await db.Jobs.SingleAsync(item => item.Id == receipt.JobId);
            Assert.Equal(DurableJobState.Failed, job.State);
            Assert.Equal(1, job.AttemptCount);
            resumed.TrySetResult();
        });
        var secondWorker = CreateWorker([second], services);
        await secondWorker.StartAsync(default);
        await resumed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await secondWorker.StopAsync(default);
        Assert.Equal(1, first.Executions);
        Assert.Equal(0, second.Executions);
    }

    private sealed class CompletionRecoveryHandler(bool failRecovery, bool exhaustRetry,
        Func<DurableJobClaim, CancellationToken, Task> observe, Func<Task>? startup = null)
        : IDurableJobHandler, IDurableJobRecoveryHandler
    {
        public string JobType => "completion-resource";
        public int Executions { get; private set; }
        public int Recoveries { get; private set; }
        public TaskCompletionSource Recovered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task RecoverAsync(CancellationToken cancellationToken) => startup?.Invoke() ?? Task.CompletedTask;
        public Task<DurableJobCompletion> ExecuteAsync(DurableJobExecutionContext context, CancellationToken cancellationToken)
        {
            Executions++;
            return Task.FromResult(exhaustRetry
                ? DurableJobCompletion.Retry("fixture", "Fixture work failed.")
                : DurableJobCompletion.Failure("fixture", "Fixture work failed."));
        }
        public async Task RecoverCompletedAsync(DurableJobClaim claim, CancellationToken cancellationToken)
        {
            Recoveries++;
            await observe(claim, cancellationToken);
            if (failRecovery && Recoveries == 1) throw new IOException("Fixture resource recovery failure.");
            Recovered.TrySetResult();
        }
    }
}

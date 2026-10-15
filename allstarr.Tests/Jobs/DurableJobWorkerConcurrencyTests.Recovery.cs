using allstarr.Core.Jobs;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace allstarr.Tests;

public sealed partial class DurableJobWorkerConcurrencyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResourceRecoveryFinishesAfterJobRecoveryAndBeforeAnyNewClaim(bool transientFailure)
    {
        var receipt = await EnqueueAsync("resource-recovery", "resource-before-claim");
        Assert.NotNull(await _queue.ClaimNextAsync("interrupted", ["resource-recovery"]));
        var handler = new ResourceRecoveryHandler(transientFailure, async () =>
        {
            await using var db = await _factory.CreateDbContextAsync();
            var job = await db.Jobs.SingleAsync(item => item.Id == receipt.JobId);
            Assert.Equal(DurableJobState.Pending, job.State);
            Assert.Equal(1, job.AttemptCount);
        });
        _cleanupActions.Add(() => handler.AllowRecovery.TrySetResult());
        using var services = new ServiceCollection().BuildServiceProvider();
        var worker = CreateWorker([handler], services);
        await worker.StartAsync(CancellationToken.None);
        await handler.RecoveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, handler.InvocationCount);
        await using (var db = await _factory.CreateDbContextAsync())
            Assert.Equal(DurableJobState.Pending, (await db.Jobs.SingleAsync(item => item.Id == receipt.JobId)).State);
        handler.AllowRecovery.TrySetResult();
        await WaitForStateAsync(receipt.JobId, DurableJobState.Succeeded);
        await worker.StopAsync(CancellationToken.None);
        Assert.Equal(transientFailure ? 2 : 1, handler.RecoveryCount);
        Assert.Equal(1, handler.InvocationCount);
    }

    private sealed class ResourceRecoveryHandler(bool transientFailure, Func<Task> observe)
        : IDurableJobHandler, IDurableJobRecoveryHandler
    {
        public string JobType => "resource-recovery";
        public int RecoveryCount { get; private set; }
        public int InvocationCount { get; private set; }
        public TaskCompletionSource RecoveryStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowRecovery { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool recovered;

        public async Task RecoverAsync(CancellationToken cancellationToken)
        {
            RecoveryCount++;
            await observe();
            if (transientFailure && RecoveryCount == 1) throw new IOException("Transient recovery fixture failure.");
            RecoveryStarted.TrySetResult();
            await AllowRecovery.Task.WaitAsync(cancellationToken);
            recovered = true;
        }

        public Task<DurableJobCompletion> ExecuteAsync(DurableJobExecutionContext context, CancellationToken cancellationToken)
        {
            Assert.True(recovered);
            InvocationCount++;
            return Task.FromResult(DurableJobCompletion.Success());
        }
    }
}

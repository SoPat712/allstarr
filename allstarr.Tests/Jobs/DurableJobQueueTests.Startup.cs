using System.Data.Common;
using allstarr.Core.Jobs;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace allstarr.Tests;

public sealed partial class DurableJobQueueTests
{
    [Fact]
    public async Task StartupRecoveryReadsHistoryOutsideWriterAndCommitsShortBatches()
    {
        for (var index = 0; index < 51; index++)
            await Enqueue("placement", "batch-recovery-" + index);
        await using (var db = await _factory.CreateDbContextAsync())
            await db.Jobs.ExecuteUpdateAsync(setters => setters.SetProperty(item => item.State, DurableJobState.Running));
        var probe = new RecoveryProbe();
        var options = new DbContextOptionsBuilder<AllstarrDbContext>(_database.Options).AddInterceptors(probe).Options;
        var queue = new DurableJobQueue(new TestDbContextFactory(options), _options, new JobPayloadPolicy(_options), _clock);
        Assert.Equal(51, await queue.RecoverInterruptedAsync());
        Assert.Equal(51, probe.HistoryReads);
        Assert.Equal(0, probe.HistoryReadsInsideTransaction);
        Assert.All(probe.BatchSizes, size => Assert.InRange(size, 1, 25));
        Assert.Equal(3, probe.BatchSizes.Count);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Equal(51, await verification.Jobs.CountAsync(item => item.State == DurableJobState.Pending));
        Assert.Equal(51, await verification.AuditEvents.CountAsync(item => item.Category == "job-attempt"));
    }

    [Fact]
    public async Task StartupRecoveryRechecksCancellationChangedDuringHistoryRead()
    {
        var queued = await Enqueue("placement", "cancel-during-recovery-read");
        Assert.NotNull(await _queue.ClaimNextAsync("previous-process"));
        var probe = new RecoveryProbe
        {
            BeforeHistoryRead = async () => Assert.True(await _queue.RequestCancellationAsync(queued.JobId, _userId))
        };
        var options = new DbContextOptionsBuilder<AllstarrDbContext>(_database.Options).AddInterceptors(probe).Options;
        var queue = new DurableJobQueue(new TestDbContextFactory(options), _options, new JobPayloadPolicy(_options), _clock);
        Assert.Equal(1, await queue.RecoverInterruptedAsync());
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(DurableJobState.Cancelled, (await db.Jobs.SingleAsync()).State);
        Assert.Equal("cancelled", Assert.Single(await db.AuditEvents.Where(item => item.Category == "job-attempt").ToArrayAsync()).Outcome);
    }

    private sealed class RecoveryProbe : DbCommandInterceptor, ISaveChangesInterceptor
    {
        public int HistoryReads;
        public int HistoryReadsInsideTransaction;
        public readonly List<int> BatchSizes = [];
        public Func<Task>? BeforeHistoryRead;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"audit_events\"", StringComparison.Ordinal))
            {
                HistoryReads++;
                if (command.Transaction != null) HistoryReadsInsideTransaction++;
                if (BeforeHistoryRead is { } before)
                {
                    BeforeHistoryRead = null;
                    await before();
                }
            }
            return result;
        }

        public ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            BatchSizes.Add(eventData.Context!.ChangeTracker.Entries<DurableJobRecord>().Count(item => item.State == EntityState.Modified));
            return ValueTask.FromResult(result);
        }
    }
}

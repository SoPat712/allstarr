using System.Diagnostics;

namespace allstarr.Services.Common;

// Every request slows down when the whole process stalls, and nothing else says why. This watches from a thread of its
// own so it keeps running when the shared pool can't: a late tick is a freeze or a collection, a late pool item is starvation.
public sealed class RuntimeStallMonitor(ILogger<RuntimeStallMonitor> logger) : IHostedService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan WorthLogging = TimeSpan.FromMilliseconds(500);
    private readonly CancellationTokenSource stopping = new();
    private Thread? thread;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        thread = new Thread(Watch) { IsBackground = true, Name = "runtime-stall-monitor" };
        thread.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        stopping.Cancel();
        return Task.CompletedTask;
    }

    private void Watch()
    {
        var clock = Stopwatch.StartNew();
        var pause = GC.GetTotalPauseDuration();
        var collections = GC.CollectionCount(2);
        var last = clock.Elapsed;
        var poolSent = TimeSpan.Zero;
        var poolAnswered = 0L;
        while (!stopping.Token.WaitHandle.WaitOne(Tick))
        {
            var now = clock.Elapsed;
            var late = now - last - Tick;
            last = now;
            // Only one probe is out at a time, so a stuck pool is measured from when it first stopped answering.
            var answered = Interlocked.Read(ref poolAnswered);
            var poolLate = answered == 0 && poolSent != TimeSpan.Zero ? now - poolSent : TimeSpan.Zero;
            if (answered != 0 || poolSent == TimeSpan.Zero)
            {
                Interlocked.Exchange(ref poolAnswered, 0);
                poolSent = now;
                ThreadPool.UnsafeQueueUserWorkItem(_ => Interlocked.Exchange(ref poolAnswered, 1), null);
            }

            var totalPause = GC.GetTotalPauseDuration();
            var fullCollections = GC.CollectionCount(2);
            if (late >= WorthLogging || poolLate >= WorthLogging)
            {
                logger.LogWarning(
                    "Runtime stall: monitor thread {ThreadLate} ms late, pool {PoolLate} ms behind; collections paused {Paused} ms ({Full} full); {Queued} queued on {Threads} pool threads",
                    (long)late.TotalMilliseconds, (long)poolLate.TotalMilliseconds, (long)(totalPause - pause).TotalMilliseconds,
                    fullCollections - collections, ThreadPool.PendingWorkItemCount, ThreadPool.ThreadCount);
            }
            pause = totalPause;
            collections = fullCollections;
        }
    }
}

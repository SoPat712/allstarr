using System.Diagnostics;
using Jint;

namespace allstarr.Core.Extensions;

internal sealed class ExtensionInvocationBudget : IDisposable
{
    private static readonly AsyncLocal<ExtensionInvocationBudget?> Ambient = new();
    private readonly ExtensionInvocationBudget? prior = Ambient.Value;
    private readonly CancellationTokenSource cancellation;
    private readonly TimeProvider clock;
    private readonly long started;
    private readonly CancellationTokenSource timeout;
    private readonly TimeSpan duration;
    private readonly DateTimeOffset? resolutionDeadline;
    private readonly object phaseGate = new();
    private bool transferring;

    public static ExtensionInvocationBudget? Current => Ambient.Value;
    public CancellationToken Token => cancellation.Token;
    public bool IsDownload { get; }
    public bool HasResolutionDeadline => resolutionDeadline.HasValue;
    private long FullRemainingMilliseconds => Math.Max(0, (long)(duration - clock.GetElapsedTime(started)).TotalMilliseconds);
    public long ResolutionRemainingMilliseconds => resolutionDeadline is { } deadline
        ? Math.Min(FullRemainingMilliseconds, Math.Max(0, (long)(deadline - clock.GetUtcNow()).TotalMilliseconds))
        : FullRemainingMilliseconds;
    public long RemainingMilliseconds
    {
        get { lock (phaseGate) return transferring ? FullRemainingMilliseconds : ResolutionRemainingMilliseconds; }
    }

    public ExtensionInvocationBudget(TimeSpan duration, CancellationToken token, TimeProvider clock, bool isDownload = false,
        DateTimeOffset? resolutionDeadline = null)
    {
        IsDownload = isDownload;
        this.duration = duration;
        this.clock = clock;
        this.resolutionDeadline = resolutionDeadline;
        started = clock.GetTimestamp();
        timeout = new CancellationTokenSource(resolutionDeadline.HasValue
            ? TimeSpan.FromMilliseconds(ResolutionRemainingMilliseconds) : duration, clock);
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        Ambient.Value = this;
    }

    public Task DelayAsync(int milliseconds) => Task.Delay(TimeSpan.FromMilliseconds(milliseconds), clock, Token);

    public void Check()
    {
        if (RemainingMilliseconds <= 0) cancellation.Cancel();
        cancellation.Token.ThrowIfCancellationRequested();
    }

    public void AcceptMediaResponse(HttpResponseMessage response, string target)
    {
        if (!IsDownload || resolutionDeadline == null || !response.IsSuccessStatusCode) return;
        var mime = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
        var audioTarget = Path.GetExtension(target).ToLowerInvariant() is
            ".audio" or ".flac" or ".mp3" or ".m4a" or ".aac" or ".ogg" or ".opus" or ".wav";
        if (mime?.StartsWith("audio/", StringComparison.Ordinal) != true &&
            !(audioTarget && mime is null or "application/octet-stream" or "application/ogg" or "video/mp2t" or "video/mp4")) return;
        lock (phaseGate)
        {
            Check();
            if (transferring) return;
            timeout.CancelAfter(TimeSpan.FromMilliseconds(FullRemainingMilliseconds));
            cancellation.Token.ThrowIfCancellationRequested();
            transferring = true;
        }
    }

    public void Dispose()
    {
        Ambient.Value = prior;
        cancellation.Dispose();
        timeout.Dispose();
    }

    internal sealed class ExecutionConstraint : Constraint
    {
        private readonly Stopwatch fallback = Stopwatch.StartNew();
        public override void Reset() => fallback.Restart();
        public override void Check()
        {
            if (Current is { } budget) budget.Check();
            else if (fallback.Elapsed > TimeSpan.FromSeconds(12)) throw new TimeoutException("Extension invocation timed out.");
        }
    }
}

using System.Diagnostics;

namespace allstarr.Core.Operations;

public enum RequestTimingStage { Jellyfin, Db, Provider }

public sealed class RequestTiming : IDisposable
{
    private static readonly AsyncLocal<RequestTiming?> Ambient = new();
    private readonly RequestTiming? parent = Ambient.Value;
    private readonly long started = Stopwatch.GetTimestamp();
    private long jellyfin, db, provider;
    private int calls, hits, misses;
    public static RequestTiming? Current => Ambient.Value;

    public static RequestTiming Begin()
    {
        var timing = new RequestTiming();
        Ambient.Value = timing;
        return timing;
    }

    public void Add(RequestTimingStage stage, TimeSpan duration)
    {
        switch (stage)
        {
            case RequestTimingStage.Jellyfin:
                Interlocked.Add(ref jellyfin, duration.Ticks);
                Interlocked.Increment(ref calls);
                break;
            case RequestTimingStage.Db: Interlocked.Add(ref db, duration.Ticks); break;
            case RequestTimingStage.Provider: Interlocked.Add(ref provider, duration.Ticks); break;
        }
    }

    public void NoteCache(bool hit)
    {
        if (hit) Interlocked.Increment(ref hits);
        else Interlocked.Increment(ref misses);
    }

    public Result Snapshot(string route) => new(Stopwatch.GetElapsedTime(started).TotalMilliseconds,
        TimeSpan.FromTicks(Interlocked.Read(ref jellyfin)).TotalMilliseconds, Volatile.Read(ref calls),
        TimeSpan.FromTicks(Interlocked.Read(ref db)).TotalMilliseconds,
        TimeSpan.FromTicks(Interlocked.Read(ref provider)).TotalMilliseconds,
        Volatile.Read(ref hits) > 0 ? Volatile.Read(ref misses) > 0 ? "hit/miss" : "hit" : Volatile.Read(ref misses) > 0 ? "miss" : "none", route);

    public void Dispose() => Ambient.Value = parent;

    public sealed record Result(double TotalMs, double JellyfinMs, int JellyfinCalls, double DbMs, double ProviderMs,
        string Cache, string Route)
    {
        public string Header => FormattableString.Invariant($"total;dur={TotalMs:0.0}, jellyfin;dur={JellyfinMs:0.0};desc=\"{JellyfinCalls} calls\", db;dur={DbMs:0.0}, provider;dur={ProviderMs:0.0}, cache;desc=\"{Cache}\", route;desc=\"{Route}\"");
    }
}

public sealed class RequestTimingHandler(bool mediaServer) : DelegatingHandler
{
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var timing = RequestTiming.Current;
        if (timing == null) return base.Send(request, cancellationToken);
        var started = Stopwatch.GetTimestamp();
        try { return base.Send(request, cancellationToken); }
        finally { timing.Add(mediaServer ? RequestTimingStage.Jellyfin : RequestTimingStage.Provider, Stopwatch.GetElapsedTime(started)); }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var timing = RequestTiming.Current;
        if (timing == null) return await base.SendAsync(request, cancellationToken);
        var started = Stopwatch.GetTimestamp();
        try { return await base.SendAsync(request, cancellationToken); }
        finally { timing.Add(mediaServer ? RequestTimingStage.Jellyfin : RequestTimingStage.Provider, Stopwatch.GetElapsedTime(started)); }
    }
}

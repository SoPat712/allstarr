using System.Diagnostics;

namespace allstarr.Core.Operations;

public enum RequestTimingStage { Jellyfin, Db, Provider }

public sealed class RequestTiming : IDisposable
{
    private static readonly AsyncLocal<RequestTiming?> Ambient = new();
    private static readonly IReadOnlyDictionary<string, double> EmptyStages = new Dictionary<string, double>();
    private readonly RequestTiming? parent = Ambient.Value;
    private readonly long started = Stopwatch.GetTimestamp();
    private readonly object stageLock = new();
    private Dictionary<string, long>? stages;
    private double preMs;
    private int inflight;
    private long jellyfin, db, provider;
    private int calls, hits, misses;
    public static RequestTiming? Current => Ambient.Value;

    public static RequestTiming Begin(double preMs = 0, int inflight = 0)
    {
        var timing = new RequestTiming { preMs = preMs, inflight = inflight };
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

    public StageScope Stage(string name)
    {
        if (name is not ("lock" or "build" or "render" or "match" or "merge" or "serialize" or "queue" or
            "library" or "sources" or "saved" or "owned" or "resolve" or "fetch" or "store")) return default;
        lock (stageLock)
        {
            stages ??= new(StringComparer.Ordinal);
            if (!stages.ContainsKey(name))
            {
                if (stages.Count == 8) return default;
                stages.Add(name, 0);
            }
        }
        return new(this, name);
    }

    public Result Snapshot(string route)
    {
        IReadOnlyDictionary<string, double> values;
        lock (stageLock)
            values = stages == null ? EmptyStages : stages.ToDictionary(stage => stage.Key,
                stage => TimeSpan.FromTicks(stage.Value).TotalMilliseconds, StringComparer.Ordinal);
        return new Result(Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            TimeSpan.FromTicks(Interlocked.Read(ref jellyfin)).TotalMilliseconds, Volatile.Read(ref calls),
            TimeSpan.FromTicks(Interlocked.Read(ref db)).TotalMilliseconds,
            TimeSpan.FromTicks(Interlocked.Read(ref provider)).TotalMilliseconds,
            Volatile.Read(ref hits) > 0 ? Volatile.Read(ref misses) > 0 ? "hit/miss" : "hit" : Volatile.Read(ref misses) > 0 ? "miss" : "none", route)
        { PreMs = preMs, Inflight = inflight, Stages = values };
    }

    public void Dispose() => Ambient.Value = parent;

    public struct StageScope : IDisposable
    {
        private RequestTiming? timing;
        private readonly string name;
        private readonly long started;

        internal StageScope(RequestTiming timing, string name)
        {
            this.timing = timing;
            this.name = name;
            started = Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            var owner = timing;
            timing = null;
            if (owner == null) return;
            var ticks = Stopwatch.GetElapsedTime(started).Ticks;
            lock (owner.stageLock) owner.stages![name] += ticks;
        }
    }

    public sealed record Result(double TotalMs, double JellyfinMs, int JellyfinCalls, double DbMs, double ProviderMs,
        string Cache, string Route)
    {
        public double PreMs { get; init; }
        public int Inflight { get; init; }
        public IReadOnlyDictionary<string, double> Stages { get; init; } = EmptyStages;
        public string StageSummary => string.Join(", ", Stages.Select(stage =>
            FormattableString.Invariant($"{stage.Key} {stage.Value:0.0} ms")));
        public string Header => FormattableString.Invariant($"total;dur={TotalMs:0.0}, jellyfin;dur={JellyfinMs:0.0};desc=\"{JellyfinCalls} calls\", db;dur={DbMs:0.0}, provider;dur={ProviderMs:0.0}, cache;desc=\"{Cache}\", route;desc=\"{Route}\", pre;dur={PreMs:0.0}, inflight;desc=\"{Inflight}\"") +
            string.Concat(Stages.Select(stage => FormattableString.Invariant($", {stage.Key};dur={stage.Value:0.0}")));
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

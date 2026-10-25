using allstarr.Core.Protocols;

namespace allstarr.Services.Common;

public sealed class MediaCapacityLimits(IConfiguration configuration)
{
    public const string AudioKey = "Cache:AudioMaximumGiB";
    public const string StreamsKey = "Playback:ProviderStreamsPerListener";
    public const string ConversionsKey = "Media:MaximumConcurrentConversions";
    private int audioGiB = -1, streams = -1, conversions = -1;
    public long AudioMaximumBytes => checked((long)Math.Clamp(Value(ref audioGiB, AudioKey, 2), 1, 1024) * 1024 * 1024 * 1024);
    public int StreamsPerListener => Math.Clamp(Value(ref streams, StreamsKey, 6), 1, 64);
    public int Conversions => Math.Clamp(Value(ref conversions, ConversionsKey, 2), 1, 16);
    private int Value(ref int saved, string key, int fallback)
    {
        var value = Volatile.Read(ref saved);
        return value >= 0 ? value : configuration.GetValue(key, fallback);
    }
    internal void Apply(string key, int value)
    {
        if (key == AudioKey) Volatile.Write(ref audioGiB, value);
        else if (key == StreamsKey) Volatile.Write(ref streams, value);
        else if (key == ConversionsKey) Volatile.Write(ref conversions, value);
    }
}

public sealed class LocalCapacityException(string resource = "media-conversion") : InvalidOperationException(Code + ": " + MessageFor(resource))
{
    public const string Code = "local-capacity-unavailable";
    public string SafeMessage => MessageFor(resource);
    public int RetryAfterSeconds => 5;
    public static void MarkResponse(HttpResponse response)
    {
        response.Headers.RetryAfter = "5";
        response.Headers["X-Allstarr-Capacity"] = "busy";
    }
    public static bool IsLocal(Exception error) => error is LocalCapacityException || error.Message.Contains(Code, StringComparison.Ordinal) || error.InnerException != null && IsLocal(error.InnerException);
    private static string MessageFor(string resource) => resource == "audio-cache-root"
        ? "The temporary audio cache directory is unavailable. Check its ownership and permissions."
        : resource == "audio-cache" ? "The audio cache is full. Increase Maximum audio cache (GiB); protected and active files were preserved."
        : "Allstarr is at capacity. Try again shortly.";
}

public sealed class MediaConversionGate(MediaCapacityLimits? limits = null)
{
    private readonly BoundedOperationGate gate = new(() => limits?.Conversions ?? 2);
    public async ValueTask<IDisposable> EnterAsync(CancellationToken token) =>
        await gate.TryEnterAsync(token) ?? throw new LocalCapacityException();
    public async Task<T> RunAsync<T>(Func<Task<T>> spawn, CancellationToken token)
    {
        using var lease = await EnterAsync(token);
        return await spawn();
    }
}

public sealed class ProviderStreamAdmission(MediaCapacityLimits limits)
{
    private readonly object gate = new();
    private readonly Dictionary<(ProtocolKind Protocol, string Backend, string Principal), int> active = new();

    public IDisposable? TryEnter(ProtocolExecutionContext context)
    {
        var key = (context.Protocol, context.BackendInstanceId, context.VerifiedBackendPrincipalId);
        lock (gate)
        {
            var count = active.GetValueOrDefault(key);
            if (count >= limits.StreamsPerListener) return null;
            active[key] = count + 1;
            return new Lease(() =>
            {
                lock (gate)
                {
                    if (active[key] == 1) active.Remove(key);
                    else active[key]--;
                }
            });
        }
    }

    private sealed class Lease(Action release) : IDisposable
    {
        private Action? remaining = release;
        public void Dispose() => Interlocked.Exchange(ref remaining, null)?.Invoke();
    }
}

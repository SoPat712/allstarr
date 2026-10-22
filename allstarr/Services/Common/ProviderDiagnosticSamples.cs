using Microsoft.Extensions.Caching.Memory;
using allstarr.Core.Capabilities;

namespace allstarr.Services.Common;

public sealed record ProviderDiagnosticSample(string Title, string Artist, string? Isrc, int DurationSeconds,
    string? TrackId = null)
{
    public string Query => $"{Artist} {Title}";
}

public readonly record struct ProviderSampleCheck(bool Success, string? ReasonCode = null, bool SampleUnavailable = false, bool Stop = false)
{
    public static ProviderSampleCheck Missing => new(false, "sample-unavailable", true);
}

public sealed class ProviderDiagnosticSamples(TimeProvider? timeProvider = null, TimeSpan? timeout = null) : IDisposable
{
    public const int MaximumAttempts = 16;
    public const int SourceFailureThreshold = 3;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly MemoryCache state = new(new MemoryCacheOptions { SizeLimit = 512 });
    private readonly object sync = new();

    private static readonly ProviderDiagnosticSample[] Catalog =
    [
        new("Get Lucky", "Daft Punk", "USQX91300108", 369, "2Foc5Q5nqNiosCNqttzHof"),
        new("Never Gonna Give You Up", "Rick Astley", "GBARL9300135", 213, "4cOdK2wGLETKBW3PvgPWqT"),
        new("Bohemian Rhapsody", "Queen", "GBUM71029604", 354, "7tFiyTwD0nx5a1eklYtX2J"),
        new("Billie Jean", "Michael Jackson", "USSM19902991", 294, "5ChkMS8OtdzJeqyybCc9R5"),
        new("Dancing Queen", "ABBA", "SEAYD7601020", 230, "0GjEhVFGZW8afUYGChu3Rr"),
        new("California Dreamin'", "The Mamas & The Papas", "USMC16532697", 160, "7BsD3xcEhgTmc4zegwbkHj"),
        new("Hotel California", "Eagles", "USEE19900323", 391, "40riOy7x9W7GXjyGp4pjAv"),
        new("Blinding Lights", "The Weeknd", "USUG11904206", 200, "0VjIjW4GlUZAMYd2vXMi3b")
    ];

    public static IReadOnlyList<ProviderDiagnosticSample> Recordings(string providerId) =>
        providerId == "spotify" ? Catalog : Catalog.Select(item => item with { TrackId = null }).ToArray();

    public static IReadOnlyList<ProviderDiagnosticSample> Playlists { get; } = new[]
    {
        "80s hits", "90s hits", "Rock classics", "Classical essentials", "Jazz classics", "K-pop classics", "Soul classics", "Dance classics"
    }.Select(query => new ProviderDiagnosticSample(query, "", null, 0)).ToArray();

    public static IReadOnlyList<string> DiscoveryQueries(string providerId) => providerId.Contains("jiosaavn", StringComparison.OrdinalIgnoreCase)
        ? ["Arijit Singh", "A. R. Rahman", "Shreya Ghoshal", "Lata Mangeshkar", "Kishore Kumar", "Diljit Dosanjh", "Shankar Mahadevan", "Ilaiyaraaja"]
        : providerId.Contains("melon", StringComparison.OrdinalIgnoreCase)
            ? ["BTS", "IU", "BIGBANG", "BoA", "SHINee", "Girls Generation", "EXO", "BLACKPINK"]
            : ["Daft Punk", "Taylor Swift", "The Beatles", "ABBA", "Bob Marley", "A. R. Rahman", "BTS", "Utada Hikaru"];

    public IReadOnlyList<ProviderDiagnosticSample> Rotate(string key, IReadOnlyList<ProviderDiagnosticSample> curated)
    {
        if (curated.Count == 0) return [];
        lock (sync)
        {
            var offset = state.TryGetValue<int>("rotation:" + key, out var current) ? current : 0;
            state.Set("rotation:" + key, (offset + 1) % curated.Count, Options());
            var ordered = curated.Skip(offset).Concat(curated.Take(offset));
            return (state.TryGetValue<ProviderDiagnosticSample>("working:" + key, out var working) && working != null
                ? new[] { working }.Concat(ordered) : ordered).DistinctBy(Identity).ToArray();
        }
    }

    public void Remember(string key, ProviderDiagnosticSample sample) => state.Set("working:" + key, sample, Options());

    internal ProviderDiagnosticSample? LastWorking(string key) => state.Get<ProviderDiagnosticSample>("working:" + key);

    public async Task<ProviderSampleCheck> CheckAsync(string key, IReadOnlyList<ProviderDiagnosticSample> curated,
        Func<ProviderDiagnosticSample, CancellationToken, Task<ProviderSampleCheck>> check,
        Func<CancellationToken, IAsyncEnumerable<ProviderDiagnosticSample>>? discover = null,
        CancellationToken cancellationToken = default, string timeoutReason = "timeout", TimeSpan? attemptTimeout = null)
    {
        using var timer = new CancellationTokenSource(timeout ?? Timeout, timeProvider ?? TimeProvider.System);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timer.Token);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var failures = 0;
        ProviderSampleCheck? failure = null;
        async Task<ProviderSampleCheck?> TryAsync(ProviderDiagnosticSample sample)
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (seen.Count >= MaximumAttempts || !seen.Add(Identity(sample))) return null;
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            attempt.CancelAfter(attemptTimeout ?? TimeSpan.FromSeconds(5));
            ProviderSampleCheck result;
            try { result = await check(sample, attempt.Token).WaitAsync(attempt.Token); }
            catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { result = new(false, timeoutReason); }
            catch (OperationCanceledException) when (timer.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                failure ??= new(false, timeoutReason);
                failures++;
                throw;
            }
            catch (HttpRequestException) { result = new(false, "unreachable"); }
            catch (IOException) { result = new(false, "unreachable"); }
            catch (System.Text.Json.JsonException) { result = new(false, "provider-contract-changed"); }
            if (result.Stop) return result;
            if (result.Success)
            {
                Remember(key, sample);
                return result;
            }
            if (!result.SampleUnavailable)
            {
                failure = result;
                if (++failures >= SourceFailureThreshold) return result;
            }
            return null;
        }
        try
        {
            foreach (var sample in Rotate(key, curated))
                if (await TryAsync(sample) is { } result) return result;
            if (discover != null && seen.Count < MaximumAttempts)
            {
                var discovered = discover(deadline.Token).GetAsyncEnumerator(deadline.Token);
                Task<bool>? advancing = null;
                try
                {
                    for (var i = 0; i < MaximumAttempts && seen.Count < MaximumAttempts; i++)
                    {
                        advancing = discovered.MoveNextAsync().AsTask();
                        if (!await advancing.WaitAsync(deadline.Token)) break;
                        if (await TryAsync(discovered.Current) is { } result) return result;
                    }
                }
                finally { _ = DisposeDiscoveryAsync(discovered, advancing); }
            }
            return failures >= SourceFailureThreshold ? failure!.Value : ProviderSampleCheck.Missing;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timer.IsCancellationRequested)
        {
            return failures >= SourceFailureThreshold ? failure!.Value :
                failures > 0 ? new(false, failure?.ReasonCode ?? timeoutReason, SampleUnavailable: true) : ProviderSampleCheck.Missing;
        }
        catch (HttpRequestException) { return ProviderSampleCheck.Missing; }
        catch (IOException) { return ProviderSampleCheck.Missing; }
        catch (System.Text.Json.JsonException) { return ProviderSampleCheck.Missing; }
    }

    private static async Task DisposeDiscoveryAsync(IAsyncEnumerator<ProviderDiagnosticSample> discovered, Task<bool>? advancing)
    {
        if (advancing != null)
            try { await advancing; } catch { }
        try { await discovered.DisposeAsync(); } catch { }
    }

    public static ProviderSampleCheck FromError(ProviderError error) =>
        error.Kind == ProviderErrorKind.NotFound || error.Kind == ProviderErrorKind.IncompatibleMedia && error.Code == "incompatible-media"
            ? ProviderSampleCheck.Missing : new(false, error.Code);

    public static ProviderExecutionContext WithCancellation(ProviderExecutionContext context, CancellationToken token) =>
        new(context.Actor, context.ProviderId, context.Account, context.Policy, context.OperationId, context.CorrelationId,
            context.Deadline, token, context.IdempotencyKey)
        { IsReadOnly = context.IsReadOnly };

    public static ProviderDiagnosticSample FromTrack(ProviderTrackMetadata track) =>
        new(track.Title, string.Join(", ", track.Artists.Select(item => item.Name)), track.Isrc,
            (int?)track.Duration?.TotalSeconds ?? 200, track.Id.Value);

    private static string Identity(ProviderDiagnosticSample sample) => sample.TrackId ?? sample.Query;
    private static MemoryCacheEntryOptions Options() => new() { Size = 1, SlidingExpiration = TimeSpan.FromHours(24) };
    public void Dispose() => state.Dispose();
}

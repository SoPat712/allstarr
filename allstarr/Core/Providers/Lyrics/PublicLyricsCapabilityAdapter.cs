using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Services.Common;

namespace allstarr.Core.Providers.Lyrics;

public abstract class PublicLyricsCapabilityAdapter(
    HttpClient http, IApplicationCache? cache, Func<TimeSpan>? lyricsTtl) : IProviderLyricsCapability
{
    private const int MaximumBytes = 2_000_000;
    protected virtual TimeProvider Clock => TimeProvider.System;
    protected virtual string CacheScope(ProviderLyricsRequest request) => string.Empty;
    protected virtual TimeSpan LookupBudget => TimeSpan.FromSeconds(3);
    protected virtual TimeSpan RequestTimeout => Timeout.InfiniteTimeSpan;
    protected virtual ProviderError LookupTimeoutError => new(ProviderErrorKind.TransientFailure);
    public abstract string ProviderId { get; }
    public ProviderCapabilityKind Capability => ProviderCapabilityKind.Lyrics;

    public async Task<ProviderOutcome<ProviderLyricsResult>> FetchLyricsAsync(
        ProviderExecutionContext context, ProviderLyricsRequest request)
    {
        if (context.ProviderId != ProviderId || !context.Policy.AllowsProvider(ProviderId) ||
            request.ProviderTrackId.ProviderId != ProviderId || request.ProviderTrackId.ResourceKind != ProviderResourceKind.Track)
            return Failure(ProviderErrorKind.Forbidden);
        if (context.CancellationToken.IsCancellationRequested) return Failure(ProviderErrorKind.Canceled);
        if (context.IsExpired(DateTimeOffset.UtcNow)) return Failure(ProviderErrorKind.CapabilityUnavailable);
        using var deadlineTimer = new CancellationTokenSource(context.Remaining(DateTimeOffset.UtcNow), Clock);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, deadlineTimer.Token);
        using var budgetTimer = new CancellationTokenSource(LookupBudget, Clock);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, budgetTimer.Token);
        var token = budget.Token;
        try
        {
            var key = $"lyrics:v3:{ProviderId}:" + CacheScope(request) + Hash(JsonSerializer.Serialize(new
            {
                request.Isrc,
                request.TrackTitle,
                request.ArtistNames,
                request.AlbumTitle,
                request.DurationSeconds
            }));
            try
            {
                var cached = cache == null ? null : await cache.GetAsync<ProviderLyricsResult>(key).WaitAsync(token);
                token.ThrowIfCancellationRequested();
                if (cached != null) return Project(cached, request);
            }
            catch (Exception exception) when (exception is not OperationCanceledException) { }
            var result = await ReadAsync(context, request, token);
            token.ThrowIfCancellationRequested();
            if (!result.IsSuccess) return result;
            try
            {
                if (cache != null)
                    await cache.SetAsync(key, result.RequireValue(), result.RequireValue().Content == null
                        ? TimeSpan.FromMinutes(5) : lyricsTtl?.Invoke() ?? TimeSpan.FromDays(7)).WaitAsync(token);
            }
            catch (Exception exception) when (exception is not OperationCanceledException) { }
            token.ThrowIfCancellationRequested();
            return Project(result.RequireValue(), request);
        }
        catch (OperationCanceledException)
        {
            return deadline.IsCancellationRequested ? Failure(ProviderErrorKind.Canceled) :
                ProviderOutcome<ProviderLyricsResult>.Failure(LookupTimeoutError);
        }
        catch (Exception exception) when (exception is JsonException or System.Xml.XmlException or ArgumentException or OverflowException)
        { return ContractFailure(); }
        catch { return Failure(ProviderErrorKind.TransientFailure); }
    }

    protected abstract Task<ProviderOutcome<ProviderLyricsResult>> ReadAsync(ProviderLyricsRequest request, CancellationToken token);
    protected virtual Task<ProviderOutcome<ProviderLyricsResult>> ReadAsync(
        ProviderExecutionContext context, ProviderLyricsRequest request, CancellationToken token) => ReadAsync(request, token);
    protected virtual Task BeforeRequestAsync(CancellationToken token) => Task.CompletedTask;

    protected async Task<(string? Content, ProviderError? Error)> GetAsync(Uri endpoint, CancellationToken token)
    {
        var response = await GetResponseAsync(endpoint, token);
        return (response.Content, response.Error);
    }

    protected async Task<(string? Content, ProviderError? Error, HttpStatusCode? StatusCode)> GetResponseAsync(
        Uri endpoint, CancellationToken token)
    {
        await BeforeRequestAsync(token);
        using var timeout = new CancellationTokenSource(RequestTimeout, Clock);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        using var outbound = new HttpRequestMessage(HttpMethod.Get, endpoint);
        outbound.Headers.Accept.ParseAdd("application/ttml+xml, application/json, text/plain");
        outbound.Headers.UserAgent.ParseAdd(AppIdentity.UserAgent);
        try
        {
            using var response = await http.SendAsync(outbound, HttpCompletionOption.ResponseHeadersRead, attempt.Token);
            if (response.RequestMessage?.RequestUri is { } actual && actual != endpoint)
                return (null, new(ProviderErrorKind.PermanentFailure), response.StatusCode);
            if (response.StatusCode == HttpStatusCode.NotFound) return (null, null, response.StatusCode);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var delay = response.Headers.RetryAfter?.Delta ??
                    (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(30);
                return (null, new(ProviderErrorKind.RateLimited, TimeSpan.FromSeconds(Math.Clamp(delay.TotalSeconds, 0, 300))), response.StatusCode);
            }
            if (!response.IsSuccessStatusCode || response.Headers.Location != null)
                return (null, new((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout
                    ? ProviderErrorKind.TransientFailure : ProviderErrorKind.PermanentFailure), response.StatusCode);
            if (response.Content.Headers.ContentLength > MaximumBytes) return (null, ProviderError.CompatibilityContractChanged(), response.StatusCode);
            try { await response.Content.LoadIntoBufferAsync(MaximumBytes, attempt.Token); }
            catch (HttpRequestException) { return (null, ProviderError.CompatibilityContractChanged(), response.StatusCode); }
            return (await response.Content.ReadAsStringAsync(attempt.Token), null, response.StatusCode);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException ||
            exception is OperationCanceledException && !token.IsCancellationRequested)
        {
            return (null, new(ProviderErrorKind.TransientFailure), null);
        }
    }

    protected ProviderOutcome<ProviderLyricsResult> ParseTtml(string xml)
    {
        try { return TtmlLyricsParser.Parse(xml, ProviderId); }
        catch (Exception exception) when (exception is System.Xml.XmlException or ArgumentException or OverflowException)
        { return ContractFailure(); }
    }

    protected ProviderOutcome<ProviderLyricsResult> Unavailable() =>
        ProviderOutcome<ProviderLyricsResult>.Success(new(ProviderLyricsAvailabilityState.Unavailable, ProviderId));
    protected static ProviderOutcome<ProviderLyricsResult> Failure(ProviderErrorKind kind) =>
        ProviderOutcome<ProviderLyricsResult>.Failure(new(kind));
    protected static ProviderOutcome<ProviderLyricsResult> ContractFailure() =>
        ProviderOutcome<ProviderLyricsResult>.Failure(ProviderError.CompatibilityContractChanged());
    protected static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    protected static string MetadataQuery(ProviderLyricsRequest request, string titleKey) =>
        $"{titleKey}={Uri.EscapeDataString(request.TrackTitle!)}&artist={Uri.EscapeDataString(string.Join(", ", request.ArtistNames))}" +
        (string.IsNullOrWhiteSpace(request.AlbumTitle) ? "" : $"&album={Uri.EscapeDataString(request.AlbumTitle)}") +
        (request.DurationSeconds is > 0 ? $"&duration={request.DurationSeconds.Value.ToString(CultureInfo.InvariantCulture)}" : "");

    private static ProviderOutcome<ProviderLyricsResult> Project(ProviderLyricsResult result, ProviderLyricsRequest request)
    {
        if (result.Availability != ProviderLyricsAvailabilityState.Available)
            return ProviderOutcome<ProviderLyricsResult>.Success(result);
        if (request.AvailabilityOnly)
            return ProviderOutcome<ProviderLyricsResult>.Success(new(result.Availability, result.Source, revision: result.Revision));
        if (request.PreferredFormat == ProviderLyricsFormat.PlainText && result.Format != ProviderLyricsFormat.PlainText)
        {
            var text = result.TimedLines == null
                ? string.Join('\n', allstarr.Core.Music.MusicLyrics.ParseSyncedLines(result.Content).Select(line => line.Text))
                : string.Join('\n', result.TimedLines.Select(line => line.Text));
            return ProviderOutcome<ProviderLyricsResult>.Success(new(result.Availability, result.Source,
                ProviderLyricsFormat.PlainText, text, result.Revision));
        }
        return ProviderOutcome<ProviderLyricsResult>.Success(result);
    }
}

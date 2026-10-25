using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Lyrics;
using allstarr.Core.Providers.Spotify;
using allstarr.Core.Storage;
using allstarr.Core.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using allstarr.Services.Common;

namespace allstarr.Tests;

public sealed class SpicyLyricsCapabilityAdapterTests
{
    [Fact]
    public async Task SpicyBrokenCacheStillUsesAnAuthorizedFreshResponse()
    {
        using var handler = new SpicyLyricsTestHandler(SpicyLyricsTestData.Payload("static"));
        using var http = new HttpClient(handler);
        var cache = new SpicyLyricsTestCache
        { BeforeGet = () => throw new IOException("Fixture cache read interruption"), BeforeSet = () => throw new IOException("Fixture cache write interruption") };
        var secrets = new SpicyLyricsTestSecrets();
        var result = await new SpicyLyricsCapabilityAdapter(http, secrets, cache)
            .FetchLyricsAsync(SpicyLyricsTestData.Context(), SpicyLyricsTestData.Request());
        Assert.Equal(ProviderLyricsAvailabilityState.Available, result.RequireValue().Availability);
        Assert.Equal(1, handler.Calls);
        Assert.True(secrets.Calls >= 3);
        Assert.Empty(cache.Values);
    }

    [Theory]
    [InlineData(3, ProviderErrorKind.CapabilityUnavailable)]
    [InlineData(60, ProviderErrorKind.TransientFailure)]
    public async Task SpicyHttpRespectsContextDeadlineAndSixSecondCap(int seconds, ProviderErrorKind expected)
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new SpicyLyricsTestHandler(SpicyLyricsTestData.Payload("static"))
        { BeforeResponse = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); } };
        using var http = new HttpClient(handler);
        var source = new SpicyLyricsCapabilityAdapter(http, new SpicyLyricsTestSecrets(), clock: clock);
        var pending = source.FetchLyricsAsync(SpicyLyricsTestData.Context(deadline: clock.GetUtcNow().AddSeconds(seconds)), SpicyLyricsTestData.Request());
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            clock.Advance(TimeSpan.FromSeconds(Math.Min(seconds, 6)));
            Assert.Equal(expected, (await pending.WaitAsync(TimeSpan.FromSeconds(10))).Error!.Kind);
        }
        finally { release.TrySetResult(); await pending.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Theory]
    [InlineData("syllable", ProviderLyricsFormat.WordTimed, "Hello world")]
    [InlineData("line", ProviderLyricsFormat.LineTimed, "Start")]
    [InlineData("static", ProviderLyricsFormat.PlainText, "First plain line")]
    public async Task SpicyOfficialFormatsProduceTheirActualTiming(string fixture, ProviderLyricsFormat format, string text)
    {
        using var handler = new SpicyLyricsTestHandler(SpicyLyricsTestData.Payload(fixture));
        using var http = new HttpClient(handler);
        var adapter = new SpicyLyricsCapabilityAdapter(http, new SpicyLyricsTestSecrets());
        var result = await adapter.FetchLyricsAsync(SpicyLyricsTestData.Context(), SpicyLyricsTestData.Request());
        Assert.True(result.IsSuccess);
        var value = result.RequireValue();
        Assert.Equal(ProviderLyricsAvailabilityState.Available, value.Availability);
        Assert.Equal(format, value.Format);
        Assert.Contains(text, value.TimedLines?.First().Text ?? value.Content);
        Assert.Equal("spicylyrics", value.Source);
        Assert.Equal(1, handler.Calls);
        if (fixture == "syllable")
        {
            Assert.Equal(2, value.TimedLines!.Count);
            Assert.Equal(3, value.TimedLines[0].Segments!.Count);
            Assert.Equal(TimeSpan.FromSeconds(1.2), value.TimedLines[0].Segments![0].Start + value.TimedLines[0].Segments![0].Duration);
            Assert.All(value.TimedLines[1].Segments!, segment => Assert.True(segment.IsBackground));
            Assert.Contains("<00:02.300>", value.Content);
        }
        if (fixture == "line")
        {
            Assert.Equal(TimeSpan.Zero, value.TimedLines![0].Start);
            Assert.Null(value.TimedLines[1].Duration);
            Assert.Null(value.TimedLines[0].Segments);
        }
        if (fixture == "static") Assert.Null(value.TimedLines);
    }

    [Fact]
    public async Task SpicyRequestUsesOnlyTheOperatorKeyAtTheFixedOrigin()
    {
        using var handler = new SpicyLyricsTestHandler(SpicyLyricsTestData.Payload("static"));
        using var http = new HttpClient(handler);
        await new SpicyLyricsCapabilityAdapter(http, new SpicyLyricsTestSecrets())
            .FetchLyricsAsync(SpicyLyricsTestData.Context(), SpicyLyricsTestData.Request());
        Assert.Equal(1, handler.Calls);
        Assert.Equal("https://api.spicylyrics.org/v1/lyrics/" + SpicyLyricsTestData.TrackId, handler.Uri);
        Assert.Equal("Bearer sl_sk_fixture_key", handler.Authorization);
        Assert.Equal("GET", handler.Method);
        Assert.False(handler.HadOriginOrCookie);
    }

    [Theory]
    [InlineData("syllable", "group", ProviderLyricsFormat.PlainText)]
    [InlineData("syllable", "segment", ProviderLyricsFormat.LineTimed)]
    [InlineData("line", "group", ProviderLyricsFormat.PlainText)]
    public async Task SpicyMissingStartsPreserveAllTextWithoutInventingTimes(string fixture, string missing, ProviderLyricsFormat expected)
    {
        var payload = JsonNode.Parse(SpicyLyricsTestData.Payload(fixture))!;
        if (fixture == "line") payload["Body"]!["Content"]![0]!.AsObject().Remove("StartTime");
        else if (missing == "group") payload["Body"]!["Content"]![0]!["Lead"]!.AsObject().Remove("StartTime");
        else payload["Body"]!["Content"]![0]!["Lead"]!["Syllables"]![0]!.AsObject().Remove("StartTime");
        using var handler = new SpicyLyricsTestHandler(payload.ToJsonString());
        using var http = new HttpClient(handler);
        var value = (await new SpicyLyricsCapabilityAdapter(http, new SpicyLyricsTestSecrets())
            .FetchLyricsAsync(SpicyLyricsTestData.Context(), SpicyLyricsTestData.Request())).RequireValue();
        Assert.Equal(expected, value.Format);
        if (expected == ProviderLyricsFormat.PlainText) Assert.Null(value.TimedLines);
        else Assert.All(value.TimedLines!, line => Assert.Null(line.Segments));
        var text = value.TimedLines == null ? value.Content : string.Join('\n', value.TimedLines.Select(line => line.Text));
        Assert.Contains(fixture == "line" ? "Finish" : "Hello world", text);
    }

    [Theory]
    [InlineData(401, ProviderErrorKind.AccountNeedsReauthentication)]
    [InlineData(403, ProviderErrorKind.AccountNeedsConfiguration)]
    [InlineData(429, ProviderErrorKind.RateLimited)]
    [InlineData(500, ProviderErrorKind.TransientFailure)]
    [InlineData(502, ProviderErrorKind.TransientFailure)]
    [InlineData(503, ProviderErrorKind.TransientFailure)]
    public async Task SpicyErrorsUseHostTypedCodesAndNeverCacheBodies(int status, ProviderErrorKind expected)
    {
        using var handler = new SpicyLyricsTestHandler("raw private upstream body") { Status = (HttpStatusCode)status };
        using var http = new HttpClient(handler);
        var cache = new SpicyLyricsTestCache();
        var result = await new SpicyLyricsCapabilityAdapter(http, new SpicyLyricsTestSecrets(), cache)
            .FetchLyricsAsync(SpicyLyricsTestData.Context(), SpicyLyricsTestData.Request());
        Assert.Equal(expected, result.Error!.Kind);
        Assert.DoesNotContain("private", result.Error.SafeMessage);
        Assert.Empty(cache.Values);
        if (status == 429) Assert.InRange(result.Error.RetryAfter!.Value, TimeSpan.Zero, TimeSpan.FromMinutes(5));
    }

    [Theory]
    [InlineData("wrong-id")]
    [InlineData("negative")]
    [InlineData("end-before-start")]
    [InlineData("unknown-type")]
    [InlineData("wrong-envelope")]
    [InlineData("status-string")]
    [InlineData("status-null")]
    [InlineData("status-object")]
    [InlineData("wrong-background")]
    [InlineData("oversize")]
    public async Task SpicyInvalidContractsNeverProduceContent(string kind)
    {
        var payload = JsonNode.Parse(SpicyLyricsTestData.Payload("syllable"))!;
        if (kind == "wrong-id") payload["Body"]!["id"] = SpicyLyricsTestData.TrackId.ToLowerInvariant();
        if (kind == "negative") payload["Body"]!["Content"]![0]!["Lead"]!["StartTime"] = -1;
        if (kind == "end-before-start") payload["Body"]!["Content"]![0]!["Lead"]!["Syllables"]![0]!["EndTime"] = 0.5;
        if (kind == "unknown-type") payload["Body"]!["Type"] = "Future";
        if (kind == "wrong-envelope") payload["Status"] = 404;
        if (kind == "status-string") payload["Status"] = "200";
        if (kind == "status-null") payload["Status"] = null;
        if (kind == "status-object") payload["Status"] = new JsonObject();
        if (kind == "wrong-background") payload["Body"]!["Content"]![0]!["Background"] = new JsonObject();
        using var handler = new SpicyLyricsTestHandler(kind == "oversize" ? new string('x', 2_000_001) : payload.ToJsonString());
        using var http = new HttpClient(handler);
        var result = await new SpicyLyricsCapabilityAdapter(http, new SpicyLyricsTestSecrets())
            .FetchLyricsAsync(SpicyLyricsTestData.Context(), SpicyLyricsTestData.Request());
        Assert.False(result.IsSuccess);
        Assert.Equal("provider-contract-changed", result.Error!.Code);
    }

    [Theory]
    [InlineData("https://evil.test/uid/1001")]
    [InlineData("https://spicylyrics.org/uid/1001?token=fixture")]
    [InlineData("https://spicylyrics.org/uid/1001#fragment")]
    [InlineData("https://user@spicylyrics.org/uid/1001")]
    [InlineData("https://spicylyrics.org:444/uid/1001")]
    [InlineData("https://spicylyrics.org/uid/9999")]
    [InlineData("javascript:alert(1)")]
    public async Task SpicyUnsafeProfilesKeepTheNameWithoutCreatingMaker(string url)
    {
        var payload = JsonNode.Parse(SpicyLyricsTestData.Payload("static"))!;
        payload["Body"]!["UploadAttribution"]!["Uploader"]!["url"] = url;
        payload["Body"]!["UploadAttribution"]!.AsObject().Remove("Maker");
        using var handler = new SpicyLyricsTestHandler(payload.ToJsonString());
        using var http = new HttpClient(handler);
        var result = (await new SpicyLyricsCapabilityAdapter(http, new SpicyLyricsTestSecrets())
            .FetchLyricsAsync(SpicyLyricsTestData.Context(), SpicyLyricsTestData.Request())).RequireValue();
        Assert.Equal("Fixture uploader", result.Attribution!.Uploader!.Username);
        Assert.Null(result.Attribution.Uploader.Url);
        Assert.Null(result.Attribution.Maker);
    }

    [Theory]
    [InlineData("apple_music")]
    [InlineData("spotify")]
    [InlineData("unknown")]
    [InlineData("future")]
    public async Task SpicyActualCatalogSurvivesServiceStampAndDoesNotInventCommunity(string catalog)
    {
        var payload = JsonNode.Parse(SpicyLyricsTestData.Payload("line"))!;
        payload["Body"]!["source"] = catalog;
        payload["Body"]!["source_via"] = "lrc-red";
        using var handler = new SpicyLyricsTestHandler(payload.ToJsonString());
        using var http = new HttpClient(handler);
        var source = new SpicyLyricsCapabilityAdapter(http, new SpicyLyricsTestSecrets(), new SpicyLyricsTestCache());
        var context = SpicyLyricsTestData.Context();
        var value = (await source.FetchLyricsAsync(context, SpicyLyricsTestData.Request())).RequireValue();
        var available = (await source.FetchLyricsAsync(context, SpicyLyricsTestData.Request(availabilityOnly: true))).RequireValue();
        var plain = (await source.FetchLyricsAsync(context, SpicyLyricsTestData.Request(preferred: ProviderLyricsFormat.PlainText))).RequireValue();
        Assert.Equal("spicylyrics", value.Source);
        Assert.Equal(catalog == "future" ? "unknown" : catalog, value.Attribution!.Catalog);
        Assert.Null(value.Attribution.Uploader);
        Assert.Equal(value.Attribution, plain.Attribution);
        Assert.Equal(value.Attribution, available.Attribution);
        Assert.Null(available.Content);
        Assert.Null(available.TimedLines);
        Assert.Equal(ProviderLyricsFormat.PlainText, plain.Format);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task SpicyCacheAuthorizesEveryHitAndKeepsStablePrivateBindings()
    {
        using var handler = new SpicyLyricsTestHandler(SpicyLyricsTestData.Payload("static"));
        using var http = new HttpClient(handler);
        var secrets = new SpicyLyricsTestSecrets();
        var cache = new SpicyLyricsTestCache();
        var source = new SpicyLyricsCapabilityAdapter(http, secrets, cache);
        var first = (await source.FetchLyricsAsync(SpicyLyricsTestData.Context(), SpicyLyricsTestData.Request())).RequireValue();
        var context = SpicyLyricsTestData.Context(correlation: "second-operation");
        var execution = new LyricsExecution(LyricsCompletionMode.OwnedAutomatic, await ((IInternalLyricsCapability)source).CaptureSourceAsync(default));
        var repeated = (await ((IInternalLyricsCapability)source).FetchAsync(context, SpicyLyricsTestData.Request(), execution)).RequireValue();
        Assert.Equal(first.Revision, repeated.Revision);
        Assert.Equal(1, handler.Calls);
        Assert.Single(cache.Values);
        Assert.Equal(1, cache.Writes);
        Assert.All(cache.Values.Keys, key => Assert.DoesNotContain(SpicyLyricsTestData.TrackId, key));
        await source.FetchLyricsAsync(SpicyLyricsTestData.Context(actor: Guid.NewGuid()), SpicyLyricsTestData.Request());
        await source.FetchLyricsAsync(SpicyLyricsTestData.Context(revision: 2), SpicyLyricsTestData.Request());
        await source.FetchLyricsAsync(SpicyLyricsTestData.Context(account: Guid.NewGuid()), SpicyLyricsTestData.Request());
        Assert.Equal(4, handler.Calls);
        var reads = cache.Reads;
        secrets.Authorized = false;
        Assert.Equal(ProviderErrorKind.AccountNeedsConfiguration, (await source.FetchLyricsAsync(context, SpicyLyricsTestData.Request())).Error!.Kind);
        Assert.Equal(reads, cache.Reads);
    }

    [Theory]
    [InlineData("get")]
    [InlineData("http")]
    [InlineData("set")]
    public async Task SpicyRevocationAcrossHeldOperationsPreventsEmission(string phase)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secrets = new SpicyLyricsTestSecrets();
        var cache = new SpicyLyricsTestCache();
        using var handler = new SpicyLyricsTestHandler(SpicyLyricsTestData.Payload("static"));
        using var http = new HttpClient(handler);
        var source = new SpicyLyricsCapabilityAdapter(http, secrets, cache);
        if (phase == "get") await source.FetchLyricsAsync(SpicyLyricsTestData.Context(), SpicyLyricsTestData.Request());
        Func<Task> hold = async () => { started.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(10)); };
        if (phase == "get") cache.BeforeGet = hold;
        else if (phase == "set") cache.BeforeSet = hold;
        else handler.BeforeResponse = _ => hold();
        var pending = source.FetchLyricsAsync(SpicyLyricsTestData.Context(), SpicyLyricsTestData.Request());
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            secrets.Authorized = false;
            release.TrySetResult();
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(result.IsSuccess);
            Assert.Equal(ProviderErrorKind.AccountNeedsConfiguration, result.Error!.Kind);
            if (phase == "set") Assert.Empty(cache.Values);
        }
        finally { release.TrySetResult(); await pending.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact]
    public async Task SpicyAbsoluteExpiryAndNearExpiryCannotBeReaged()
    {
        var clock = new SpicyLyricsTestClock();
        var cache = new SpicyLyricsTestCache();
        using var handler = new SpicyLyricsTestHandler(SpicyLyricsTestData.Payload("static"));
        using var http = new HttpClient(handler);
        var source = new SpicyLyricsCapabilityAdapter(http, new SpicyLyricsTestSecrets(), cache, () => TimeSpan.FromDays(365), clock);
        ProviderExecutionContext Context() => SpicyLyricsTestData.Context(deadline: clock.UtcNow.AddMinutes(1));
        await source.FetchLyricsAsync(Context(), SpicyLyricsTestData.Request());
        var original = Assert.Single(cache.Values).Value;
        var stamp = JsonNode.Parse(original)!;
        Assert.Equal(clock.UtcNow.AddDays(30), stamp["ExpiresAtUtc"]!.GetValue<DateTimeOffset>());
        clock.Advance(TimeSpan.FromDays(29));
        await source.FetchLyricsAsync(Context(), SpicyLyricsTestData.Request());
        Assert.Equal(original, Assert.Single(cache.Values).Value);
        Assert.Equal(1, cache.Writes);
        clock.Advance(TimeSpan.FromDays(1) - TimeSpan.FromSeconds(10));
        await source.FetchLyricsAsync(Context(), SpicyLyricsTestData.Request());
        Assert.Equal(2, handler.Calls);
        clock.Advance(TimeSpan.FromDays(31));
        await source.FetchLyricsAsync(Context(), SpicyLyricsTestData.Request());
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task SpicyFileCacheReplaysTimingAndCreditsAfterAdapterRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "allstarr-spicy-cache-" + Guid.NewGuid().ToString("N"));
        var clock = new SpicyLyricsTestClock();
        var options = new FileMediaCacheOptions(root);
        using var handler = new SpicyLyricsTestHandler(SpicyLyricsTestData.Payload("syllable"));
        using var http = new HttpClient(handler);
        try
        {
            using var cache = new FileMediaApplicationCache(options, clock, NullLogger<FileMediaApplicationCache>.Instance);
            var source = new SpicyLyricsCapabilityAdapter(http, new SpicyLyricsTestSecrets(), cache, clock: clock);
            var context = SpicyLyricsTestData.Context(deadline: clock.UtcNow.AddMinutes(1));
            var first = (await source.FetchLyricsAsync(context, SpicyLyricsTestData.Request())).RequireValue();
            using var restarted = new FileMediaApplicationCache(options, clock, NullLogger<FileMediaApplicationCache>.Instance);
            var replay = new SpicyLyricsCapabilityAdapter(http, new SpicyLyricsTestSecrets(), restarted, clock: clock);
            var second = (await replay.FetchLyricsAsync(context, SpicyLyricsTestData.Request())).RequireValue();
            Assert.Equal(1, handler.Calls);
            Assert.Equal(first.Revision, second.Revision);
            Assert.Equal(first.Attribution, second.Attribution);
            Assert.Equal(TimeSpan.FromSeconds(0.2), second.TimedLines![0].Segments![0].Duration);
            Assert.Null(second.TimedLines[0].Segments![0].Synthetic);
            Assert.NotEmpty(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SpicyMissingAccountKeyOrSelectorDoesNotSend()
    {
        using var handler = new SpicyLyricsTestHandler(SpicyLyricsTestData.Payload("static"));
        using var http = new HttpClient(handler);
        var secrets = new SpicyLyricsTestSecrets();
        var source = new SpicyLyricsCapabilityAdapter(http, secrets);
        Assert.Equal(ProviderLyricsAvailabilityState.Unavailable, (await source.FetchLyricsAsync(SpicyLyricsTestData.Context(), SpicyLyricsTestData.Request("metadata-hash"))).RequireValue().Availability);
        secrets.Value = "";
        Assert.Equal(ProviderErrorKind.AccountNeedsConfiguration, (await source.FetchLyricsAsync(SpicyLyricsTestData.Context(), SpicyLyricsTestData.Request())).Error!.Kind);
        var actor = new ProviderActorContext(ProviderActorKind.PublicRead, null);
        var context = new ProviderExecutionContext(actor, "spicylyrics", null, SpicyLyricsTestData.Context().Policy,
            "lyrics", "fixture", DateTimeOffset.UtcNow.AddMinutes(1), default);
        Assert.Equal(ProviderErrorKind.Forbidden, (await source.FetchLyricsAsync(context, SpicyLyricsTestData.Request())).Error!.Kind);
        Assert.Equal(0, handler.Calls);
    }
}

internal static class SpicyLyricsTestData
{
    internal const string TrackId = "AbCdEfGhIjKlMnOpQrStUv";
    internal static readonly Guid UserId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    internal static readonly Guid AccountId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    internal static readonly Guid SecretId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    internal static string Payload(string format) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Lyrics", "spicylyrics-" + format + ".json"));
    internal static ProviderLyricsRequest Request(string id = TrackId, bool availabilityOnly = false, ProviderLyricsFormat? preferred = null) =>
        new(Guid.NewGuid(), new("spicylyrics", ProviderResourceKind.Track, id), availabilityOnly, preferred);
    internal static ProviderExecutionContext Context(long revision = 1, Guid? actor = null, Guid? account = null,
        CancellationToken token = default, DateTimeOffset? deadline = null, string correlation = "fixture") => new(
        new(ProviderActorKind.User, actor ?? UserId, new("jellyfin", "backend", "principal")), "spicylyrics",
        new(account ?? AccountId, "spicylyrics", ProviderAccountScope.Shared, revision, secretReferenceId: SecretId),
        new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true), ProviderExplicitContentPolicy.Allow,
            true, true, false, ["spicylyrics"]), "lyrics", correlation, deadline ?? DateTimeOffset.UtcNow.AddMinutes(1), token);
}

internal sealed class SpicyLyricsTestSecrets : IProviderAccountSecretAccessor
{
    internal bool Authorized { get; set; } = true;
    internal string Value { get; set; } = "sl_sk_fixture_key";
    internal int Calls { get; private set; }
    public Task<T> UseAsync<T>(ProviderAccountContext account, Func<ReadOnlyMemory<byte>, Task<T>> operation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Calls++;
        if (!Authorized) throw new UnauthorizedAccessException();
        return operation(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { apiKey = Value })));
    }
}

internal sealed class SpicyLyricsTestHandler(string payload) : HttpMessageHandler
{
    internal string Payload { get; set; } = payload;
    internal HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
    internal int Calls { get; private set; }
    internal string? Uri { get; private set; }
    internal string? Authorization { get; private set; }
    internal string? Method { get; private set; }
    internal bool HadOriginOrCookie { get; private set; }
    internal Func<CancellationToken, Task>? BeforeResponse { get; set; }
    internal bool MatchRequestedId { get; set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Calls++;
        Uri = request.RequestUri!.AbsoluteUri;
        Authorization = request.Headers.Authorization?.ToString();
        Method = request.Method.Method;
        HadOriginOrCookie = request.Headers.Contains("Origin") || request.Headers.Contains("Referer") || request.Headers.Contains("Cookie");
        if (BeforeResponse != null) await BeforeResponse(token);
        var body = Payload;
        if (MatchRequestedId)
        {
            var json = JsonNode.Parse(body)!;
            json["Body"]!["id"] = request.RequestUri.AbsolutePath.Split('/').Last();
            body = json.ToJsonString();
        }
        return new(Status) { Content = new StringContent(body, Encoding.UTF8, "application/json"), RequestMessage = request };
    }
}

internal sealed class SpicyLyricsTestClock : TimeProvider, IPlatformClock
{
    public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => UtcNow;
    internal void Advance(TimeSpan value) => UtcNow += value;
}

internal sealed class SpicyLyricsTestCache : IApplicationCache
{
    internal Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
    internal int Reads, Writes;
    internal TaskCompletionSource Written { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Func<Task>? BeforeGet, BeforeSet;
    public bool IsEnabled { get; set; } = true;
    public Task<string?> GetStringAsync(string key) => Task.FromResult(Values.GetValueOrDefault(key));
    public async Task<T?> GetAsync<T>(string key) where T : class
    {
        Reads++;
        if (BeforeGet != null) await BeforeGet();
        return Values.TryGetValue(key, out var value) ? JsonSerializer.Deserialize<T>(value) : null;
    }
    public Task<bool> SetStringAsync(string key, string value, TimeSpan? expiry = null)
    { Values[key] = value; Writes++; Written.TrySetResult(); return Task.FromResult(true); }
    public async Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiry = null) where T : class
    {
        if (BeforeSet != null) await BeforeSet();
        return await SetStringAsync(key, JsonSerializer.Serialize(value), expiry);
    }
    public Task<bool> DeleteAsync(string key) => Task.FromResult(Values.Remove(key));
    public Task<bool> ExistsAsync(string key) => Task.FromResult(Values.ContainsKey(key));
    public Task<int> DeleteByPatternAsync(string pattern) => Task.FromResult(0);
    public Task<int> PurgeAllAsync() { var count = Values.Count; Values.Clear(); return Task.FromResult(count); }
}

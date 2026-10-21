using System.Net;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Lyrics;
using allstarr.Core.Storage;
using Microsoft.Extensions.Logging;

namespace allstarr.Tests;

public sealed partial class MusixmatchLyricsCapabilityAdapterTests
{
    private const string TokenPayload = """{"message":{"header":{"status_code":200},"body":{"user_token":"automatic-fixture-token"}}}""";

    [Fact]
    public async Task AutomaticTokenIsReusedWithoutReadingAccountSecrets()
    {
        var handler = new FixtureHttpHandler { Payload = MissingMacroPayload };
        handler.Responses.Enqueue((HttpStatusCode.OK, TokenPayload));
        var secrets = new FixtureSecretAccessor();
        var adapter = CreateAdapter(handler, secrets);

        for (var attempt = 0; attempt < 2; attempt++)
            Assert.True((await adapter.FetchLyricsAsync(MissingAccountContext(), LyricsRequest())).IsSuccess);

        Assert.Empty(secrets.Accounts);
        Assert.Equal(3, handler.Requests);
        Assert.Equal("/ws/1.1/token.get", handler.RequestUris[0].AbsolutePath);
        Assert.Equal("mac-ios-v2.0", ParseQuery(handler.RequestUris[0])["app_id"]);
        Assert.False(ParseQuery(handler.RequestUris[0]).ContainsKey("usertoken"));
        Assert.All(handler.RequestUris.Skip(1), uri => Assert.Equal("automatic-fixture-token", ParseQuery(uri)["usertoken"]));
    }

    [Fact]
    public async Task AutomaticLookupCachesOnlyAvailableContent()
    {
        EnsureCacheSettings();
        var handler = new FixtureHttpHandler();
        handler.Responses.Enqueue((HttpStatusCode.OK, TokenPayload));
        var adapter = CreateAdapter(handler, new FixtureSecretAccessor());

        for (var attempt = 0; attempt < 2; attempt++)
            Assert.Equal("[00:01.234]First\n[00:02.500]Second",
                (await adapter.FetchLyricsAsync(MissingAccountContext(), LyricsRequest())).RequireValue().Content);

        Assert.Equal(2, handler.Requests);
    }

    [Theory]
    [InlineData(401, "", true)]
    [InlineData(200, "renew", false)]
    [InlineData(200, "captcha", false)]
    public async Task AutomaticTokenRefreshesOnceAfterAuthorizationFailure(int status, string hint, bool httpFailure)
    {
        var handler = new FixtureHttpHandler();
        handler.Responses.Enqueue((HttpStatusCode.OK, TokenPayload));
        handler.Responses.Enqueue((httpFailure ? HttpStatusCode.Unauthorized : HttpStatusCode.OK,
            JsonSerializer.Serialize(new { message = new { header = new { status_code = status, hint } } })));
        handler.Responses.Enqueue((HttpStatusCode.OK, TokenPayload.Replace("automatic-fixture-token", "refreshed-fixture-token", StringComparison.Ordinal)));
        handler.Responses.Enqueue((HttpStatusCode.OK, TimedMacroPayload));

        var result = await CreateAdapter(handler, new FixtureSecretAccessor())
            .FetchLyricsAsync(MissingAccountContext(), LyricsRequest());

        Assert.Equal(ProviderLyricsAvailabilityState.Available, result.RequireValue().Availability);
        Assert.Equal(4, handler.Requests);
        Assert.Equal("refreshed-fixture-token", ParseQuery(handler.RequestUris[3])["usertoken"]);
    }

    [Fact]
    public async Task RepeatedAutomaticAuthorizationFailureStopsAfterOneRefreshAndBacksOff()
    {
        var handler = new FixtureHttpHandler { StatusCode = HttpStatusCode.Unauthorized };
        handler.Responses.Enqueue((HttpStatusCode.OK, TokenPayload));
        handler.Responses.Enqueue((HttpStatusCode.Unauthorized, ""));
        handler.Responses.Enqueue((HttpStatusCode.OK, TokenPayload));
        var adapter = CreateAdapter(handler, new FixtureSecretAccessor());

        for (var attempt = 0; attempt < 2; attempt++)
            Assert.Equal(ProviderErrorKind.RateLimited,
                (await adapter.FetchLyricsAsync(MissingAccountContext(), LyricsRequest())).Error?.Kind);

        Assert.Equal(4, handler.Requests);
    }

    [Theory]
    [InlineData(401, "")]
    [InlineData(200, "renew")]
    [InlineData(200, "captcha")]
    public async Task RejectedAccountTokenNeverObtainsAnAutomaticToken(int status, string hint)
    {
        var handler = new FixtureHttpHandler
        {
            Payload = JsonSerializer.Serialize(new
            { message = new { header = new { status_code = status, hint } } })
        };
        var adapter = CreateAdapter(handler, new FixtureSecretAccessor("legacy-desktop-token"));

        var result = await adapter.FetchLyricsAsync(Context(), LyricsRequest());

        Assert.Equal(ProviderErrorKind.AccountNeedsReauthentication, result.Error?.Kind);
        Assert.Contains("Reconnect", result.Error?.SafeMessage);
        Assert.Equal("legacy-desktop-token", ParseQuery(Assert.Single(handler.RequestUris))["usertoken"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DebugInfoUsesMatchingHeadersAndNeverAppearsInLogs(bool transportFailure)
    {
        const string debug = "---- Debug Info ----\r\n[Device]: private-phone\r\n[UserToken]: private-debug-token\r\n[AppVersion]: 8.3.4\r\n[AppBuild]: 2026070900\r\n[DeviceId]: private-device-id\r\n[UserId]: private-user-id\r\n[RegId]: private-registration\r\n[Country]: private-country\r\n[Timestamp]: private-timestamp";
        var handler = new FixtureHttpHandler { ThrowRequestUriInException = transportFailure };
        var logs = new CapturedLogs();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var adapter = new MusixmatchLyricsCapabilityAdapter(new HttpClient(handler), new FixtureSecretAccessor(debug),
            new TestMemoryApplicationCache(), factory.CreateLogger<MusixmatchLyricsCapabilityAdapter>());

        var result = await adapter.FetchLyricsAsync(Context(), LyricsRequest());

        Assert.Equal(!transportFailure, result.IsSuccess);
        Assert.Equal("private-debug-token", ParseQuery(handler.RequestUri!)["usertoken"]);
        Assert.Equal("8.3.4", handler.AppVersion);
        Assert.Equal("Musixmatch/2026070900 CFNetwork/3860.300.31 Darwin/25.2.0", handler.UserAgent);
        var text = string.Join('\n', logs.Messages.Select(entry => entry.Message));
        foreach (var value in new[] { debug, "private-phone", "private-debug-token", "8.3.4", "2026070900", "private-device-id", "private-user-id", "private-registration", "private-country", "private-timestamp" })
        {
            Assert.DoesNotContain(value, text, StringComparison.Ordinal);
            Assert.DoesNotContain(Uri.EscapeDataString(value), text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task TokenFetchIsSharedAndAnIndividualCancellationDoesNotCancelOtherLookups()
    {
        var handler = new BlockingTokenHandler();
        var adapter = new MusixmatchLyricsCapabilityAdapter(new HttpClient(handler), new FixtureSecretAccessor(), new TestMemoryApplicationCache());
        using var canceled = new CancellationTokenSource();
        var first = adapter.FetchLyricsAsync(BuildContext(null, 1, null, ProviderAccountScope.Personal, null, canceled.Token), LyricsRequest());
        await handler.Started.Task;
        var others = Enumerable.Range(0, 12).Select(_ => adapter.FetchLyricsAsync(MissingAccountContext(), LyricsRequest())).ToArray();
        canceled.Cancel();
        Assert.Equal(ProviderErrorKind.Canceled, (await first).Error?.Kind);
        handler.Release.SetResult();

        Assert.All(await Task.WhenAll(others), result => Assert.True(result.IsSuccess));
        Assert.Equal(1, handler.TokenRequests);
        Assert.Equal(12, handler.LookupRequests);
    }

    [Fact]
    public async Task WarmAutomaticCacheDoesNotReplaceSelectedAccountCredentials()
    {
        EnsureCacheSettings();
        var handler = new FixtureHttpHandler();
        handler.Responses.Enqueue((HttpStatusCode.OK, TokenPayload));
        handler.Responses.Enqueue((HttpStatusCode.OK, TimedMacroPayload));
        handler.Responses.Enqueue((HttpStatusCode.Unauthorized, ""));
        var adapter = CreateAdapter(handler, new FixtureSecretAccessor("selected-account-token"));

        Assert.True((await adapter.FetchLyricsAsync(MissingAccountContext(), LyricsRequest())).IsSuccess);
        var selected = await adapter.FetchLyricsAsync(Context(), LyricsRequest());

        Assert.Equal(ProviderErrorKind.AccountNeedsReauthentication, selected.Error?.Kind);
        Assert.Equal(3, handler.Requests);
        Assert.Equal("selected-account-token", ParseQuery(handler.RequestUris[2])["usertoken"]);
    }

    [Theory]
    [InlineData("spotify:track:0123456789ABCDEFGHIJKL", true)]
    [InlineData("0123456789ABCDEFGHIJKL", false)]
    [InlineData("spotify:track:invalid", false)]
    public async Task MobileLookupIncludesOnlyAKnownSpotifyUri(string trackId, bool known)
    {
        var handler = new FixtureHttpHandler();
        await CreateAdapter(handler, new FixtureSecretAccessor())
            .FetchLyricsAsync(Context(), LyricsRequest(trackId: trackId));

        var query = ParseQuery(handler.RequestUri!);
        Assert.Equal(known, query.ContainsKey("track_spotify_id"));
        if (known) Assert.Equal(trackId, query["track_spotify_id"]);
    }

    private sealed class BlockingTokenHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int TokenRequests;
        public int LookupRequests;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var tokenRequest = request.RequestUri!.AbsolutePath.EndsWith("/token.get", StringComparison.Ordinal);
            if (tokenRequest)
            {
                Interlocked.Increment(ref TokenRequests);
                Started.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            else Interlocked.Increment(ref LookupRequests);
            return new(HttpStatusCode.OK) { Content = new StringContent(tokenRequest ? TokenPayload : MissingMacroPayload, Encoding.UTF8, "application/json") };
        }
    }
}

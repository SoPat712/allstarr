using System.Net;
using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Spotify;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace allstarr.Tests;

public sealed partial class SpotifyPlaylistCapabilityAdapterTests
{
    private const string LyricsTrackId = "0123456789ABCDEFGHIJKL";
    private static readonly Guid LyricsRecordingId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Fact]
    public async Task NativeLyrics_UsesAccountTokenAndExactWebPlayerProfileForTimedLines()
    {
        var handler = new LyricsHandler
        {
            LyricsPayload = "{\"lyrics\":{\"syncType\":\"LINE_SYNCED\",\"lines\":[" +
                "{\"startTimeMs\":\"1234\",\"words\":\"First\"}," +
                "{\"startTimeMs\":2500,\"words\":\"Second\"}]}}"
        };
        var adapter = new SpotifyPlaylistCapabilityAdapter(
            new HttpClient(handler), new FakeSecretAccessor("cookie"));

        var outcome = await new SpotifyLyricsCapabilityAdapter(adapter).FetchLyricsAsync(
            Context(), LyricsRequest(preferredFormat: ProviderLyricsFormat.LineTimed));

        Assert.True(outcome.IsSuccess, outcome.Error?.ToString());
        var result = outcome.RequireValue();
        Assert.Equal(ProviderLyricsAvailabilityState.Available, result.Availability);
        Assert.Equal(ProviderLyricsFormat.LineTimed, result.Format);
        Assert.Equal("[00:01.234]First\n[00:02.500]Second", result.Content);
        Assert.True(result.Revision?.StartsWith("sha256:", StringComparison.Ordinal) == true);
        Assert.Equal(1, handler.LyricsRequests);
        Assert.Equal("GET /color-lyrics/v2/track/0123456789ABCDEFGHIJKL?format=json&market=from_token", handler.LyricsRequestTarget);
        Assert.Equal("Bearer account-access-token", handler.LyricsAuthorization);
        Assert.Equal("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36", handler.LyricsUserAgent);
        Assert.Equal("WebPlayer", handler.LyricsAppPlatform);
        Assert.Equal("1.2.46.25.g7f189073", handler.LyricsAppVersion);
        Assert.Null(handler.LyricsCookie);
    }

    [Fact]
    public async Task NativeLyrics_PreservesPlainSourceWhenTimedFormatIsUnavailable()
    {
        var handler = new LyricsHandler
        {
            LyricsPayload = "{\"lyrics\":{\"syncType\":\"UNSYNCED\",\"lines\":[" +
                "{\"words\":\"Plain first\"},{\"words\":\"Plain second\"}]}}"
        };
        var adapter = new SpotifyPlaylistCapabilityAdapter(new HttpClient(handler), new FakeSecretAccessor("cookie"));

        var outcome = await new SpotifyLyricsCapabilityAdapter(adapter).FetchLyricsAsync(
            Context(), LyricsRequest(preferredFormat: ProviderLyricsFormat.LineTimed));

        Assert.True(outcome.IsSuccess, outcome.Error?.ToString());
        var result = outcome.RequireValue();
        Assert.Equal(ProviderLyricsAvailabilityState.Available, result.Availability);
        Assert.Equal(ProviderLyricsFormat.PlainText, result.Format);
        Assert.Equal("Plain first\nPlain second", result.Content);
    }

    [Fact]
    public async Task NativeLyrics_PlainPreferenceProjectsTimedSourceWithoutTimestamps()
    {
        var handler = new LyricsHandler
        {
            LyricsPayload = "{\"lyrics\":{\"syncType\":\"LINE_SYNCED\",\"lines\":[" +
                "{\"startTimeMs\":\"1234\",\"words\":\"First\"}," +
                "{\"startTimeMs\":2500,\"words\":\"Second\"}]}}"
        };
        var adapter = new SpotifyPlaylistCapabilityAdapter(new HttpClient(handler), new FakeSecretAccessor("cookie"));

        var outcome = await new SpotifyLyricsCapabilityAdapter(adapter).FetchLyricsAsync(
            Context(), LyricsRequest(preferredFormat: ProviderLyricsFormat.PlainText));

        Assert.True(outcome.IsSuccess, outcome.Error?.ToString());
        Assert.Equal(ProviderLyricsFormat.PlainText, outcome.RequireValue().Format);
        Assert.Equal("First\nSecond", outcome.RequireValue().Content);
    }

    [Fact]
    public async Task NativeLyrics_CacheSeparatesAccountAndRevisionWhileAvailabilityReadsReuseFullContent()
    {
        EnsureLyricsCacheSettings();
        var handler = new LyricsHandler
        {
            LyricsPayload = "{\"lyrics\":{\"syncType\":\"UNSYNCED\",\"lines\":[{\"words\":\"Cached lyrics\"}]}}"
        };
        var secrets = new FakeSecretAccessor("cookie");
        var cache = new TestMemoryApplicationCache();
        var adapter = new SpotifyPlaylistCapabilityAdapter(new HttpClient(handler), secrets, cache: cache);
        var accountA = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var accountB = Guid.Parse("55555555-5555-5555-5555-555555555555");

        var first = await new SpotifyLyricsCapabilityAdapter(adapter).FetchLyricsAsync(
            LyricsContext(accountA, revision: 1), LyricsRequest());
        var availability = await new SpotifyLyricsCapabilityAdapter(adapter).FetchLyricsAsync(
            LyricsContext(accountA, revision: 1), LyricsRequest(availabilityOnly: true));
        var changedRevision = await new SpotifyLyricsCapabilityAdapter(adapter).FetchLyricsAsync(
            LyricsContext(accountA, revision: 2), LyricsRequest());
        var otherAccount = await new SpotifyLyricsCapabilityAdapter(adapter).FetchLyricsAsync(
            LyricsContext(accountB, revision: 1), LyricsRequest());

        Assert.True(first.IsSuccess, first.Error?.ToString());
        Assert.True(availability.IsSuccess, availability.Error?.ToString());
        Assert.True(changedRevision.IsSuccess, changedRevision.Error?.ToString());
        Assert.True(otherAccount.IsSuccess, otherAccount.Error?.ToString());
        Assert.Equal("Cached lyrics", first.RequireValue().Content);
        Assert.Null(availability.RequireValue().Content);
        Assert.Equal("Cached lyrics", changedRevision.RequireValue().Content);
        Assert.Equal("Cached lyrics", otherAccount.RequireValue().Content);
        Assert.Equal(3, handler.LyricsRequests);
        var keys = cache.GetKeysByPattern("lyrics:*").ToArray();
        Assert.Contains(keys, key => key.Contains(accountA.ToString("N"), StringComparison.Ordinal));
        Assert.Contains(keys, key => key.Contains(accountB.ToString("N"), StringComparison.Ordinal));
        Assert.Contains(keys, key => key.Contains(":2:", StringComparison.Ordinal));
        Assert.DoesNotContain(keys, key => key.Contains("account-access-token", StringComparison.Ordinal));
        Assert.DoesNotContain(keys, key => key.Contains("cookie", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NativeLyrics_AvailabilityProbeThenContentUsesOneNetworkFetch()
    {
        EnsureLyricsCacheSettings();
        var handler = new LyricsHandler
        {
            LyricsPayload = "{\"lyrics\":{\"syncType\":\"UNSYNCED\",\"lines\":[{\"words\":\"Probe then content\"}]}}"
        };
        var adapter = new SpotifyPlaylistCapabilityAdapter(
            new HttpClient(handler), new FakeSecretAccessor("cookie"), cache: new TestMemoryApplicationCache());

        var availability = await new SpotifyLyricsCapabilityAdapter(adapter).FetchLyricsAsync(
            Context(), LyricsRequest(availabilityOnly: true));
        var content = await new SpotifyLyricsCapabilityAdapter(adapter).FetchLyricsAsync(
            Context(), LyricsRequest());

        Assert.True(availability.IsSuccess, availability.Error?.ToString());
        Assert.True(content.IsSuccess, content.Error?.ToString());
        Assert.Null(availability.RequireValue().Content);
        Assert.Equal("Probe then content", content.RequireValue().Content);
        Assert.Equal(1, handler.LyricsRequests);
    }

    [Fact]
    public async Task NativeLyrics_UnauthorizedResponseInvalidatesOnlyThatCachedToken()
    {
        var handler = new LyricsHandler { LyricsStatus = HttpStatusCode.Unauthorized };
        var secrets = new FakeSecretAccessor("cookie");
        var adapter = new SpotifyPlaylistCapabilityAdapter(new HttpClient(handler), secrets);

        var rejected = await new SpotifyLyricsCapabilityAdapter(adapter).FetchLyricsAsync(Context(), LyricsRequest());
        handler.LyricsStatus = HttpStatusCode.OK;
        handler.LyricsPayload = "{\"lyrics\":{\"syncType\":\"UNSYNCED\",\"lines\":[{\"words\":\"Reauthorized\"}]}}";
        var retried = await new SpotifyLyricsCapabilityAdapter(adapter).FetchLyricsAsync(Context(), LyricsRequest());

        Assert.False(rejected.IsSuccess);
        Assert.Equal(ProviderErrorKind.Unauthorized, rejected.Error!.Kind);
        Assert.True(retried.IsSuccess, retried.Error?.ToString());
        Assert.Equal("Reauthorized", retried.RequireValue().Content);
        Assert.Collection(secrets.AccountIds, _ => { }, _ => { });
        Assert.Equal(2, handler.LyricsRequests);
    }

    [Fact]
    public async Task NativeLyrics_PublicContextIsDeniedBeforeSecretOrLyricsHttp()
    {
        var handler = new LyricsHandler { LyricsPayload = "{\"lyrics\":{}}" };
        var secrets = new FakeSecretAccessor("must-not-be-used");
        var adapter = new SpotifyPlaylistCapabilityAdapter(new HttpClient(handler), secrets);

        var outcome = await new SpotifyLyricsCapabilityAdapter(adapter).FetchLyricsAsync(PublicContext(), LyricsRequest());

        Assert.False(outcome.IsSuccess);
        Assert.Equal(ProviderErrorKind.Forbidden, outcome.Error!.Kind);
        Assert.Empty(secrets.AccountIds);
        Assert.Equal(0, handler.LyricsRequests);
    }

    [Fact]
    public async Task NativeLyrics_CancelledExpiredAndInvalidRequestsNeverUseHttp()
    {
        var handler = new LyricsHandler { LyricsPayload = "{\"lyrics\":{}}" };
        var secrets = new FakeSecretAccessor("must-not-be-used");
        var adapter = new SpotifyPlaylistCapabilityAdapter(new HttpClient(handler), secrets);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var cancelled = await new SpotifyLyricsCapabilityAdapter(adapter).FetchLyricsAsync(
            LyricsContext(Guid.Parse("33333333-3333-3333-3333-333333333333"), cancellationToken: cancellation.Token), LyricsRequest());
        var expired = await new SpotifyLyricsCapabilityAdapter(adapter).FetchLyricsAsync(
            LyricsContext(Guid.Parse("33333333-3333-3333-3333-333333333333"), deadline: DateTimeOffset.UtcNow.AddSeconds(-1)), LyricsRequest());
        var invalid = await new SpotifyLyricsCapabilityAdapter(adapter).FetchLyricsAsync(
            Context(), LyricsRequest(trackId: "too-short"));

        Assert.Equal(ProviderErrorKind.Canceled, cancelled.Error!.Kind);
        Assert.Equal(ProviderErrorKind.CapabilityUnavailable, expired.Error!.Kind);
        Assert.Equal(ProviderErrorKind.PermanentFailure, invalid.Error!.Kind);
        Assert.Empty(secrets.AccountIds);
        Assert.Equal(0, handler.LyricsRequests);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, ProviderErrorKind.RateLimited)]
    [InlineData(HttpStatusCode.BadGateway, ProviderErrorKind.TransientFailure)]
    public async Task NativeLyrics_MapsRateLimitAndServerFailuresToSafeOutcomes(
        HttpStatusCode status, ProviderErrorKind expected)
    {
        var handler = new LyricsHandler { LyricsStatus = status };
        var adapter = new SpotifyPlaylistCapabilityAdapter(new HttpClient(handler), new FakeSecretAccessor("cookie"));

        var outcome = await new SpotifyLyricsCapabilityAdapter(adapter).FetchLyricsAsync(Context(), LyricsRequest());

        Assert.False(outcome.IsSuccess);
        Assert.Equal(expected, outcome.Error!.Kind);
        Assert.DoesNotContain("upstream", outcome.Error.SafeMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NativeLyrics_OversizedBodyFailsAsCompatibilityChangeWithoutBodyLeak()
    {
        var handler = new LyricsHandler { LyricsPayload = new string('x', 2_000_001) };
        var adapter = new SpotifyPlaylistCapabilityAdapter(new HttpClient(handler), new FakeSecretAccessor("cookie"));

        var outcome = await new SpotifyLyricsCapabilityAdapter(adapter).FetchLyricsAsync(Context(), LyricsRequest());

        Assert.False(outcome.IsSuccess);
        Assert.Equal("provider-contract-changed", outcome.Error!.Code);
        Assert.DoesNotContain("xxx", outcome.Error.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NativeLyrics_MapsNotFoundToUnavailableWithoutContent()
    {
        var handler = new LyricsHandler { LyricsStatus = HttpStatusCode.NotFound };
        var adapter = new SpotifyPlaylistCapabilityAdapter(new HttpClient(handler), new FakeSecretAccessor("cookie"));

        var outcome = await new SpotifyLyricsCapabilityAdapter(adapter).FetchLyricsAsync(Context(), LyricsRequest());

        Assert.True(outcome.IsSuccess, outcome.Error?.ToString());
        var result = outcome.RequireValue();
        Assert.Equal(ProviderLyricsAvailabilityState.Unavailable, result.Availability);
        Assert.Null(result.Format);
        Assert.Null(result.Content);
    }

    [Fact]
    public async Task NativeLyrics_RejectsMalformedPayloadAsCompatibilityFailure()
    {
        var handler = new LyricsHandler
        {
            LyricsPayload = "{\"lyrics\":{\"syncType\":\"LINE_SYNCED\",\"lines\":[{\"words\":\"missing time\"}]}}"
        };
        var adapter = new SpotifyPlaylistCapabilityAdapter(new HttpClient(handler), new FakeSecretAccessor("cookie"));

        var outcome = await new SpotifyLyricsCapabilityAdapter(adapter).FetchLyricsAsync(Context(), LyricsRequest());

        Assert.False(outcome.IsSuccess);
        Assert.Equal("provider-contract-changed", outcome.Error!.Code);
        Assert.DoesNotContain("missing time", outcome.Error.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NativeLyrics_RejectsWrongResourceBeforeSecretOrNetworkUse()
    {
        var handler = new LyricsHandler
        {
            LyricsPayload = "{\"lyrics\":{\"syncType\":\"UNSYNCED\",\"lines\":[{\"words\":\"ignored\"}]}}"
        };
        var secrets = new FakeSecretAccessor("must-not-be-used");
        var adapter = new SpotifyPlaylistCapabilityAdapter(new HttpClient(handler), secrets);

        var outcome = await new SpotifyLyricsCapabilityAdapter(adapter).FetchLyricsAsync(
            Context(), LyricsRequest(providerId: "other-provider"));

        Assert.False(outcome.IsSuccess);
        Assert.Equal(ProviderErrorKind.Forbidden, outcome.Error!.Kind);
        Assert.Empty(secrets.AccountIds);
        Assert.Equal(0, handler.LyricsRequests);
    }

    [Fact]
    public async Task NativeLyrics_UsesOneExchangeAfterPlaylistReadOnTheSameClient()
    {
        var handler = new LyricsHandler
        {
            LyricsPayload = "{\"lyrics\":{\"syncType\":\"UNSYNCED\",\"lines\":[{\"words\":\"Cached scope\"}]}}"
        };
        var secrets = new FakeSecretAccessor("cookie");
        var adapter = new SpotifyPlaylistCapabilityAdapter(new HttpClient(handler), secrets);

        var playlist = await adapter.GetUserPlaylistsAsync(Context(), new(new ProviderPageRequest()));
        var lyrics = await new SpotifyLyricsCapabilityAdapter(adapter).FetchLyricsAsync(Context(), LyricsRequest());

        Assert.True(playlist.IsSuccess, playlist.Error?.ToString());
        Assert.True(lyrics.IsSuccess, lyrics.Error?.ToString());
        Assert.Single(secrets.AccountIds);
        Assert.Equal(1, handler.LyricsRequests);
    }

    [Fact]
    public void NativeLyrics_RegistrationRequiresAnAccountAndDeclaresItsFixedOrigin()
    {
        var adapter = new SpotifyPlaylistCapabilityAdapter(new HttpClient(new SpotifyFakeHandler()), new FakeSecretAccessor("cookie"));
        var registration = SpotifyPlaylistCapabilityAdapter.CreateRegistration(
            adapter, new SpotifyLyricsCapabilityAdapter(adapter));

        var validated = ProviderRegistrationValidator.Validate(registration);
        var lyrics = validated.Descriptor.Capabilities.Single(item => item.Capability == ProviderCapabilityKind.Lyrics);

        Assert.Equal(ProviderAccountRequirement.Required, lyrics.AccountRequirement);
        Assert.Contains(ProviderAccountScope.Personal, lyrics.AllowedAccountScopes);
        Assert.Contains(ProviderAccountScope.Shared, lyrics.AllowedAccountScopes);
        Assert.False(lyrics.SupportsPublicRead);
        Assert.Contains("fetchLyrics", lyrics.Hooks);
        Assert.Contains(validated.Descriptor.Permissions.NetworkOrigins,
            origin => origin == new Uri("https://spclient.wg.spotify.com/"));
    }

    [Fact]
    public async Task NativeLyrics_TokenParserCanBeUsedByAnExplicitAccountProbe()
    {
        var handler = new LyricsHandler
        {
            LyricsPayload = "{\"lyrics\":{\"syncType\":\"UNSYNCED\",\"lines\":[{\"words\":\"Probe\"}]}}"
        };

        var outcome = await SpotifyLyricsCapabilityAdapter.ReadLyricsFromTokenAsync(
            new HttpClient(handler), "account-access-token", LyricsRequest(), default);

        Assert.True(outcome.IsSuccess, outcome.Error?.ToString());
        Assert.Equal("Probe", outcome.RequireValue().Content);
        Assert.Equal("Bearer account-access-token", handler.LyricsAuthorization);
        Assert.Null(handler.LyricsCookie);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeLyrics_CancellationOrDeadlineDuringCacheReadCannotReturnContent(bool deadline)
    {
        using var canceled = new CancellationTokenSource();
        var cache = new Mock<IApplicationCache>(MockBehavior.Strict);
        cache.Setup(item => item.GetAsync<ProviderLyricsResult>(It.IsAny<string>()))
            .Returns(() =>
            {
                if (deadline) return new TaskCompletionSource<ProviderLyricsResult?>().Task;
                canceled.Cancel();
                return Task.FromResult<ProviderLyricsResult?>(new(ProviderLyricsAvailabilityState.Available,
                    "spotify", ProviderLyricsFormat.PlainText, "Cached private content"));
            });
        var handler = new LyricsHandler();
        var secrets = new FakeSecretAccessor("must-not-be-used");
        var owner = new SpotifyPlaylistCapabilityAdapter(new HttpClient(handler), secrets, cache: cache.Object);
        var context = LyricsContext(Guid.NewGuid(), cancellationToken: canceled.Token,
            deadline: DateTimeOffset.UtcNow.AddSeconds(deadline ? 1 : 60));
        var result = await new SpotifyLyricsCapabilityAdapter(owner).FetchLyricsAsync(context, LyricsRequest())
            .WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(ProviderErrorKind.Canceled, result.Error?.Kind);
        Assert.Empty(secrets.AccountIds);
        Assert.Equal(0, handler.LyricsRequests);
    }

    private static ProviderLyricsRequest LyricsRequest(
        ProviderLyricsFormat? preferredFormat = null,
        string providerId = SpotifyPlaylistCapabilityAdapter.StableProviderId,
        bool availabilityOnly = false,
        string? trackId = null) => new(
        LyricsRecordingId,
        new ProviderExternalResourceId(providerId, ProviderResourceKind.Track, trackId ?? LyricsTrackId),
        availabilityOnly: availabilityOnly,
        preferredFormat: preferredFormat,
        trackTitle: "Fixture track",
        artistNames: ["Fixture artist"],
        albumTitle: "Fixture album",
        durationSeconds: 180);

    private static ProviderExecutionContext LyricsContext(
        Guid accountId,
        long revision = 1,
        Guid? secretReferenceId = null,
        DateTimeOffset? deadline = null,
        CancellationToken cancellationToken = default)
    {
        var user = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var account = new ProviderAccountContext(
            accountId,
            "spotify",
            ProviderAccountScope.Personal,
            revision,
            ownerUserId: user,
            secretReferenceId: secretReferenceId ?? Guid.Parse("44444444-4444-4444-4444-444444444444"));
        return new(
            new ProviderActorContext(ProviderActorKind.User, user,
                new ProviderBackendPrincipal("jellyfin", "backend", "principal")),
            "spotify",
            account,
            new ProviderExecutionPolicy(
                new ProviderQualityPolicy(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true),
                ProviderExplicitContentPolicy.Allow,
                true, true, false, ["spotify"]),
            "lyrics-read",
            "correlation",
            deadline ?? DateTimeOffset.UtcNow.AddMinutes(1),
            cancellationToken);
    }

    private static void EnsureLyricsCacheSettings() =>
        CacheExtensions.InitializeCacheSettings(new ServiceCollection().BuildServiceProvider());

    private sealed class LyricsHandler : HttpMessageHandler
    {
        private readonly SpotifyFakeHandler _fallback = new();
        private readonly HttpMessageInvoker _invoker;

        public LyricsHandler()
        {
            _fallback.TokenExpirationTimestampMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds();
            _invoker = new HttpMessageInvoker(_fallback);
        }

        public HttpStatusCode LyricsStatus { get; set; } = HttpStatusCode.OK;
        public string? LyricsPayload { get; set; }
        public HttpStatusCode TokenStatus
        {
            get => _fallback.TokenStatus;
            set => _fallback.TokenStatus = value;
        }

        public string TokenValue
        {
            get => _fallback.TokenValue;
            set => _fallback.TokenValue = value;
        }

        public int LyricsRequests { get; private set; }
        public string? LyricsRequestTarget { get; private set; }
        public string? LyricsAuthorization { get; private set; }
        public string? LyricsCookie { get; private set; }
        public string? LyricsUserAgent { get; private set; }
        public string? LyricsAppPlatform { get; private set; }
        public string? LyricsAppVersion { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.Host == "spclient.wg.spotify.com" &&
                request.RequestUri.AbsolutePath.StartsWith("/color-lyrics/v2/track/", StringComparison.Ordinal))
            {
                LyricsRequests++;
                LyricsRequestTarget = $"{request.Method} {request.RequestUri.PathAndQuery}";
                LyricsAuthorization = request.Headers.Authorization?.ToString();
                LyricsCookie = request.Headers.TryGetValues("Cookie", out var cookies) ? cookies.Single() : null;
                LyricsUserAgent = request.Headers.UserAgent.ToString();
                LyricsAppPlatform = request.Headers.TryGetValues("app-platform", out var platform) ? platform.Single() : null;
                LyricsAppVersion = request.Headers.TryGetValues("spotify-app-version", out var version) ? version.Single() : null;
                var response = new HttpResponseMessage(LyricsStatus);
                if (LyricsPayload != null)
                    response.Content = new StringContent(LyricsPayload, Encoding.UTF8, "application/json");
                return response;
            }

            return await _invoker.SendAsync(request, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _invoker.Dispose();
            base.Dispose(disposing);
        }
    }
}

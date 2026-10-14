using allstarr.Core.Capabilities;
using allstarr.Core.Playlists.Sources;
using allstarr.Core.Providers.Spotify;
using allstarr.Core.Storage;
using allstarr.Models.Settings;
using System.Net;

namespace allstarr.Tests;

public sealed partial class SpotifyPlaylistCapabilityAdapterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultiPageImport_PerformsOneExchangeAndPreservesEverySourcePosition(bool anonymous)
    {
        var handler = new SpotifyFakeHandler
        {
            TokenIsAnonymous = anonymous,
            TokenExpirationTimestampMs = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds()
        };
        var secrets = new FakeSecretAccessor("fixture-cookie");
        var adapter = new SpotifyPlaylistCapabilityAdapter(new HttpClient(handler), secrets);
        var context = anonymous ? PublicContext() : Context();
        var result = await new ProviderPlaylistSnapshotCollector().CollectAsync(adapter, context,
            new(new("spotify", ProviderResourceKind.Playlist, "playlist-opaque"), PageSize: 2));

        Assert.True(result.IsSuccess);
        Assert.Equal(4, result.PagesRead);
        Assert.Equal(Enumerable.Range(0, 8), result.Snapshot!.Entries.Select(item => item.SourcePosition));
        Assert.Equal(4, handler.ApiPaths.Count);
        Assert.Equal(1, Exchanges(handler));
        Assert.Equal(1, SecretFetches(handler));
        Assert.Equal(1, handler.Paths.Count(path => path == "/"));
        Assert.Equal(anonymous ? 0 : 1, secrets.AccountIds.Count);
        if (anonymous) Assert.Null(handler.CookieHeader);
    }

    [Fact]
    public async Task Tokens_AreIsolatedByAccountRevisionAndAnonymousScope()
    {
        var handler = new SpotifyFakeHandler
        { TokenExpirationTimestampMs = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds() };
        var secrets = new FakeSecretAccessor("fixture-cookie");
        var adapter = new SpotifyPlaylistCapabilityAdapter(new HttpClient(handler), secrets);
        var first = Context();
        var secondId = Guid.CreateVersion7();
        var second = TokenContext(secondId, 1);
        handler.TokenValue = "first-account-token";
        Assert.True((await TokenPage(adapter, first)).IsSuccess);
        handler.TokenValue = "second-account-token";
        Assert.True((await TokenPage(adapter, second)).IsSuccess);
        Assert.Equal("Bearer second-account-token", handler.ApiAuthorizationHeaders[^1]);
        Assert.True((await TokenPage(adapter, first)).IsSuccess);
        Assert.Equal("Bearer first-account-token", handler.ApiAuthorizationHeaders[^1]);
        Assert.Equal(2, Exchanges(handler));
        Assert.Equal(new[] { first.Account!.AccountId, secondId }, secrets.AccountIds);

        handler.TokenIsAnonymous = true;
        Assert.True((await TokenPage(adapter, PublicContext())).IsSuccess);
        Assert.True((await TokenPage(adapter, PublicContext())).IsSuccess);
        Assert.Equal(3, Exchanges(handler));
        Assert.Equal(2, secrets.AccountIds.Count);
        Assert.Null(handler.CookieHeader);

        handler.TokenIsAnonymous = false;
        Assert.True((await TokenPage(adapter, TokenContext(first.Account.AccountId, 2))).IsSuccess);
        Assert.True((await TokenPage(adapter, first)).IsSuccess);
        Assert.Equal(4, Exchanges(handler));
        Assert.Equal(3, secrets.AccountIds.Count);
        Assert.Equal(1, SecretFetches(handler));
    }

    [Fact]
    public async Task Tokens_WithUnknownExpiryAreNotReusedButTotpSecretsAre()
    {
        var handler = new SpotifyFakeHandler();
        var adapter = new SpotifyPlaylistCapabilityAdapter(new HttpClient(handler), new FakeSecretAccessor("cookie"));
        Assert.True((await TokenPage(adapter, Context())).IsSuccess);
        Assert.True((await TokenPage(adapter, Context())).IsSuccess);
        Assert.Equal(2, Exchanges(handler));
        Assert.Equal(1, SecretFetches(handler));
    }

    [Fact]
    public async Task ConcurrentPages_ShareOnePendingExchange()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new SpotifyFakeHandler
        {
            TokenExpirationTimestampMs = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(),
            TokenRequested = requested,
            TokenRelease = release.Task
        };
        var secrets = new FakeSecretAccessor("cookie");
        var adapter = new SpotifyPlaylistCapabilityAdapter(new HttpClient(handler), secrets);
        var first = TokenPage(adapter, Context());
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = TokenPage(adapter, Context());
        release.SetResult();
        var results = await Task.WhenAll(first, second);
        Assert.All(results, item => Assert.True(item.IsSuccess));
        Assert.Equal(1, Exchanges(handler));
        Assert.Single(secrets.AccountIds);
    }

    [Fact]
    public async Task ExpiredTokensAndOldTotpSecretsAreRefreshedUsingTheClock()
    {
        var clock = new TokenClock();
        var handler = new SpotifyFakeHandler
        { TokenExpirationTimestampMs = clock.GetUtcNow().AddMinutes(1).ToUnixTimeMilliseconds() };
        var adapter = new SpotifyPlaylistCapabilityAdapter(new HttpClient(handler),
            new FakeSecretAccessor("cookie"), timeProvider: clock);
        Assert.True((await TokenPage(adapter, Context())).IsSuccess);
        Assert.True((await TokenPage(adapter, Context())).IsSuccess);
        Assert.Equal(1, Exchanges(handler));
        clock.Advance(TimeSpan.FromMinutes(2));
        handler.TokenExpirationTimestampMs = clock.GetUtcNow().AddMinutes(1).ToUnixTimeMilliseconds();
        Assert.True((await TokenPage(adapter, Context())).IsSuccess);
        Assert.Equal(2, Exchanges(handler));
        Assert.Equal(1, SecretFetches(handler));
        clock.Advance(TimeSpan.FromMinutes(6));
        handler.TokenExpirationTimestampMs = clock.GetUtcNow().AddMinutes(1).ToUnixTimeMilliseconds();
        Assert.True((await TokenPage(adapter, Context())).IsSuccess);
        Assert.Equal(3, Exchanges(handler));
        Assert.Equal(2, SecretFetches(handler));
    }

    [Fact]
    public async Task RejectedTokenIsEvictedWithoutRefreshingHealthyAccounts()
    {
        var handler = new SpotifyFakeHandler
        { TokenExpirationTimestampMs = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds() };
        var adapter = new SpotifyPlaylistCapabilityAdapter(new HttpClient(handler), new FakeSecretAccessor("cookie"));
        var healthy = TokenContext(Guid.CreateVersion7(), 1);
        Assert.True((await TokenPage(adapter, healthy)).IsSuccess);
        Assert.True((await TokenPage(adapter, Context())).IsSuccess);
        handler.ApiStatus = HttpStatusCode.Unauthorized;
        var rejected = await TokenPage(adapter, Context());
        Assert.Equal(ProviderErrorKind.Unauthorized, rejected.Error!.Kind);
        handler.ApiStatus = null;
        Assert.True((await TokenPage(adapter, healthy)).IsSuccess);
        handler.TokenValue = "refreshed-fixture-token";
        Assert.True((await TokenPage(adapter, Context())).IsSuccess);
        Assert.Equal(3, Exchanges(handler));
        Assert.Equal("Bearer refreshed-fixture-token", handler.ApiAuthorizationHeaders[^1]);
        Assert.Equal(1, SecretFetches(handler));
    }

    [Fact]
    public async Task WarmTokenCannotBypassMissingSecretOrChangedCompatibilitySettings()
    {
        var handler = new SpotifyFakeHandler
        { TokenExpirationTimestampMs = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds() };
        var settings = new SpotifyApiSettings();
        var adapter = new SpotifyPlaylistCapabilityAdapter(new HttpClient(handler), new FakeSecretAccessor("cookie"), settings: settings);
        Assert.True((await TokenPage(adapter, Context())).IsSuccess);
        var paths = handler.Paths.Count;
        var missing = await TokenPage(adapter, Context(includeSecretReference: false));
        Assert.Equal(ProviderErrorKind.AccountNeedsConfiguration, missing.Error!.Kind);
        settings.TokenEndpoint = "https://untrusted.example.test/api/token";
        var invalid = await TokenPage(adapter, Context());
        Assert.False(invalid.IsSuccess);
        Assert.Equal(paths, handler.Paths.Count);
        settings.TokenEndpoint = SpotifyApiSettings.DefaultTokenEndpoint;
        settings.TotpSecretsUrl = "https://raw.githubusercontent.com/fixture/changed/secretBytes.json";
        Assert.True((await TokenPage(adapter, Context())).IsSuccess);
        Assert.Equal(2, Exchanges(handler));
        Assert.Equal(2, SecretFetches(handler));
    }

    [Fact]
    public async Task NearExpiryTokensRefreshBeforeAnotherPageCanUseThem()
    {
        var clock = new TokenClock();
        var handler = new SpotifyFakeHandler
        { TokenExpirationTimestampMs = clock.GetUtcNow().AddMinutes(1).ToUnixTimeMilliseconds() };
        var adapter = new SpotifyPlaylistCapabilityAdapter(new HttpClient(handler), new FakeSecretAccessor("cookie"), timeProvider: clock);
        Assert.True((await TokenPage(adapter, Context())).IsSuccess);
        clock.Advance(TimeSpan.FromSeconds(31));
        handler.TokenExpirationTimestampMs = clock.GetUtcNow().AddHours(1).ToUnixTimeMilliseconds();
        Assert.True((await TokenPage(adapter, Context())).IsSuccess);
        Assert.Equal(2, Exchanges(handler));
        Assert.Equal(1, SecretFetches(handler));
    }

    [Fact]
    public async Task AnonymousPagesArtworkAndProfileBrowseReuseTheSameToken()
    {
        var handler = new SpotifyFakeHandler
        { TokenIsAnonymous = true, TokenExpirationTimestampMs = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds() };
        var secrets = new FakeSecretAccessor("unused");
        var adapter = new SpotifyPlaylistCapabilityAdapter(new HttpClient(handler), secrets, cache: new TestMemoryApplicationCache());
        var page = (await TokenPage(adapter, PublicContext())).RequireValue();
        var artwork = await adapter.ResolveArtworkAsync(PublicContext(), new(page.Playlist.Artwork!, maximumBytes: 16));
        var profile = await adapter.GetUserPlaylistsAsync(PublicContext(), new(new ProviderPageRequest(2), "profile-owner"));
        Assert.True(artwork.IsSuccess);
        Assert.True(profile.IsSuccess);
        Assert.Equal(1, Exchanges(handler));
        Assert.Equal(1, SecretFetches(handler));
        Assert.Empty(secrets.AccountIds);
        Assert.Null(handler.CookieHeader);
    }

    private static Task<ProviderOutcome<ProviderPlaylistTrackPage>> TokenPage(
        SpotifyPlaylistCapabilityAdapter adapter, ProviderExecutionContext context) =>
        adapter.GetPlaylistTracksAsync(context,
            new(new("spotify", ProviderResourceKind.Playlist, "playlist-opaque"), new ProviderPageRequest(2)));

    private static int Exchanges(SpotifyFakeHandler handler) => handler.Paths.Count(path => path.StartsWith("/api/token?", StringComparison.Ordinal));
    private static int SecretFetches(SpotifyFakeHandler handler) => handler.Paths.Count(path => path.Contains("secretBytes.json", StringComparison.Ordinal));
    private static ProviderExecutionContext TokenContext(Guid id, long revision)
    {
        var original = Context();
        var account = new ProviderAccountContext(id, "spotify", ProviderAccountScope.Personal, revision,
            ownerUserId: original.Actor.EffectiveUserId, secretReferenceId: original.Account!.SecretReferenceId);
        return new(original.Actor, "spotify", account, original.Policy, original.OperationId,
            original.CorrelationId, original.Deadline, original.CancellationToken);
    }

    private sealed class TokenClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }
}

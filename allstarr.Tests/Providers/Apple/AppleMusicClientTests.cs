using System.Net;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.AppleMusicKit;
using allstarr.Core.Providers.Spotify;
using allstarr.Core.Storage;

namespace allstarr.Tests;

public sealed class AppleMusicClientTests
{
    [Fact]
    public async Task Public_catalog_and_playlist_never_resolve_or_send_personal_credentials()
    {
        var secrets = new SelectedSecrets();
        var calls = new List<string>();
        using var http = new HttpClient(new AppleProviderTestFactory.Handler(request =>
        {
            calls.Add(request.RequestUri!.AbsolutePath);
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.False(request.Headers.Contains("Music-User-Token"));
            return Json(request.RequestUri.AbsolutePath.EndsWith("/tracks")
                ? """{"data":[{"id":"101","attributes":{"name":"Track","artistName":"Artist"}}]}"""
                : """{"data":[{"id":"pl.public","attributes":{"name":"Playlist","description":""}}]}""");
        }));
        var client = AppleProviderTestFactory.Client(http, secrets);
        var playlist = new AppleMusicKitPlaylistCapabilityAdapter(client, http);
        var result = await playlist.GetPlaylistTracksAsync(Public(), new(new("apple-musickit", ProviderResourceKind.Playlist, "pl.public"), new()));
        Assert.True(result.IsSuccess, result.Error?.Code);
        Assert.Single(result.RequireValue().Tracks.Items);
        Assert.Equal(2, calls.Count);
        Assert.Empty(secrets.Reads);
        var privateResult = await playlist.GetUserPlaylistsAsync(Public(), new(new()));
        Assert.Equal(ProviderErrorKind.Forbidden, privateResult.Error!.Kind);
        Assert.Equal(2, calls.Count);
    }

    [Fact]
    public async Task Concurrent_personal_reads_use_the_exact_accounts_and_never_reuse_a_token()
    {
        var first = Personal();
        var second = Personal();
        var secrets = new SelectedSecrets();
        secrets.Values[first.Account!.AccountId] = "first-selected-token";
        secrets.Values[second.Account!.AccountId] = "second-selected-token";
        var received = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var http = new HttpClient(new AppleProviderTestFactory.Handler(request =>
        {
            received.Add(request.Headers.GetValues("Cookie").Single());
            Assert.Equal("https://music.apple.com", request.Headers.GetValues("Origin").Single());
            return Json("{\"data\":[]}");
        }));
        var client = AppleProviderTestFactory.Client(http, secrets);
        var playlist = new AppleMusicKitPlaylistCapabilityAdapter(client, http);
        var result = await Task.WhenAll(playlist.GetUserPlaylistsAsync(first, new(new())), playlist.GetUserPlaylistsAsync(second, new(new())));
        Assert.All(result, value => Assert.True(value.IsSuccess));
        Assert.Equal(new[] { "media-user-token=first-selected-token", "media-user-token=second-selected-token" }, received.Order());
        Assert.Equal(2, secrets.Reads.Count);
    }

    [Fact]
    public async Task Unauthorized_refreshes_the_public_bearer_once_without_switching_personal_accounts()
    {
        var scripts = 0;
        var first = AppleProviderTestFactory.Bearer;
        var next = first[..first.LastIndexOf('.')] + ".bmV3";
        using var web = new HttpClient(new AppleProviderTestFactory.Handler(request => JsonText(request.RequestUri!.AbsolutePath == "/"
            ? "<script src=\"/assets/index-test.js\"></script>"
            : $"\"{(Interlocked.Increment(ref scripts) == 1 ? first : next)}\"")));
        var authorizations = new List<string>();
        using var http = new HttpClient(new AppleProviderTestFactory.Handler(request =>
        {
            authorizations.Add(request.Headers.Authorization!.Parameter!);
            Assert.Equal("media-user-token=fixture-user", request.Headers.GetValues("Cookie").Single());
            return authorizations.Count == 1 ? new(HttpStatusCode.Unauthorized) : Json("{\"data\":[]}");
        }));
        var secrets = new SelectedSecrets();
        var client = new AppleMusicClient(http, new(web), secrets);
        var playlist = new AppleMusicKitPlaylistCapabilityAdapter(client, http);
        Assert.True((await playlist.GetUserPlaylistsAsync(Personal(), new(new()))).IsSuccess);
        Assert.Equal(new[] { first, next }, authorizations);
        Assert.Equal(2, scripts);
        Assert.Single(secrets.Reads);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"DeveloperToken\":\"obsolete\"}")]
    [InlineData("{\"MusicUserToken\":\"injected;cookie\",\"Storefront\":\"us\"}")]
    [InlineData("{\"MusicUserToken\":\"valid\",\"Storefront\":\"usa\"}")]
    public async Task Missing_or_invalid_selected_credentials_fail_before_network(string value)
    {
        var secrets = new SelectedSecrets { Raw = value };
        var client = AppleProviderTestFactory.Client(secrets: secrets);
        var result = await client.ResolveCatalogTrackAsync(Personal(), "101");
        Assert.Equal(ProviderErrorKind.AccountNeedsConfiguration, result.Error!.Kind);
    }

    [Fact]
    public async Task Sidecar_transport_scopes_revision_and_rechecks_revocation_without_retaining_headers()
    {
        var secrets = new SelectedSecrets();
        var client = AppleProviderTestFactory.Client(secrets: secrets);
        var context = Personal();
        var receivedScopes = new List<string>();
        using var transport = new HttpClient(new AppleProviderTestFactory.Handler(request =>
        {
            Assert.Equal("fixture-user", request.Headers.GetValues("Music-User-Token").Single());
            Assert.Equal("us", request.Headers.GetValues("X-Apple-Storefront").Single());
            receivedScopes.Add(request.Headers.GetValues("X-Allstarr-Account-Context").Single());
            return new(HttpStatusCode.OK);
        }));
        using var first = new HttpRequestMessage(HttpMethod.Get, "https://gateway.test/api/download/101");
        using var response = await client.SendSidecarAsync(context, transport, first, default);
        Assert.False(first.Headers.Contains("Music-User-Token"));
        Assert.False(first.Headers.Contains("X-Allstarr-Account-Context"));
        var revision = Personal(context.Account!.AccountId, context.Actor.UserId, 2);
        using var second = new HttpRequestMessage(HttpMethod.Get, first.RequestUri);
        using var response2 = await client.SendSidecarAsync(revision, transport, second, default);
        Assert.NotEqual(receivedScopes[0], receivedScopes[1]);
        Assert.All(receivedScopes, value => Assert.Matches("^[0-9a-f]{64}$", value));
        secrets.Revoked = true;
        using var revoked = new HttpRequestMessage(HttpMethod.Get, first.RequestUri);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.SendSidecarAsync(context, transport, revoked, default));
        Assert.Equal(2, receivedScopes.Count);
    }

    [Fact]
    public async Task Web_token_failure_is_safe_and_exposed_for_account_status()
    {
        using var web = new HttpClient(new AppleProviderTestFactory.Handler(_ => new(HttpStatusCode.ServiceUnavailable)
        { Content = new StringContent("private-upstream-body") }));
        var tokens = new AppleWebTokenProvider(web);
        var client = new AppleMusicClient(new HttpClient(), tokens, new SelectedSecrets());
        var outcome = await new AppleMusicKitPlaylistCapabilityAdapter(client, new HttpClient()).GetUserPlaylistsAsync(Personal(), new(new()));
        Assert.Equal("apple-web-token-unavailable", outcome.Error!.Code);
        Assert.Equal(outcome.Error.Code, tokens.FailureCode);
        Assert.Contains("Retry", outcome.Error.SafeMessage);
        Assert.DoesNotContain("private-upstream-body", outcome.Error.ToString());
    }

    [Fact]
    public async Task Catalog_reads_use_the_account_storefront_without_leasing_the_secret()
    {
        var secrets = new SelectedSecrets();
        var paths = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var http = new HttpClient(new AppleProviderTestFactory.Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            paths.Add(path);
            Assert.False(request.Headers.Contains("Cookie"));
            return Json(path.EndsWith("/search", StringComparison.Ordinal)
                ? """{"results":{"songs":{"data":[]},"playlists":{"data":[]}}}"""
                : path.EndsWith("/tracks", StringComparison.Ordinal)
                    ? """{"data":[]}"""
                    : path.Contains("/playlists/", StringComparison.Ordinal)
                        ? """{"data":[{"id":"pl.public","attributes":{"name":"Playlist","description":""}}]}"""
                        : """{"data":[{"id":"101","attributes":{"name":"Track","artistName":"Artist"}}]}""");
        }));
        var client = AppleProviderTestFactory.Client(http, secrets, new Storefronts(" GB "));
        var metadata = new AppleMusicKitMetadataCapabilityAdapter(client);
        var playlist = new AppleMusicKitPlaylistCapabilityAdapter(client, http);
        var account = Personal();

        Assert.True((await metadata.SearchTracksAsync(account, new("query", new(5)))).IsSuccess);
        Assert.True((await metadata.LookupByIsrcAsync(account, new("USAT21234567"))).IsSuccess);
        Assert.True((await metadata.GetTrackAsync(account, new(new("apple-musickit", ProviderResourceKind.Track, "101")))).IsSuccess);
        Assert.True((await playlist.GetPlaylistTracksAsync(account,
            new(new("apple-musickit", ProviderResourceKind.Playlist, "pl.public"), new()))).IsSuccess);
        Assert.True((await playlist.SearchPlaylistsAsync(Public(), new("query", new(5)))).IsSuccess);

        Assert.Empty(secrets.Reads);
        Assert.Equal(5, paths.Count(path => path.StartsWith("/v1/catalog/gb/", StringComparison.Ordinal)));
        Assert.Equal(["/v1/catalog/us/search"], paths.Where(path => !path.StartsWith("/v1/catalog/gb/", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("usa")]
    [InlineData("g1")]
    public async Task Missing_or_invalid_storefront_setting_uses_the_default(string? storefront)
    {
        var paths = new List<string>();
        using var http = new HttpClient(new AppleProviderTestFactory.Handler(request =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            return Json("""{"results":{"songs":{"data":[]}}}""");
        }));
        var metadata = new AppleMusicKitMetadataCapabilityAdapter(
            AppleProviderTestFactory.Client(http, new SelectedSecrets(), new Storefronts(storefront)));
        Assert.True((await metadata.SearchTracksAsync(Personal(), new("query", new(5)))).IsSuccess);
        Assert.Equal(["/v1/catalog/us/search"], paths);
    }

    [Fact]
    public async Task Catalog_and_personal_unauthorized_keep_their_own_failure()
    {
        var secrets = new SelectedSecrets();
        using var http = new HttpClient(new AppleProviderTestFactory.Handler(_ => new(HttpStatusCode.Unauthorized)));
        var client = AppleProviderTestFactory.Client(http, secrets, new Storefronts("gb"));
        var account = Personal();

        var catalog = await new AppleMusicKitMetadataCapabilityAdapter(client).SearchTracksAsync(account, new("query", new(5)));
        Assert.Equal("apple-web-token-unavailable", catalog.Error!.Code);
        Assert.Empty(secrets.Reads);

        var personal = await new AppleMusicKitPlaylistCapabilityAdapter(client, http).GetUserPlaylistsAsync(account, new(new()));
        Assert.Equal(ProviderErrorKind.AccountNeedsReauthentication, personal.Error!.Kind);
        Assert.Single(secrets.Reads);
    }

    [Fact]
    public void Account_settings_projection_keeps_only_non_secret_values()
    {
        var descriptor = AppleMusicKitPlaylistCapabilityAdapter.Descriptor([AppleMusicKitPlaylistCapabilityAdapter.MetadataDescriptor]);
        var json = ProviderAccountSettings.Project(descriptor,
            Encoding.UTF8.GetBytes("""{"MusicUserToken":"private-token","Storefront":"gb","extra":"ignored"}"""));
        Assert.Equal("""{"storefront":"gb"}""", json);
        Assert.Equal("gb", ProviderAccountSettings.ReadText(json, "storefront"));
        Assert.Equal(ProviderAccountSettings.Empty, ProviderAccountSettings.Project(descriptor, Encoding.UTF8.GetBytes("not json")));
    }

    internal static ProviderExecutionContext Public() => new(new(ProviderActorKind.PublicRead, null), "apple-musickit", null,
        Policy(), "catalog-read", "fixture", DateTimeOffset.UtcNow.AddMinutes(1), default);
    internal static ProviderExecutionContext Personal(Guid? accountId = null, Guid? userId = null, long revision = 1)
    {
        var user = userId ?? Guid.NewGuid();
        return new(new(ProviderActorKind.User, user, new("jellyfin", "backend", "fixture")), "apple-musickit",
            new(accountId ?? Guid.NewGuid(), "apple-musickit", ProviderAccountScope.Personal, revision,
                ownerUserId: user, secretReferenceId: Guid.NewGuid()), Policy(), "account-read", "fixture", DateTimeOffset.UtcNow.AddMinutes(1), default);
    }
    private static ProviderExecutionPolicy Policy() => new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true),
        ProviderExplicitContentPolicy.Allow, true, false, true, ["apple-musickit"]);
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage JsonText(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    private sealed class Storefronts(string? value) : IProviderAccountSettingsReader
    {
        public Task<string?> GetTextAsync(ProviderAccountContext? account, string key, CancellationToken cancellationToken) =>
            Task.FromResult(account != null && key == "storefront" ? value : null);
    }
    private sealed class SelectedSecrets : IProviderAccountSecretAccessor
    {
        public Dictionary<Guid, string> Values { get; } = [];
        public System.Collections.Concurrent.ConcurrentBag<Guid> Reads { get; } = [];
        public string? Raw { get; init; }
        public bool Revoked { get; set; }
        public Task<T> UseAsync<T>(ProviderAccountContext account, Func<ReadOnlyMemory<byte>, Task<T>> operation, CancellationToken cancellationToken)
        {
            if (Revoked) throw new UnauthorizedAccessException("The account revision is no longer authorized.");
            Reads.Add(account.AccountId);
            return operation(Raw != null ? Encoding.UTF8.GetBytes(Raw) : JsonSerializer.SerializeToUtf8Bytes(
                new { MusicUserToken = Values.GetValueOrDefault(account.AccountId, "fixture-user"), Storefront = "us" }));
        }
    }
}

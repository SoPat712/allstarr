using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Spotify;
using allstarr.Core.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace allstarr.Tests;

public sealed class SpotifyPlaylistCapabilityAdapterTests
{
    [Fact]
    public async Task Selected_account_secret_fetches_deterministic_track_page_without_exposing_credentials()
    {
        var handler = new SpotifyFakeHandler();
        var secrets = new FakeSecretAccessor("account-cookie-secret");
        var adapter = new SpotifyPlaylistCapabilityAdapter(new HttpClient(handler), secrets);
        var context = Context();
        var playlistId = new ProviderExternalResourceId("spotify", ProviderResourceKind.Playlist, "playlist-opaque");

        var outcome = await adapter.GetPlaylistTracksAsync(context, new ProviderPlaylistTracksRequest(
            playlistId, new ProviderPageRequest(2, "2")));

        Assert.True(outcome.IsSuccess);
        var page = outcome.RequireValue();
        Assert.Equal("snapshot-9", page.Playlist.SourceRevision);
        Assert.Equal("\"etag-9\"", page.Playlist.SourceETag);
        Assert.Equal("Road Mix", page.Playlist.Name);
        Assert.Equal("Description", page.Playlist.Description);
        Assert.NotNull(page.Playlist.Artwork?.ResourceId);
        Assert.Null(page.Playlist.Artwork?.PublicUri);
        Assert.Equal([2, 3], page.Tracks.Items.Select(track => track.Position));
        Assert.Equal(["track-a", "track-a"], page.Tracks.Items.Select(track => track.TrackId.Value));
        Assert.Equal("4", page.Tracks.NextCursor);
        Assert.Equal("snapshot-9", page.Tracks.SnapshotVersion);
        Assert.Equal(context.Account!.AccountId, secrets.AccountIds.Single());
        Assert.Equal("sp_dc=account-cookie-secret", handler.CookieHeader);
        Assert.All(handler.ApiAuthorizationHeaders, value => Assert.Equal("Bearer account-access-token", value));

        var serialized = JsonSerializer.Serialize(page);
        Assert.DoesNotContain("account-cookie-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("account-access-token", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("image-cdn", serialized, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sp_dc=account-cookie-secret")]
    [InlineData("other=value; sp_dc=account-cookie-secret; another=value")]
    [InlineData("{\"sessionCookie\":\"account-cookie-secret\",\"sessionCookieSetDate\":\"2026-07-18T00:00:00Z\"}")]
    public async Task Session_cookie_input_formats_are_normalized_for_account_bound_requests(string storedSecret)
    {
        var handler = new SpotifyFakeHandler();
        var adapter = new SpotifyPlaylistCapabilityAdapter(new HttpClient(handler), new FakeSecretAccessor(storedSecret));

        var outcome = await adapter.GetUserPlaylistsAsync(Context(), new(new ProviderPageRequest()));

        Assert.True(outcome.IsSuccess);
        Assert.Equal("sp_dc=account-cookie-secret", handler.CookieHeader);
    }

    [Fact]
    public async Task User_playlist_and_search_cursors_are_stable_offsets()
    {
        var handler = new SpotifyFakeHandler();
        var adapter = new SpotifyPlaylistCapabilityAdapter(new HttpClient(handler), new FakeSecretAccessor("cookie"));
        var context = Context();

        var user = await adapter.GetUserPlaylistsAsync(context, new(new ProviderPageRequest(1, "5")));
        var search = await adapter.SearchPlaylistsAsync(context, new("road", new ProviderPageRequest(1, "8")));

        Assert.True(user.IsSuccess);
        Assert.True(search.IsSuccess);
        Assert.Equal("6", user.RequireValue().NextCursor);
        Assert.Equal("9", search.RequireValue().NextCursor);
        Assert.Contains(handler.Paths, path => path.Contains("offset=5", StringComparison.Ordinal));
        Assert.Contains(handler.Paths, path => path.Contains("offset=8", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Paths, path => path.Contains("stream", StringComparison.OrdinalIgnoreCase) || path.Contains("download", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Missing_secret_and_auth_failure_are_typed_and_do_not_fallback_to_global_configuration()
    {
        var handler = new SpotifyFakeHandler();
        var adapter = new SpotifyPlaylistCapabilityAdapter(new HttpClient(handler), new FakeSecretAccessor("cookie"));
        var playlist = new ProviderExternalResourceId("spotify", ProviderResourceKind.Playlist, "playlist");

        var missing = await adapter.GetPlaylistTracksAsync(Context(includeSecretReference: false),
            new ProviderPlaylistTracksRequest(playlist, new ProviderPageRequest()));
        Assert.Equal(ProviderErrorKind.AccountNeedsConfiguration, missing.Error!.Kind);
        Assert.Empty(handler.Paths);

        handler.TokenStatus = HttpStatusCode.Unauthorized;
        var unauthorized = await adapter.GetPlaylistTracksAsync(Context(),
            new ProviderPlaylistTracksRequest(playlist, new ProviderPageRequest()));
        Assert.Equal(ProviderErrorKind.Unauthorized, unauthorized.Error!.Kind);
        Assert.Single(handler.Paths);
        Assert.Equal("The provider rejected the selected account credentials.", unauthorized.Error.SafeMessage);
        Assert.DoesNotContain("cookie", unauthorized.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Spotify_rate_limit_and_revision_conflict_are_typed_without_returning_provider_bodies()
    {
        var handler = new SpotifyFakeHandler { ApiStatus = HttpStatusCode.TooManyRequests };
        var adapter = new SpotifyPlaylistCapabilityAdapter(new HttpClient(handler), new FakeSecretAccessor("cookie"));
        var playlist = new ProviderExternalResourceId("spotify", ProviderResourceKind.Playlist, "playlist");
        var limited = await adapter.GetPlaylistTracksAsync(Context(), new(playlist, new ProviderPageRequest()));
        Assert.Equal(ProviderErrorKind.RateLimited, limited.Error!.Kind);
        Assert.Equal(TimeSpan.FromSeconds(12), limited.Error.RetryAfter);

        handler.ApiStatus = null;
        var conflict = await adapter.GetPlaylistTracksAsync(Context(), new(playlist, new ProviderPageRequest(), "old-snapshot"));
        Assert.Equal(ProviderErrorKind.PermanentFailure, conflict.Error!.Kind);
        Assert.DoesNotContain("upstream-secret-body", conflict.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Artwork_is_resolved_just_in_time_with_selected_account_and_a_hard_byte_limit()
    {
        var handler = new SpotifyFakeHandler();
        var adapter = new SpotifyPlaylistCapabilityAdapter(new HttpClient(handler), new FakeSecretAccessor("cookie"));
        var reference = new ProviderArtworkReference(
            new ProviderExternalResourceId("spotify", ProviderResourceKind.Playlist, "playlist-opaque"),
            revision: "snapshot-9");

        var result = await adapter.ResolveArtworkAsync(Context(), new(reference, maximumBytes: 16));

        Assert.True(result.IsSuccess);
        Assert.Equal("image/jpeg", result.RequireValue().ContentType);
        Assert.Equal([1, 2, 3, 4], result.RequireValue().Bytes);
        Assert.Contains(handler.Paths, path => path == "/signed?token=secret");
        Assert.DoesNotContain("token=secret", JsonSerializer.Serialize(result.RequireValue()), StringComparison.Ordinal);

        handler.ArtworkBytes = new byte[17];
        var oversized = await adapter.ResolveArtworkAsync(Context(), new(reference, maximumBytes: 16));
        Assert.Equal(ProviderErrorKind.PermanentFailure, oversized.Error!.Kind);
    }

    [Fact]
    public void Registration_activates_only_the_operational_typed_playlist_capability()
    {
        var adapter = new SpotifyPlaylistCapabilityAdapter(new HttpClient(new SpotifyFakeHandler()), new FakeSecretAccessor("cookie"));
        var registration = SpotifyPlaylistCapabilityAdapter.CreateRegistration(adapter);
        var validated = ProviderRegistrationValidator.Validate(registration);
        var playlist = validated.Descriptor.Capabilities.Single(capability => capability.Capability == ProviderCapabilityKind.Playlist);

        Assert.Equal(ProviderCapabilitySupportState.Supported, playlist.SupportState);
        Assert.Equal(ProviderAccountRequirement.Required, playlist.AccountRequirement);
        Assert.Same(adapter, validated.Implementations.Single());
        Assert.DoesNotContain(validated.Descriptor.Capabilities,
            capability => capability.Capability is ProviderCapabilityKind.Streaming or ProviderCapabilityKind.Download);

        var services = new ServiceCollection();
        services.AddSpotifyPlaylistCapability();
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IProviderAccountSecretAccessor) &&
                                                descriptor.ImplementationType == typeof(EncryptedProviderAccountSecretAccessor));
    }

    private static ProviderExecutionContext Context(bool includeSecretReference = true)
    {
        var tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var user = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var account = new ProviderAccountContext(
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            "spotify",
            ProviderAccountScope.User,
            1,
            tenantId: tenant,
            ownerUserId: user,
            secretReferenceId: includeSecretReference ? Guid.Parse("44444444-4444-4444-4444-444444444444") : null);
        return new(
            new ProviderActorContext(tenant, ProviderActorKind.User, user,
                new ProviderBackendPrincipal("jellyfin", "backend", "principal")),
            "spotify",
            account,
            null,
            new ProviderExecutionPolicy(
                new ProviderQualityPolicy(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true),
                ProviderExplicitContentPolicy.Allow,
                true, true, false, ["spotify"]),
            "playlist-read",
            "correlation",
            DateTimeOffset.UtcNow.AddMinutes(1),
            default);
    }

    private sealed class FakeSecretAccessor(string value) : IProviderAccountSecretAccessor
    {
        public List<Guid> AccountIds { get; } = [];

        public async Task<T> UseAsync<T>(ProviderAccountContext account, Func<ReadOnlyMemory<byte>, Task<T>> operation, CancellationToken cancellationToken)
        {
            AccountIds.Add(account.AccountId);
            var bytes = Encoding.UTF8.GetBytes(value);
            try { return await operation(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
    }

    private sealed class SpotifyFakeHandler : HttpMessageHandler
    {
        public HttpStatusCode TokenStatus { get; set; } = HttpStatusCode.OK;
        public HttpStatusCode? ApiStatus { get; set; }
        public string? CookieHeader { get; private set; }
        public List<string> ApiAuthorizationHeaders { get; } = [];
        public List<string> Paths { get; } = [];
        public byte[] ArtworkBytes { get; set; } = [1, 2, 3, 4];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.PathAndQuery);
            if (request.RequestUri.Host == "i.scdn.co")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(ArtworkBytes)
                    {
                        Headers = { ContentType = new("image/jpeg") }
                    }
                });
            if (request.RequestUri.Host == "open.spotify.com")
            {
                CookieHeader = request.Headers.GetValues("Cookie").Single();
                return Task.FromResult(Json(TokenStatus, new { accessToken = "account-access-token" }));
            }

            ApiAuthorizationHeaders.Add(request.Headers.Authorization!.ToString());
            if (ApiStatus != null)
            {
                var failure = Json(ApiStatus.Value, new { error = "upstream-secret-body" });
                if (ApiStatus == HttpStatusCode.TooManyRequests)
                    failure.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(12));
                return Task.FromResult(failure);
            }
            var path = request.RequestUri.AbsolutePath;
            if (path.EndsWith("/tracks", StringComparison.Ordinal))
                return Task.FromResult(Json(HttpStatusCode.OK, new
                {
                    items = new[]
                    {
                        new { track = Track("track-a", "First") },
                        new { track = Track("track-a", "Duplicate") }
                    },
                    next = "https://api.spotify.com/next",
                    total = 8
                }));
            if (path.StartsWith("/v1/playlists/", StringComparison.Ordinal))
            {
                var response = Json(HttpStatusCode.OK, Playlist());
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"etag-9\"");
                return Task.FromResult(response);
            }
            if (path == "/v1/me/playlists")
                return Task.FromResult(Json(HttpStatusCode.OK, new { items = new[] { Playlist() }, next = "https://api.spotify.com/next", total = 10 }));
            if (path == "/v1/search")
                return Task.FromResult(Json(HttpStatusCode.OK, new { playlists = new { items = new[] { Playlist() }, next = "https://api.spotify.com/next", total = 10 } }));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static object Playlist() => new
        {
            id = "playlist-opaque",
            name = "Road Mix",
            description = "Description",
            owner = new { id = "owner", display_name = "Owner" },
            images = new[] { new { url = "https://i.scdn.co/signed?token=secret" } },
            snapshot_id = "snapshot-9",
            tracks = new { total = 8 }
        };

        private static object Track(string id, string name) => new
        {
            id,
            name,
            album = new { id = "album", name = "Album" },
            artists = new[] { new { id = "artist", name = "Artist" } },
            duration_ms = 180000,
            @explicit = false,
            external_ids = new { isrc = "USABC1234567" }
        };

        private static HttpResponseMessage Json(HttpStatusCode status, object value) => new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
        };
    }
}

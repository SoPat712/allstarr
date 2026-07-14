using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.AppleMusicKit;
using allstarr.Core.Providers.Spotify;
using allstarr.Core.Storage;

namespace allstarr.Tests;

public sealed class AppleMusicKitPlaylistCapabilityAdapterTests
{
    [Fact]
    public async Task Selected_user_account_preserves_source_order_duplicates_and_stable_artwork()
    {
        var handler = new AppleHandler();
        var secrets = new SecretAccessor(new("developer-secret", "user-secret"));
        var adapter = new AppleMusicKitPlaylistCapabilityAdapter(new HttpClient(handler), secrets);
        var playlist = new ProviderExternalResourceId("apple-musickit", ProviderResourceKind.Playlist, "p.opaque");

        var result = await adapter.GetPlaylistTracksAsync(Context(), new(playlist, new ProviderPageRequest(2, "4")));

        Assert.True(result.IsSuccess);
        var page = result.RequireValue();
        Assert.Equal("2026-01-01T00:00:00Z", page.Playlist.SourceRevision);
        Assert.Equal("A description", page.Playlist.Description);
        Assert.Equal(playlist, page.Playlist.Artwork!.ResourceId);
        Assert.Null(page.Playlist.Artwork.PublicUri);
        Assert.Equal([4, 5], page.Tracks.Items.Select(item => item.Position));
        Assert.Equal(["song.1", "song.1"], page.Tracks.Items.Select(item => item.TrackId.Value));
        Assert.Equal("6", page.Tracks.NextCursor);
        Assert.Equal(Context().Account!.AccountId, Assert.Single(secrets.AccountIds));
        Assert.All(handler.Authorization, value => Assert.Equal("Bearer developer-secret", value));
        Assert.All(handler.UserTokens, value => Assert.Equal("user-secret", value));
        Assert.Contains(handler.Paths, path => path.Contains("limit=2&offset=4", StringComparison.Ordinal));

        var serialized = JsonSerializer.Serialize(page);
        Assert.DoesNotContain("developer-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("user-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("cdn.example", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Paging_is_deterministic_and_search_is_explicitly_not_advertised()
    {
        var handler = new AppleHandler();
        var adapter = new AppleMusicKitPlaylistCapabilityAdapter(new HttpClient(handler),
            new SecretAccessor(new("developer", "user")));

        var playlists = await adapter.GetUserPlaylistsAsync(Context(), new(new ProviderPageRequest(1, "7")));
        var unsupported = await adapter.SearchPlaylistsAsync(Context(), new("mix", new ProviderPageRequest()));

        Assert.True(playlists.IsSuccess);
        Assert.Equal("8", playlists.RequireValue().NextCursor);
        Assert.Equal(ProviderErrorKind.PermanentFailure, unsupported.Error!.Kind);
        var registration = ProviderRegistrationValidator.Validate(AppleMusicKitPlaylistCapabilityAdapter.CreateRegistration(adapter));
        var capability = Assert.Single(registration.Descriptor.Capabilities);
        Assert.Equal(["getPlaylistTracks", "getUserPlaylists", "resolveArtwork"], capability.Hooks);
        Assert.Equal([ProviderAccountScope.User], capability.AllowedAccountScopes);
        Assert.Same(adapter, Assert.Single(registration.Implementations));
        Assert.DoesNotContain(handler.Paths, path => path.Contains("stream", StringComparison.OrdinalIgnoreCase) || path.Contains("download", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Missing_malformed_or_non_user_credentials_never_fall_back()
    {
        var handler = new AppleHandler();
        var valid = new AppleMusicKitPlaylistCapabilityAdapter(new HttpClient(handler), new SecretAccessor(new("d", "u")));
        var playlist = new ProviderExternalResourceId("apple-musickit", ProviderResourceKind.Playlist, "p");
        Assert.Equal(ProviderErrorKind.AccountNeedsConfiguration,
            (await valid.GetPlaylistTracksAsync(Context(includeSecret: false), new(playlist, new()))).Error!.Kind);
        Assert.Equal(ProviderErrorKind.AccountNeedsConfiguration,
            (await valid.GetPlaylistTracksAsync(Context(scope: ProviderAccountScope.Global), new(playlist, new()))).Error!.Kind);

        var malformed = new AppleMusicKitPlaylistCapabilityAdapter(new HttpClient(handler), new RawSecretAccessor("not-json"));
        Assert.Equal(ProviderErrorKind.AccountNeedsConfiguration,
            (await malformed.GetPlaylistTracksAsync(Context(), new(playlist, new()))).Error!.Kind);
        Assert.Empty(handler.Paths);
    }

    [Fact]
    public async Task Artwork_template_is_resolved_with_selected_user_credentials_and_bytes_are_bounded()
    {
        var handler = new AppleHandler();
        var adapter = new AppleMusicKitPlaylistCapabilityAdapter(new HttpClient(handler),
            new SecretAccessor(new("developer", "user")));
        var reference = new ProviderArtworkReference(
            new ProviderExternalResourceId("apple-musickit", ProviderResourceKind.Playlist, "p.opaque"),
            revision: "2026-01-01T00:00:00Z");

        var result = await adapter.ResolveArtworkAsync(Context(), new(reference, 16));

        Assert.True(result.IsSuccess);
        Assert.Equal("image/png", result.RequireValue().ContentType);
        Assert.Equal([5, 6, 7], result.RequireValue().Bytes);
        Assert.Contains("/1024x1024.jpg", handler.Paths);
        Assert.DoesNotContain("developer", JsonSerializer.Serialize(result.RequireValue()), StringComparison.Ordinal);
        Assert.DoesNotContain("user", JsonSerializer.Serialize(result.RequireValue()), StringComparison.Ordinal);

        handler.ArtworkBytes = new byte[17];
        var oversized = await adapter.ResolveArtworkAsync(Context(), new(reference, 16));
        Assert.Equal(ProviderErrorKind.PermanentFailure, oversized.Error!.Kind);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ProviderErrorKind.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, ProviderErrorKind.Forbidden)]
    [InlineData(HttpStatusCode.BadRequest, ProviderErrorKind.PermanentFailure)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ProviderErrorKind.TransientFailure)]
    [InlineData(HttpStatusCode.TooManyRequests, ProviderErrorKind.RateLimited)]
    public async Task Failures_are_typed_without_leaking_provider_bodies(HttpStatusCode status, ProviderErrorKind expected)
    {
        var handler = new AppleHandler { Failure = status };
        var adapter = new AppleMusicKitPlaylistCapabilityAdapter(new HttpClient(handler), new SecretAccessor(new("d", "u")));
        var result = await adapter.GetUserPlaylistsAsync(Context(), new(new ProviderPageRequest()));
        Assert.Equal(expected, result.Error!.Kind);
        if (status == HttpStatusCode.TooManyRequests) Assert.Equal(TimeSpan.FromSeconds(17), result.Error.RetryAfter);
        Assert.DoesNotContain("secret-body", result.Error.ToString(), StringComparison.Ordinal);
    }

    private static ProviderExecutionContext Context(bool includeSecret = true, ProviderAccountScope scope = ProviderAccountScope.User)
    {
        var tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var user = Guid.Parse("22222222-2222-2222-2222-222222222222");
        return new(new ProviderActorContext(tenant, ProviderActorKind.User, user, new("jellyfin", "backend", "principal")),
            "apple-musickit", new(Guid.Parse("33333333-3333-3333-3333-333333333333"), "apple-musickit", scope, 1,
                tenantId: scope == ProviderAccountScope.Global ? null : tenant,
                ownerUserId: scope == ProviderAccountScope.User ? user : null,
                secretReferenceId: includeSecret ? Guid.Parse("44444444-4444-4444-4444-444444444444") : null), null,
            new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true), ProviderExplicitContentPolicy.Allow,
                true, true, false, ["apple-musickit"]), "playlist-read", "correlation", DateTimeOffset.UtcNow.AddMinutes(1), default);
    }

    private sealed class SecretAccessor(AppleMusicKitPlaylistCapabilityAdapter.Credential credential) : IProviderAccountSecretAccessor
    {
        public List<Guid> AccountIds { get; } = [];
        public Task<T> UseAsync<T>(ProviderAccountContext account, Func<ReadOnlyMemory<byte>, Task<T>> operation, CancellationToken cancellationToken)
        {
            AccountIds.Add(account.AccountId);
            return Use(operation, JsonSerializer.Serialize(credential));
        }
    }

    private sealed class RawSecretAccessor(string value) : IProviderAccountSecretAccessor
    {
        public Task<T> UseAsync<T>(ProviderAccountContext account, Func<ReadOnlyMemory<byte>, Task<T>> operation, CancellationToken cancellationToken) => Use(operation, value);
    }

    private static async Task<T> Use<T>(Func<ReadOnlyMemory<byte>, Task<T>> operation, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        try { return await operation(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private sealed class AppleHandler : HttpMessageHandler
    {
        public HttpStatusCode? Failure { get; set; }
        public List<string> Paths { get; } = [];
        public List<string> Authorization { get; } = [];
        public List<string> UserTokens { get; } = [];
        public byte[] ArtworkBytes { get; set; } = [5, 6, 7];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.PathAndQuery);
            if (request.RequestUri.Host == "is1-ssl.mzstatic.com")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(ArtworkBytes)
                    {
                        Headers = { ContentType = new("image/png") }
                    }
                });
            Authorization.Add(request.Headers.Authorization!.ToString());
            UserTokens.Add(request.Headers.GetValues("Music-User-Token").Single());
            if (Failure is { } failure)
            {
                var response = Json(failure, new { errors = new[] { new { detail = "secret-body" } } });
                if (failure == HttpStatusCode.TooManyRequests) response.Headers.RetryAfter = new(TimeSpan.FromSeconds(17));
                return Task.FromResult(response);
            }
            if (request.RequestUri.AbsolutePath.EndsWith("/tracks", StringComparison.Ordinal))
                return Task.FromResult(Json(HttpStatusCode.OK, new { data = new[] { Song("song.1", "First"), Song("song.1", "Duplicate") }, next = "/next" }));
            return Task.FromResult(Json(HttpStatusCode.OK, new { data = new[] { Playlist() }, next = "/next" }));
        }
        private static object Playlist() => new
        {
            id = "p.opaque",
            type = "library-playlists",
            attributes = new { name = "Road Mix", description = "A description", lastModifiedDate = "2026-01-01T00:00:00Z", artwork = new { url = "https://is1-ssl.mzstatic.com/{w}x{h}.jpg" } },
            relationships = new { tracks = new { meta = new { total = 20 } } }
        };
        private static object Song(string id, string name) => new
        {
            id,
            type = "library-songs",
            attributes = new { name, artistName = "Artist", albumName = "Album", durationInMillis = 123000, isrc = "USABC1234567", contentRating = "explicit" }
        };
        private static HttpResponseMessage Json(HttpStatusCode status, object value) => new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
        };
    }
}

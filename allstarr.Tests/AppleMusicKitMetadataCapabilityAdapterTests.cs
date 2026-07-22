using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.AppleMusicKit;
using allstarr.Core.Providers.Spotify;
using allstarr.Core.Storage;

namespace allstarr.Tests;

public sealed class AppleMusicKitMetadataCapabilityAdapterTests
{
    [Fact]
    public async Task Personal_library_search_and_lookups_map_all_entity_types_and_page_deterministically()
    {
        var handler = new AppleMetadataHandler();
        var secrets = new SecretAccessor(new("developer-token", "music-user-token"));
        var adapter = new AppleMusicKitMetadataCapabilityAdapter(new HttpClient(handler), secrets);
        var search = new ProviderMetadataSearchRequest("road mix", new(2, "4"));

        var tracks = await adapter.SearchTracksAsync(Context(), search);
        var albums = await adapter.SearchAlbumsAsync(Context(), search);
        var artists = await adapter.SearchArtistsAsync(Context(), search);
        var track = await adapter.GetTrackAsync(Context(), new(Id(ProviderResourceKind.Track, "i.song"), "\"revision-1\""));
        var album = await adapter.GetAlbumAsync(Context(), new(Id(ProviderResourceKind.Album, "i.album")));
        var artist = await adapter.GetArtistAsync(Context(), new(Id(ProviderResourceKind.Artist, "i.artist")));

        Assert.True(tracks.IsSuccess, tracks.Error?.ToString());
        Assert.True(albums.IsSuccess, albums.Error?.ToString());
        Assert.True(artists.IsSuccess, artists.Error?.ToString());
        Assert.True(track.IsSuccess, track.Error?.ToString());
        Assert.True(album.IsSuccess, album.Error?.ToString());
        Assert.True(artist.IsSuccess, artist.Error?.ToString());
        Assert.Equal("6", tracks.RequireValue().NextCursor);
        Assert.Equal("6", albums.RequireValue().NextCursor);
        Assert.Equal("6", artists.RequireValue().NextCursor);
        Assert.True(tracks.RequireValue().IsPartial);
        Assert.Equal("Library Song", track.RequireValue().Title);
        Assert.Equal("Library Album", album.RequireValue().Title);
        Assert.Equal(12, album.RequireValue().TrackCount);
        Assert.Equal("Library Artist", artist.RequireValue().Name);
        Assert.Equal("https://is1-ssl.mzstatic.com/image/thumb/library/1024x1024bb.jpg",
            track.RequireValue().Artwork?.PublicUri?.ToString());
        Assert.Equal("https://is1-ssl.mzstatic.com/image/thumb/library/1024x1024bb.jpg",
            album.RequireValue().Artwork?.PublicUri?.ToString());
        Assert.Equal("\"revision-1\"", track.RequireValue().SnapshotVersion);
        Assert.Equal(6, secrets.AccountIds.Count);
        Assert.All(handler.Authorization, value => Assert.Equal("Bearer developer-token", value));
        Assert.All(handler.UserTokens, value => Assert.Equal("music-user-token", value));
        Assert.Contains(handler.Paths, value => value.Contains("term=road%20mix&types=library-songs&limit=2&offset=4", StringComparison.Ordinal));
        Assert.Contains(handler.Paths, value => value == "/v1/me/library/albums/i.album");
        Assert.DoesNotContain("developer-token", JsonSerializer.Serialize(track.RequireValue()), StringComparison.Ordinal);
        Assert.DoesNotContain("music-user-token", JsonSerializer.Serialize(track.RequireValue()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Combined_registration_advertises_one_account_scoped_provider_with_both_capabilities()
    {
        var secrets = new SecretAccessor(new("developer", "user"));
        var playlist = new AppleMusicKitPlaylistCapabilityAdapter(new HttpClient(new AppleMetadataHandler()), secrets);
        var metadata = new AppleMusicKitMetadataCapabilityAdapter(new HttpClient(new AppleMetadataHandler()), secrets);

        var registration = ProviderRegistrationValidator.Validate(
            AppleMusicKitPlaylistCapabilityAdapter.CreateRegistration(playlist, metadata));

        Assert.Equal("apple-musickit", registration.Descriptor.Id);
        Assert.Equal([ProviderCapabilityKind.Metadata, ProviderCapabilityKind.Playlist],
            registration.Descriptor.Capabilities.Select(item => item.Capability));
        Assert.All(registration.Descriptor.Capabilities,
            item => Assert.Equal([ProviderAccountScope.User], item.AllowedAccountScopes));
        Assert.Equal(2, registration.Implementations.Count);
        Assert.Same(metadata, registration.Implementations[0]);
        Assert.Same(playlist, registration.Implementations[1]);
    }

    [Fact]
    public async Task Unauthorized_malformed_and_invalid_cursor_fail_with_typed_errors()
    {
        var unauthorizedHandler = new AppleMetadataHandler { Failure = HttpStatusCode.Unauthorized };
        var unauthorized = new AppleMusicKitMetadataCapabilityAdapter(
            new HttpClient(unauthorizedHandler), new SecretAccessor(new("d", "u")));
        var authenticationError = (await unauthorized.SearchTracksAsync(Context(), Search())).Error!;
        Assert.Equal(ProviderErrorKind.AccountNeedsReauthentication, authenticationError.Kind);
        Assert.Equal("account-needs-reauthentication", authenticationError.Code);
        Assert.Contains("Reconnect", authenticationError.SafeMessage, StringComparison.Ordinal);

        var malformedHandler = new AppleMetadataHandler { Malformed = true };
        var malformed = new AppleMusicKitMetadataCapabilityAdapter(
            new HttpClient(malformedHandler), new SecretAccessor(new("d", "u")));
        Assert.Equal(ProviderErrorKind.PermanentFailure,
            (await malformed.SearchTracksAsync(Context(), Search())).Error!.Kind);
        Assert.Equal(ProviderErrorKind.PermanentFailure,
            (await malformed.GetTrackAsync(Context(), new(Id(ProviderResourceKind.Track, "i.song")))).Error!.Kind);

        var handler = new AppleMetadataHandler();
        var adapter = new AppleMusicKitMetadataCapabilityAdapter(
            new HttpClient(handler), new SecretAccessor(new("d", "u")));
        Assert.Equal(ProviderErrorKind.PermanentFailure,
            (await adapter.SearchTracksAsync(Context(), new("query", new(5, "not-an-offset")))).Error!.Kind);
        Assert.Equal(ProviderErrorKind.NotSupported,
            (await adapter.LookupByIsrcAsync(Context(), new("USABC1234567"))).Error!.Kind);
        Assert.Empty(handler.Paths);
    }

    [Fact]
    public async Task Cross_user_account_and_non_Apple_final_origin_are_rejected_without_leaking_credentials()
    {
        var handler = new AppleMetadataHandler();
        var secrets = new SecretAccessor(new("developer-secret", "user-secret"));
        var adapter = new AppleMusicKitMetadataCapabilityAdapter(new HttpClient(handler), secrets);

        Assert.Throws<UnauthorizedAccessException>(() => Context(crossUser: true));
        Assert.Empty(secrets.AccountIds);
        Assert.Empty(handler.Paths);

        handler.FinalOrigin = new("https://attacker.example/redirected");
        var redirected = await adapter.SearchTracksAsync(Context(), Search());
        Assert.Equal(ProviderErrorKind.Forbidden, redirected.Error!.Kind);
        Assert.DoesNotContain("developer-secret", redirected.Error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("user-secret", redirected.Error.ToString(), StringComparison.Ordinal);
    }

    private static ProviderMetadataSearchRequest Search() => new("query", new());
    private static ProviderExternalResourceId Id(ProviderResourceKind kind, string value) =>
        new("apple-musickit", kind, value);

    private static ProviderExecutionContext Context(bool crossUser = false)
    {
        var tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var user = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var owner = crossUser ? Guid.Parse("99999999-9999-9999-9999-999999999999") : user;
        return new(new ProviderActorContext(tenant, ProviderActorKind.User, user,
                new("jellyfin", "backend", "principal")),
            "apple-musickit",
            new(Guid.Parse("33333333-3333-3333-3333-333333333333"), "apple-musickit",
                ProviderAccountScope.User, 1, tenantId: tenant, ownerUserId: owner,
                secretReferenceId: Guid.Parse("44444444-4444-4444-4444-444444444444")),
            null,
            new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true),
                ProviderExplicitContentPolicy.Allow, true, true, false, ["apple-musickit"]),
            "metadata-read", "correlation", DateTimeOffset.UtcNow.AddMinutes(1), default);
    }

    private sealed class SecretAccessor(AppleMusicKitPlaylistCapabilityAdapter.Credential credential)
        : IProviderAccountSecretAccessor
    {
        public List<Guid> AccountIds { get; } = [];

        public Task<T> UseAsync<T>(ProviderAccountContext account,
            Func<ReadOnlyMemory<byte>, Task<T>> operation, CancellationToken cancellationToken)
        {
            AccountIds.Add(account.AccountId);
            return Use(operation, JsonSerializer.Serialize(credential));
        }
    }

    private static async Task<T> Use<T>(Func<ReadOnlyMemory<byte>, Task<T>> operation, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        try { return await operation(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private sealed class AppleMetadataHandler : HttpMessageHandler
    {
        public HttpStatusCode? Failure { get; set; }
        public bool Malformed { get; set; }
        public Uri? FinalOrigin { get; set; }
        public List<string> Paths { get; } = [];
        public List<string> Authorization { get; } = [];
        public List<string> UserTokens { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.PathAndQuery);
            Authorization.Add(request.Headers.Authorization!.ToString());
            UserTokens.Add(request.Headers.GetValues("Music-User-Token").Single());

            HttpResponseMessage response;
            if (Failure is { } failure)
                response = Json(failure, new { errors = new[] { new { detail = "private-provider-body" } } });
            else if (Malformed)
                response = Json(HttpStatusCode.OK, new { results = new { library_songs = new { data = new[] { new { id = "broken" } } } } });
            else if (request.RequestUri.AbsolutePath.EndsWith("/search", StringComparison.Ordinal))
            {
                var type = QueryValue(request.RequestUri.Query, "types");
                response = Json(HttpStatusCode.OK, new
                {
                    results = new Dictionary<string, object>
                    {
                        [type] = new { data = new[] { Entity(type, "1"), Entity(type, "2") }, next = "/next" }
                    }
                });
            }
            else
            {
                var type = request.RequestUri.AbsolutePath.Contains("/albums/", StringComparison.Ordinal)
                    ? "library-albums"
                    : request.RequestUri.AbsolutePath.Contains("/artists/", StringComparison.Ordinal)
                        ? "library-artists"
                        : "library-songs";
                response = Json(HttpStatusCode.OK, new
                {
                    data = new[] { Entity(type, type switch
                {
                    "library-albums" => "i.album",
                    "library-artists" => "i.artist",
                    _ => "i.song"
                }) }
                });
            }

            response.Headers.ETag = new EntityTagHeaderValue("\"revision-1\"");
            if (FinalOrigin != null) response.RequestMessage = new(HttpMethod.Get, FinalOrigin);
            return Task.FromResult(response);
        }

        private static object Entity(string type, string id) => type switch
        {
            "library-albums" => new
            {
                id,
                type,
                attributes = new
                {
                    name = "Library Album",
                    artistName = "Library Artist",
                    trackCount = 12,
                    artwork = new { url = "https://is1-ssl.mzstatic.com/image/thumb/library/{w}x{h}bb.jpg" }
                }
            },
            "library-artists" => new
            {
                id,
                type,
                attributes = new { name = "Library Artist" }
            },
            _ => new
            {
                id,
                type,
                attributes = new
                {
                    name = "Library Song",
                    artistName = "Library Artist",
                    albumName = "Library Album",
                    albumId = "i.album",
                    durationInMillis = 180000,
                    isrc = "USABC1234567",
                    contentRating = "clean",
                    artwork = new { url = "https://is1-ssl.mzstatic.com/image/thumb/library/{w}x{h}bb.jpg" }
                }
            }
        };

        private static string QueryValue(string query, string key) => query.TrimStart('?').Split('&')
            .Select(item => item.Split('=', 2))
            .Single(item => Uri.UnescapeDataString(item[0]) == key)
            .Select(Uri.UnescapeDataString)
            .Last();

        private static HttpResponseMessage Json(HttpStatusCode status, object value) => new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
        };
    }
}

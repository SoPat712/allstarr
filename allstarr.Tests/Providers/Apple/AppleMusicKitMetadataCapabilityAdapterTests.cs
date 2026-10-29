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
    public void Track_mapping_retains_returned_credits_descriptions_and_largest_declared_artwork()
    {
        using var payload = JsonDocument.Parse("""
            {"id":"101","attributes":{"name":"Song","artistName":"Artist","albumName":"Album",
              "durationInMillis":123456,"trackNumber":3,"discNumber":2,"contentRating":"clean",
              "composerName":"Composer","genreNames":["Pop","Electronic"],"copyright":"Copyright",
              "editorialNotes":{"standard":"Recording notes"},"audioVariants":["hi-res-lossless"],
              "artwork":{"url":"https://is1-ssl.mzstatic.com/cover/{w}x{h}bb.jpg","width":3000,"height":2000}},
             "relationships":{"artists":{"data":[
               {"id":"artist-1"},
               {"id":"artist-2","attributes":{"name":"Guest"}}]},
               "albums":{"data":[{"id":"album-1","attributes":{"artistName":"Album Artist","trackCount":12,
                 "discCount":2,"upc":"0123456789012","recordLabel":"Label","releaseDate":"2026-01-02"}}]}}}
            """);

        var track = AppleMusicKitMetadataCapabilityAdapter.MapTrack(payload.RootElement, null);

        Assert.Equal(["Artist", "Guest"], track.Artists.Select(item => item.Name));
        Assert.Equal("artist-2", track.Artists[1].ArtistId!.Value);
        Assert.Equal("Album Artist", track.AlbumArtist);
        Assert.Equal("Composer", track.Composer);
        Assert.Equal(["Pop", "Electronic"], track.Genres);
        Assert.Equal("Pop", track.Genre);
        Assert.Equal("Copyright", track.Copyright);
        Assert.Equal("Label", track.Label);
        Assert.Equal("Recording notes", track.Notes);
        Assert.Equal(12, track.TotalTracks);
        Assert.Equal(2, track.TotalDiscs);
        Assert.Equal("0123456789012", track.Upc);
        Assert.Equal("2026-01-02", track.ReleaseDate);
        Assert.False(track.IsExplicit);
        Assert.Null(track.Media);
        Assert.Null(track.ReplayGain);
        Assert.Equal("https://is1-ssl.mzstatic.com/cover/3000x2000bb.jpg", track.Artwork!.PublicUri!.AbsoluteUri);
    }

    [Theory]
    [InlineData("\"contentRating\":\"explicit\",", true)]
    [InlineData("\"contentRating\":\"clean\",", false)]
    [InlineData("\"contentRating\":null,", null)]
    [InlineData("", null)]
    public async Task Album_search_and_full_lookup_preserve_source_release_evidence(string rating, bool? expectedExplicit)
    {
        var handler = new AlbumEvidenceHandler(rating);
        var adapter = AppleProviderTestFactory.Metadata(
            new HttpClient(handler), new SecretAccessor(new("music-user-token", "us")));

        var search = await adapter.SearchAlbumsAsync(Context(), Search());
        var lookup = await adapter.GetAlbumAsync(Context(), new(Id(ProviderResourceKind.Album, "album-1")));

        var searched = Assert.Single(search.RequireValue().Items);
        var full = lookup.RequireValue();
        foreach (var album in new[] { searched, full })
        {
            Assert.Equal(expectedExplicit, album.IsExplicit);
            Assert.Equal("0123456789012", album.Upc);
            Assert.Equal("Deluxe", album.Edition);
            Assert.Equal("Record label", album.Label);
        }
        Assert.Single(full.Tracks);
        Assert.True(full.Tracks[0].IsExplicit);
    }

    [Theory]
    [InlineData("Album", false, "album", null)]
    [InlineData("Album - Single", false, "single", null)]
    [InlineData("Album - eP", false, "ep", null)]
    [InlineData("Album - Single (Live)", false, "album", null)]
    [InlineData("Album", true, "album", "compilation")]
    public async Task Album_search_and_lookup_preserve_release_type_and_compilation(
        string title, bool compilation, string releaseType, string? exclusionReason)
    {
        var adapter = AppleProviderTestFactory.Metadata(
            new HttpClient(new AlbumEvidenceHandler("", title, compilation)),
            new SecretAccessor(new("music-user-token", "us")));

        var searched = Assert.Single((await adapter.SearchAlbumsAsync(Context(), Search())).RequireValue().Items);
        var full = (await adapter.GetAlbumAsync(Context(), new(Id(ProviderResourceKind.Album, "album-1")))).RequireValue();

        foreach (var album in new[] { searched, full })
        {
            Assert.Equal(releaseType, album.ReleaseType);
            Assert.Equal(exclusionReason, album.ExclusionReason);
        }
    }

    [Fact]
    public async Task Album_type_uses_the_source_single_marker_when_the_title_has_no_suffix()
    {
        var adapter = AppleProviderTestFactory.Metadata(new HttpClient(new AlbumEvidenceHandler("\"isSingle\":true,")),
            new SecretAccessor(new("music-user-token", "us")));

        var album = Assert.Single((await adapter.SearchAlbumsAsync(Context(), Search())).RequireValue().Items);

        Assert.Equal("single", album.ReleaseType);
    }

    private sealed class AlbumEvidenceHandler(string rating, string title = "Album", bool compilation = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var album = $$$"""
                {"id":"album-1","attributes":{"name":{{{JsonSerializer.Serialize(title)}}},"artistName":"Artist",{{{rating}}}
                  "isCompilation":{{{JsonSerializer.Serialize(compilation)}}},
                  "upc":"0123456789012","edition":"Deluxe","trackCount":1,"recordLabel":"Record label"}}
                """;
            var body = request.RequestUri!.AbsolutePath.EndsWith("/search", StringComparison.Ordinal)
                ? "{\"results\":{\"albums\":{\"data\":[" + album + "]}}}"
                : request.RequestUri.AbsolutePath.EndsWith("/tracks", StringComparison.Ordinal)
                    ? """{"data":[{"id":"song-1","attributes":{"name":"Song","artistName":"Artist","contentRating":"explicit"}}]}"""
                    : $$"""{"data":[{{album}}]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    [Fact]
    public async Task Isrc_lookup_uses_authenticated_catalog_song_filter_and_maps_the_recording()
    {
        var handler = new AppleMetadataHandler();
        var secrets = new SecretAccessor(new("music-user-token", "us"));
        var adapter = AppleProviderTestFactory.Metadata(new HttpClient(handler), secrets);

        var result = await adapter.LookupByIsrcAsync(Context(), new("USABC1234567"));

        Assert.True(result.IsSuccess, result.Error?.ToString());
        var track = result.RequireValue();
        Assert.Equal("101", track.Id.Value);
        Assert.Equal("apple-musickit", track.Id.ProviderId);
        Assert.Equal("USABC1234567", track.Isrc);
        Assert.Equal("Library Song", track.Title);
        Assert.Equal("\"revision-1\"", track.SnapshotVersion);
        var uri = Assert.Single(handler.RequestUris);
        Assert.Equal("https://amp-api.music.apple.com", uri.GetLeftPart(UriPartial.Authority));
        Assert.Equal("/v1/catalog/us/songs?filter[isrc]=USABC1234567", Uri.UnescapeDataString(uri.PathAndQuery));
        Assert.Equal(HttpMethod.Get, Assert.Single(handler.Methods));
        Assert.Equal("Bearer " + AppleProviderTestFactory.Bearer, Assert.Single(handler.Authorization));
        Assert.Equal("", Assert.Single(handler.UserTokens));
        Assert.Empty(secrets.AccountIds);
    }

    [Fact]
    public async Task Isrc_lookup_without_a_catalog_match_returns_not_found()
    {
        var handler = new AppleMetadataHandler { EmptyCatalog = true };
        var adapter = AppleProviderTestFactory.Metadata(
            new HttpClient(handler), new SecretAccessor(new("music-user-token", "us")));

        var result = await adapter.LookupByIsrcAsync(Context(), new("USABC1234567"));

        Assert.False(result.IsSuccess);
        Assert.Equal(ProviderErrorKind.NotFound, result.Error!.Kind);
        Assert.Single(handler.Paths);
    }

    [Fact]
    public async Task Public_catalog_search_and_personal_lookups_map_all_entity_types_and_page_deterministically()
    {
        var handler = new AppleMetadataHandler();
        var secrets = new SecretAccessor(new("music-user-token", "us"));
        var adapter = AppleProviderTestFactory.Metadata(new HttpClient(handler), secrets);
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
        Assert.Equal("2024-02-29", album.RequireValue().ReleaseDate);
        Assert.Equal("2024-02-29", albums.RequireValue().Items[0].ReleaseDate);
        Assert.Equal("Library Artist", artist.RequireValue().Name);
        Assert.Equal("https://is1-ssl.mzstatic.com/image/thumb/library/1024x1024bb.jpg",
            track.RequireValue().Artwork?.PublicUri?.ToString());
        Assert.Equal("https://is1-ssl.mzstatic.com/image/thumb/library/1024x1024bb.jpg",
            album.RequireValue().Artwork?.PublicUri?.ToString());
        Assert.Equal("\"revision-1\"", track.RequireValue().SnapshotVersion);
        Assert.Equal(3, secrets.AccountIds.Count);
        Assert.All(handler.Authorization, value => Assert.Equal("Bearer " + AppleProviderTestFactory.Bearer, value));
        Assert.All(handler.UserTokens.Take(3), value => Assert.Equal("", value));
        Assert.All(handler.UserTokens.Skip(3), value => Assert.Equal("music-user-token", value));
        Assert.Contains(handler.Paths, value => value.Contains("term=road%20mix&types=songs&limit=2&offset=4", StringComparison.Ordinal));
        Assert.Contains(handler.Paths, value => value == "/v1/me/library/albums/i.album");
        Assert.DoesNotContain("developer-token", JsonSerializer.Serialize(track.RequireValue()), StringComparison.Ordinal);
        Assert.DoesNotContain("music-user-token", JsonSerializer.Serialize(track.RequireValue()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Combined_registration_advertises_one_account_scoped_provider_with_both_capabilities()
    {
        var secrets = new SecretAccessor(new("user", "us"));
        var playlist = AppleProviderTestFactory.Playlist(new HttpClient(new AppleMetadataHandler()), secrets);
        var metadata = AppleProviderTestFactory.Metadata(new HttpClient(new AppleMetadataHandler()), secrets);

        var registration = ProviderRegistrationValidator.Validate(
            AppleMusicKitPlaylistCapabilityAdapter.CreateRegistration(playlist, metadata));

        Assert.Equal("apple-musickit", registration.Descriptor.Id);
        Assert.Equal([ProviderCapabilityKind.Metadata, ProviderCapabilityKind.Playlist],
            registration.Descriptor.Capabilities.Select(item => item.Capability));
        Assert.All(registration.Descriptor.Capabilities,
            item => Assert.Equal([ProviderAccountScope.Shared, ProviderAccountScope.Personal], item.AllowedAccountScopes));
        Assert.Equal(2, registration.Implementations.Count);
        Assert.Same(metadata, registration.Implementations[0]);
        Assert.Same(playlist, registration.Implementations[1]);
    }

    [Fact]
    public async Task Unauthorized_malformed_and_invalid_cursor_fail_with_typed_errors()
    {
        var unauthorizedHandler = new AppleMetadataHandler { Failure = HttpStatusCode.Unauthorized };
        var unauthorized = AppleProviderTestFactory.Metadata(
            new HttpClient(unauthorizedHandler), new SecretAccessor(new("u", "us")));
        var authenticationError = (await unauthorized.GetTrackAsync(Context(), new(Id(ProviderResourceKind.Track, "i.song")))).Error!;
        Assert.Equal(ProviderErrorKind.AccountNeedsReauthentication, authenticationError.Kind);
        Assert.Equal("account-needs-reauthentication", authenticationError.Code);
        Assert.Contains("Reconnect", authenticationError.SafeMessage, StringComparison.Ordinal);

        var malformedHandler = new AppleMetadataHandler { Malformed = true };
        var malformed = AppleProviderTestFactory.Metadata(
            new HttpClient(malformedHandler), new SecretAccessor(new("u", "us")));
        Assert.Equal(ProviderErrorKind.PermanentFailure,
            (await malformed.SearchTracksAsync(Context(), Search())).Error!.Kind);
        Assert.Equal(ProviderErrorKind.PermanentFailure,
            (await malformed.GetTrackAsync(Context(), new(Id(ProviderResourceKind.Track, "i.song")))).Error!.Kind);

        var handler = new AppleMetadataHandler();
        var adapter = AppleProviderTestFactory.Metadata(
            new HttpClient(handler), new SecretAccessor(new("u", "us")));
        Assert.Equal(ProviderErrorKind.PermanentFailure,
            (await adapter.SearchTracksAsync(Context(), new("query", new(5, "not-an-offset")))).Error!.Kind);
        Assert.Empty(handler.Paths);
    }

    [Fact]
    public async Task Cross_user_account_and_non_Apple_final_origin_are_rejected_without_leaking_credentials()
    {
        var handler = new AppleMetadataHandler();
        var secrets = new SecretAccessor(new("user-secret", "us"));
        var adapter = AppleProviderTestFactory.Metadata(new HttpClient(handler), secrets);

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
        var user = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var owner = crossUser ? Guid.Parse("99999999-9999-9999-9999-999999999999") : user;
        return new(new ProviderActorContext(ProviderActorKind.User, user,
                new("jellyfin", "backend", "principal")),
            "apple-musickit",
            new(Guid.Parse("33333333-3333-3333-3333-333333333333"), "apple-musickit",
                ProviderAccountScope.Personal, 1, ownerUserId: owner,
                secretReferenceId: Guid.Parse("44444444-4444-4444-4444-444444444444")),
            new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true),
                ProviderExplicitContentPolicy.Allow, true, true, false, ["apple-musickit"]),
            "metadata-read", "correlation", DateTimeOffset.UtcNow.AddMinutes(1), default);
    }

    private sealed class SecretAccessor(AppleMusicCredential credential)
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
        public bool EmptyCatalog { get; set; }
        public Uri? FinalOrigin { get; set; }
        public List<string> Paths { get; } = [];
        public List<Uri> RequestUris { get; } = [];
        public List<HttpMethod> Methods { get; } = [];
        public List<string> Authorization { get; } = [];
        public List<string> UserTokens { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.PathAndQuery);
            RequestUris.Add(request.RequestUri);
            Methods.Add(request.Method);
            Authorization.Add(request.Headers.Authorization!.ToString());
            UserTokens.Add(request.Headers.TryGetValues("Cookie", out var cookies) ? cookies.Single().Replace("media-user-token=", "") : "");

            HttpResponseMessage response;
            if (Failure is { } failure)
                response = Json(failure, new { errors = new[] { new { detail = "private-provider-body" } } });
            else if (Malformed)
                response = Json(HttpStatusCode.OK, new { results = new { library_songs = new { data = new[] { new { id = "broken" } } } } });
            else if (request.RequestUri.AbsolutePath.EndsWith("/songs", StringComparison.Ordinal))
                response = Json(HttpStatusCode.OK, new
                {
                    data = EmptyCatalog ? Array.Empty<object>() : new[] { Entity("songs", "101") }
                });
            else if (request.RequestUri.AbsolutePath.EndsWith("/tracks", StringComparison.Ordinal))
                response = Json(HttpStatusCode.OK, new { data = new[] { Entity("songs", "i.song") } });
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
            "library-albums" or "albums" => new
            {
                id,
                type,
                attributes = new
                {
                    name = "Library Album",
                    artistName = "Library Artist",
                    trackCount = 12,
                    releaseDate = "2024-02-29",
                    artwork = new { url = "https://is1-ssl.mzstatic.com/image/thumb/library/{w}x{h}bb.jpg" }
                }
            },
            "library-artists" or "artists" => new
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

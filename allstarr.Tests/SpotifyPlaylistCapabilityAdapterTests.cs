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
        Assert.Null(page.Playlist.SourceETag);
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
        Assert.Contains(handler.Paths, path => path.Contains("\"offset\":5", StringComparison.Ordinal));
        Assert.Contains(handler.Paths, path => path.Contains("\"offset\":8", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Paths, path => path.Contains("stream", StringComparison.OrdinalIgnoreCase) || path.Contains("download", StringComparison.OrdinalIgnoreCase));
        Assert.All(handler.ApiPaths, path => Assert.Contains("api-partner.spotify.com/pathfinder", path, StringComparison.Ordinal));
    }

    [Fact]
    public async Task User_library_requests_all_playlists_without_the_spotify_owned_filter()
    {
        var handler = new SpotifyFakeHandler();
        var adapter = new SpotifyPlaylistCapabilityAdapter(new HttpClient(handler), new FakeSecretAccessor("cookie"));

        var outcome = await adapter.GetUserPlaylistsAsync(Context(), new(new ProviderPageRequest(30)));

        Assert.True(outcome.IsSuccess);
        var request = Assert.Single(handler.Paths, path =>
            path.Contains("operationName=libraryV3", StringComparison.Ordinal));
        Assert.Contains("\"filters\":[\"Playlists\"]", request, StringComparison.Ordinal);
        Assert.DoesNotContain("By Spotify", request, StringComparison.Ordinal);
        Assert.DoesNotContain("\"flatten\"", request, StringComparison.Ordinal);
        Assert.DoesNotContain("withCuration", request, StringComparison.Ordinal);
        Assert.Contains("50650f72ea32a99b5b46240bee22fea83024eec302478a9a75cfd05a0814ba99",
            request, StringComparison.Ordinal);
    }

    [Fact]
    public async Task User_library_accepts_direct_playlist_items_and_string_counts()
    {
        var handler = new SpotifyFakeHandler { ReturnAlternativeLibraryEnvelope = true };
        var adapter = new SpotifyPlaylistCapabilityAdapter(
            new HttpClient(handler),
            new FakeSecretAccessor("cookie"));

        var outcome = await adapter.GetUserPlaylistsAsync(
            Context(),
            new(new ProviderPageRequest(100)));

        Assert.True(outcome.IsSuccess);
        var playlist = Assert.Single(outcome.RequireValue().Items);
        Assert.Equal("playlist-direct", playlist.Id.Value);
        Assert.Equal("Direct Mix", playlist.Name);
        Assert.Equal("Nested description", playlist.Description);
        Assert.Equal(12, playlist.TrackCount);
        Assert.Null(outcome.RequireValue().NextCursor);
        Assert.NotNull(playlist.Artwork);
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
        Assert.Equal(3, handler.Paths.Count);
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

        handler.UseRetryAfterDate = true;
        var limitedByDate = await adapter.GetPlaylistTracksAsync(Context(), new(playlist, new ProviderPageRequest()));
        Assert.Equal(ProviderErrorKind.RateLimited, limitedByDate.Error!.Kind);
        Assert.InRange(limitedByDate.Error.RetryAfter!.Value.TotalSeconds, 10, 13);

        handler.ApiStatus = null;
        var conflict = await adapter.GetPlaylistTracksAsync(Context(), new(playlist, new ProviderPageRequest(), "old-snapshot"));
        Assert.Equal(ProviderErrorKind.PermanentFailure, conflict.Error!.Kind);
        Assert.DoesNotContain("upstream-secret-body", conflict.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stale_pathfinder_query_hash_is_not_reported_as_an_empty_playlist()
    {
        var handler = new SpotifyFakeHandler { ReturnPersistedQueryError = true };
        var adapter = new SpotifyPlaylistCapabilityAdapter(new HttpClient(handler), new FakeSecretAccessor("cookie"));

        var outcome = await adapter.GetUserPlaylistsAsync(Context(), new(new ProviderPageRequest()));

        Assert.False(outcome.IsSuccess);
        Assert.Equal(ProviderErrorKind.CapabilityUnavailable, outcome.Error!.Kind);
        Assert.Equal("capability-unavailable", outcome.Error.Code);
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
    public async Task Discovery_artwork_is_reused_by_the_authenticated_proxy_without_a_second_graphql_query()
    {
        var handler = new SpotifyFakeHandler();
        var adapter = new SpotifyPlaylistCapabilityAdapter(new HttpClient(handler), new FakeSecretAccessor("cookie"));
        var discovery = await adapter.GetUserPlaylistsAsync(Context(), new(new ProviderPageRequest()));
        var artworkReference = discovery.RequireValue().Items.Single().Artwork!;
        var graphQlRequestsBeforeArtwork = handler.ApiPaths.Count(path =>
            path.Contains("api-partner.spotify.com/pathfinder", StringComparison.Ordinal));

        var artwork = await adapter.ResolveArtworkAsync(
            Context(),
            new ProviderPlaylistArtworkRequest(artworkReference, maximumBytes: 16));

        Assert.True(artwork.IsSuccess);
        Assert.Equal(graphQlRequestsBeforeArtwork, handler.ApiPaths.Count(path =>
            path.Contains("api-partner.spotify.com/pathfinder", StringComparison.Ordinal)));
        Assert.Contains(handler.Paths, path => path == "/signed?token=secret");
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
        public List<string> ApiPaths { get; } = [];
        public byte[] ArtworkBytes { get; set; } = [1, 2, 3, 4];
        public bool UseRetryAfterDate { get; set; }
        public bool ReturnPersistedQueryError { get; set; }
        public bool ReturnAlternativeLibraryEnvelope { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.PathAndQuery);
            if (request.RequestUri.Host == "raw.githubusercontent.com")
                return Task.FromResult(Json(HttpStatusCode.OK, new[] { new { version = 1, secret = Enumerable.Range(1, 32).ToArray() } }));
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
                if (request.Method == HttpMethod.Head)
                {
                    var time = new HttpResponseMessage(HttpStatusCode.OK);
                    time.Headers.Date = DateTimeOffset.UtcNow;
                    return Task.FromResult(time);
                }
                CookieHeader = request.Headers.GetValues("Cookie").Single();
                return Task.FromResult(Json(TokenStatus, new { accessToken = "account-access-token" }));
            }

            ApiAuthorizationHeaders.Add(request.Headers.Authorization!.ToString());
            ApiPaths.Add($"{request.RequestUri.Scheme}://{request.RequestUri.Host}{request.RequestUri.AbsolutePath}");
            if (ApiStatus != null)
            {
                var failure = Json(ApiStatus.Value, new { error = "upstream-secret-body" });
                if (ApiStatus == HttpStatusCode.TooManyRequests)
                    failure.Headers.RetryAfter = UseRetryAfterDate
                        ? new System.Net.Http.Headers.RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(12))
                        : new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(12));
                return Task.FromResult(failure);
            }
            if (ReturnPersistedQueryError)
                return Task.FromResult(Json(HttpStatusCode.OK, new
                {
                    errors = new[]
                    {
                        new
                        {
                            message = "PersistedQueryNotFound",
                            extensions = new { code = "PERSISTED_QUERY_NOT_FOUND" }
                        }
                    }
                }));

            var decoded = Uri.UnescapeDataString(request.RequestUri.Query);
            Paths[^1] = $"{request.RequestUri.AbsolutePath}{decoded}";
            if (decoded.Contains("operationName=fetchPlaylist", StringComparison.Ordinal))
                return Task.FromResult(Json(HttpStatusCode.OK, PlaylistResponse()));
            if (decoded.Contains("operationName=libraryV3", StringComparison.Ordinal))
                return Task.FromResult(Json(
                    HttpStatusCode.OK,
                    ReturnAlternativeLibraryEnvelope
                        ? AlternativeLibraryResponse()
                        : LibraryResponse()));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static object PlaylistData() => new
        {
            name = "Road Mix",
            description = "Description",
            ownerV2 = new { data = new { username = "owner", name = "Owner" } },
            images = new
            {
                items = new[]
                {
                    new
                    {
                        sources = new[]
                        {
                            new { url = "https://i.scdn.co/signed?token=secret", width = 640 }
                        }
                    }
                }
            },
            revisionId = "snapshot-9",
            attributes = new[] { new { key = "core:item_count", value = "8" } }
        };

        private static object LibraryResponse() => new
        {
            data = new
            {
                me = new
                {
                    libraryV3 = new
                    {
                        totalCount = 10,
                        items = new[]
                        {
                            new
                            {
                                item = new
                                {
                                    uri = "spotify:playlist:playlist-opaque",
                                    data = PlaylistData()
                                }
                            }
                        }
                    }
                }
            }
        };

        private static object AlternativeLibraryResponse() => new
        {
            data = new
            {
                me = new
                {
                    libraryV3 = new
                    {
                        totalCount = "1",
                        items = new[]
                        {
                            new
                            {
                                item = new
                                {
                                    uri = "spotify:playlist:playlist-direct",
                                    name = "Direct Mix",
                                    description = new { text = "Nested description" },
                                    ownerV2 = new { data = new { username = "owner", name = "Owner" } },
                                    images = new
                                    {
                                        items = new[]
                                        {
                                            new
                                            {
                                                sources = new[]
                                                {
                                                    new { url = "https://i.scdn.co/direct.jpg", width = 640 }
                                                }
                                            }
                                        }
                                    },
                                    revisionId = "snapshot-direct",
                                    attributes = new[]
                                    {
                                        new { key = "core:item_count", value = "12" }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };

        private static object PlaylistResponse() => new
        {
            data = new
            {
                playlistV2 = new
                {
                    name = "Road Mix",
                    description = "Description",
                    ownerV2 = new { data = new { username = "owner", name = "Owner" } },
                    images = new
                    {
                        items = new[]
                        {
                            new
                            {
                                sources = new[]
                                {
                                    new { url = "https://i.scdn.co/signed?token=secret", width = 640 }
                                }
                            }
                        }
                    },
                    revisionId = "snapshot-9",
                    content = new
                    {
                        totalCount = 8,
                        items = new[]
                        {
                            Track("track-a", "First"),
                            Track("track-a", "Duplicate")
                        }
                    }
                }
            }
        };

        private static object Track(string id, string name) => new
        {
            addedAt = new { isoString = "2026-02-16T05:00:00Z" },
            itemV2 = new
            {
                data = new
                {
                    uri = $"spotify:track:{id}",
                    name,
                    artists = new
                    {
                        items = new[]
                        {
                            new
                            {
                                uri = "spotify:artist:artist",
                                profile = new { name = "Artist" }
                            }
                        }
                    },
                    albumOfTrack = new
                    {
                        uri = "spotify:album:album",
                        name = "Album",
                        coverArt = new
                        {
                            sources = new[]
                            {
                                new { url = "https://i.scdn.co/album.jpg", width = 640 }
                            }
                        }
                    },
                    trackDuration = new { totalMilliseconds = 180000 },
                    contentRating = new { label = "NONE" }
                }
            }
        };

        private static HttpResponseMessage Json(HttpStatusCode status, object value) => new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
        };
    }
}

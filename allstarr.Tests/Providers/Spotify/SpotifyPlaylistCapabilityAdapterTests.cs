using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Spotify;
using allstarr.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace allstarr.Tests;

public sealed partial class SpotifyPlaylistCapabilityAdapterTests
{
    [Fact]
    public async Task LargePublicProfilesHaveBoundedTransportAndAnHonestPartialResult()
    {
        var handler = new SpotifyFakeHandler { TokenIsAnonymous = true, ProfileCount = 1001, GenerateProfilePlaylists = true };
        var result = (await new SpotifyPlaylistCapabilityAdapter(new HttpClient(handler), new FakeSecretAccessor("unused"))
            .GetUserPlaylistsAsync(PublicContext(), new(new ProviderPageRequest(200, "800"), "profile-owner"))).RequireValue();
        Assert.Equal(200, result.Items.Count);
        Assert.Equal("playlist-800", result.Items[0].Id.Value);
        Assert.Null(result.NextCursor);
        Assert.True(result.IsPartial);
        Assert.Equal([1, 1000], handler.ProfileLimits);
    }

    [Fact]
    public async Task OversizedPublicProfileResponseFailsSafely()
    {
        var handler = new SpotifyFakeHandler
        {
            TokenIsAnonymous = true,
            ProfilePayload = "{\"total_public_playlists_count\":0,\"public_playlists\":[],\"extra\":\"" + new string('x', 4 * 1024 * 1024) + "\"}"
        };
        var result = await new SpotifyPlaylistCapabilityAdapter(new HttpClient(handler), new FakeSecretAccessor("unused"))
            .GetUserPlaylistsAsync(PublicContext(), new(new ProviderPageRequest(), "profile-owner"));
        Assert.False(result.IsSuccess);
        Assert.Equal("provider-contract-changed", result.Error!.Code);
        Assert.Equal([1], handler.ProfileLimits);
    }

    [Fact]
    public async Task PublicProfilePagesReuseOnlyPublicMetadataAndNeverBorrowAnAccount()
    {
        var handler = new SpotifyFakeHandler { TokenIsAnonymous = true };
        var secrets = new FakeSecretAccessor("unused");
        var adapter = new SpotifyPlaylistCapabilityAdapter(new HttpClient(handler), secrets, cache: new TestMemoryApplicationCache());
        var first = (await adapter.GetUserPlaylistsAsync(PublicContext(), new(new ProviderPageRequest(2), "profile-owner"))).RequireValue();
        var second = (await adapter.GetUserPlaylistsAsync(PublicContext(), new(new ProviderPageRequest(2, first.NextCursor), "profile-owner"))).RequireValue();
        Assert.Equal(["first", "second"], first.Items.Select(item => item.Id.Value));
        Assert.Equal("2", first.NextCursor);
        Assert.True(first.IsPartial);
        Assert.Equal("third", Assert.Single(second.Items).Id.Value);
        Assert.Null(second.NextCursor);
        Assert.Equal([1, 4], handler.ProfileLimits);
        Assert.False((await adapter.GetUserPlaylistsAsync(Context(), new(new ProviderPageRequest(), "profile-owner"))).IsSuccess);
        Assert.Empty(secrets.AccountIds);
        Assert.Null(handler.CookieHeader);
    }

    [Theory]
    [InlineData("profile-owner")]
    [InlineData("spotify:user:profile-owner")]
    [InlineData("https://open.spotify.com/user/profile-owner?si=ignored")]
    public async Task PublicUserProfileDiscoveryUsesAnonymousScopeAndStableOrderedPages(string sourceUser)
    {
        var handler = new SpotifyFakeHandler { TokenIsAnonymous = true };
        var secrets = new FakeSecretAccessor("must-not-be-used");
        var adapter = new SpotifyPlaylistCapabilityAdapter(new HttpClient(handler), secrets);
        var result = await adapter.GetUserPlaylistsAsync(PublicContext(), new(new ProviderPageRequest(2, "1"), sourceUser));
        Assert.True(result.IsSuccess, result.Error?.ToString());
        var page = result.RequireValue();
        Assert.Equal(["second", "third"], page.Items.Select(item => item.Id.Value));
        Assert.Equal(["Second", "Third"], page.Items.Select(item => item.Name));
        Assert.Equal(["profile-owner", "other-owner"], page.Items.Select(item => item.Owner.ProviderUserId));
        Assert.Null(page.NextCursor);
        Assert.False(page.IsPartial);
        Assert.Equal([1, 4], handler.ProfileLimits);
        Assert.Empty(secrets.AccountIds);
        Assert.Null(handler.CookieHeader);
        Assert.All(handler.ApiPaths, path => Assert.Equal("https://spclient.wg.spotify.com/user-profile-view/v3/profile/profile-owner", path));
        Assert.Empty(handler.MutationRequests);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"total_public_playlists_count\":-1,\"public_playlists\":[]}")]
    [InlineData("{\"total_public_playlists_count\":1}")]
    [InlineData("{\"total_public_playlists_count\":1,\"public_playlists\":[{\"uri\":\"spotify:playlist:first\",\"name\":\"Missing owner\"}]}")]
    public async Task PublicUserProfileShapeFailuresReturnSafeCompatibilityErrors(string payload)
    {
        var handler = new SpotifyFakeHandler { TokenIsAnonymous = true, ProfilePayload = payload };
        var outcome = await new SpotifyPlaylistCapabilityAdapter(new HttpClient(handler), new FakeSecretAccessor("unused"))
            .GetUserPlaylistsAsync(PublicContext(), new(new ProviderPageRequest(), "profile-owner"));
        Assert.False(outcome.IsSuccess);
        Assert.Equal("provider-contract-changed", outcome.Error!.Code);
        Assert.DoesNotContain(payload, outcome.Error.SafeMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://open.spotify.com.evil.test/user/profile-owner")]
    [InlineData("https://open.spotify.com/user/owner/extra")]
    [InlineData("spotify:playlist:wrong-resource")]
    [InlineData("../owner")]
    public async Task InvalidPublicUserReferenceFailsBeforeAnyTokenRequest(string reference)
    {
        var handler = new SpotifyFakeHandler { TokenIsAnonymous = true };
        var outcome = await new SpotifyPlaylistCapabilityAdapter(new HttpClient(handler), new FakeSecretAccessor("unused"))
            .GetUserPlaylistsAsync(PublicContext(), new(new ProviderPageRequest(), reference));
        Assert.False(outcome.IsSuccess);
        Assert.Empty(handler.Paths);
    }

    [Fact]
    public async Task PublicPlaylistReadUsesAnAnonymousTokenWithoutCookiesOrAccountSecrets()
    {
        var handler = new SpotifyFakeHandler { TokenIsAnonymous = true };
        var secrets = new FakeSecretAccessor("must-not-be-used");
        var adapter = new SpotifyPlaylistCapabilityAdapter(new HttpClient(handler), secrets);
        var outcome = await adapter.GetPlaylistTracksAsync(PublicContext(), new(
            new("spotify", ProviderResourceKind.Playlist, "playlist-opaque"), new ProviderPageRequest(2)));
        Assert.True(outcome.IsSuccess);
        Assert.Empty(secrets.AccountIds);
        Assert.Null(handler.CookieHeader);
        Assert.NotEmpty(handler.ApiAuthorizationHeaders);
        Assert.Empty(handler.MutationRequests);
    }

    [Fact]
    public async Task PublicTokenExchangeRejectsAnUnexpectedAuthenticatedSession()
    {
        var handler = new SpotifyFakeHandler { TokenIsAnonymous = false };
        var secrets = new FakeSecretAccessor("must-not-be-used");
        var outcome = await new SpotifyPlaylistCapabilityAdapter(new HttpClient(handler), secrets)
            .GetPlaylistTracksAsync(PublicContext(), new(new("spotify", ProviderResourceKind.Playlist, "playlist-opaque"), new ProviderPageRequest()));
        Assert.False(outcome.IsSuccess);
        Assert.Empty(handler.ApiPaths);
        Assert.Null(handler.CookieHeader);
        Assert.Empty(secrets.AccountIds);
    }

    [Fact]
    public async Task PublicReadsCannotAccessThePersonalLibraryLikedSongsOrMutations()
    {
        var handler = new SpotifyFakeHandler { TokenIsAnonymous = true };
        var secrets = new FakeSecretAccessor("must-not-be-used");
        var adapter = new SpotifyPlaylistCapabilityAdapter(new HttpClient(handler), secrets);
        var context = PublicContext();
        Assert.False((await adapter.GetUserPlaylistsAsync(context, new(new ProviderPageRequest()))).IsSuccess);
        Assert.False((await adapter.GetPlaylistTracksAsync(context, new(
            new("spotify", ProviderResourceKind.Playlist, "liked-songs"), new ProviderPageRequest()))).IsSuccess);
        Assert.False((await adapter.MutatePlaylistAsync(context, new("spotify", "Public mutation", [], ProviderPlaylistConflictBehavior.FailIfChanged))).IsSuccess);
        Assert.Empty(handler.Paths);
        Assert.Empty(secrets.AccountIds);
    }

    private static ProviderExecutionContext PublicContext() => new(
        new ProviderActorContext(ProviderActorKind.PublicRead, null), "spotify", null, Context().Policy,
        "public-playlist-read", "correlation", DateTimeOffset.UtcNow.AddMinutes(1), default);

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
        Assert.All(page.Tracks.Items, track =>
            Assert.Equal("https://i.scdn.co/album.jpg", track.Metadata?.Artwork?.PublicUri?.AbsoluteUri));
        Assert.Equal("4", page.Tracks.NextCursor);
        Assert.Equal("snapshot-9", page.Tracks.SnapshotVersion);
        Assert.Equal(context.Account!.AccountId, secrets.AccountIds.Single());
        Assert.Equal("sp_dc=account-cookie-secret", handler.CookieHeader);
        Assert.All(handler.ApiAuthorizationHeaders, value => Assert.Equal("Bearer account-access-token", value));
        Assert.All(handler.WebPlayerProfiles, profile =>
        {
            Assert.Equal(SpotifyWebRequestProfile.UserAgent, profile.UserAgent);
            Assert.Equal(SpotifyWebRequestProfile.AppPlatform, profile.AppPlatform);
            Assert.Equal(SpotifyWebRequestProfile.AppVersion, profile.AppVersion);
        });

        var serialized = JsonSerializer.Serialize(page);
        Assert.DoesNotContain("account-cookie-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("account-access-token", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("image-cdn", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LikedSongsReadsOnlyTheSelectedEncryptedAccountAndRejectsAnonymousTokens()
    {
        var handler = new SpotifyFakeHandler();
        var secrets = new FakeSecretAccessor("selected-cookie");
        var adapter = new SpotifyPlaylistCapabilityAdapter(new HttpClient(handler), secrets);
        var context = Context();
        var request = new ProviderPlaylistTracksRequest(new("spotify", ProviderResourceKind.Playlist, "liked-songs"), new ProviderPageRequest(2));
        var outcome = await adapter.GetPlaylistTracksAsync(context, request);
        Assert.True(outcome.IsSuccess);
        Assert.Equal("Liked Songs", outcome.RequireValue().Playlist.Name);
        Assert.Equal(context.Account!.AccountId, Assert.Single(secrets.AccountIds));
        Assert.Equal("sp_dc=selected-cookie", handler.CookieHeader);
        Assert.Contains(handler.Paths, path => path.Contains("operationName=fetchLibraryTracks", StringComparison.Ordinal));
        Assert.Empty(handler.MutationRequests);

        var anonymous = new SpotifyFakeHandler { TokenIsAnonymous = true };
        var denied = await new SpotifyPlaylistCapabilityAdapter(new HttpClient(anonymous), new FakeSecretAccessor("cookie"))
            .GetPlaylistTracksAsync(Context(), request);
        Assert.False(denied.IsSuccess);
        Assert.Equal(ProviderErrorKind.Unauthorized, denied.Error!.Kind);
        Assert.Empty(anonymous.ApiPaths);
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
        var match = Assert.Single(search.RequireValue().Items);
        Assert.Equal("Road Mix", match.Name);
        Assert.Equal("playlist-opaque", match.Id.Value);
        Assert.Equal(match.Id, match.Artwork!.ResourceId);
        Assert.Null(match.Artwork.PublicUri);
        Assert.Contains(handler.Paths, path => path.Contains("\"offset\":5", StringComparison.Ordinal));
        Assert.Contains(handler.Paths, path => path.Contains("\"offset\":8", StringComparison.Ordinal));
        Assert.Contains(handler.Paths, path => path.Contains("\"textFilter\":\"road\"", StringComparison.Ordinal));
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
        Assert.Contains("\"flatten\":true", request, StringComparison.Ordinal);
        Assert.Contains("\"includeFoldersWhenFlattening\":true", request, StringComparison.Ordinal);
        Assert.Contains("\"withCuration\":true", request, StringComparison.Ordinal);
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
    public async Task Anonymous_expired_and_forbidden_token_exchanges_fail_without_pathfinder_fallback()
    {
        var handler = new SpotifyFakeHandler { TokenIsAnonymous = true };
        var adapter = new SpotifyPlaylistCapabilityAdapter(
            new HttpClient(handler),
            new FakeSecretAccessor("cookie"));

        var anonymous = await adapter.GetUserPlaylistsAsync(Context(), new(new ProviderPageRequest()));
        Assert.Equal(ProviderErrorKind.Unauthorized, anonymous.Error!.Kind);
        Assert.Empty(handler.ApiPaths);

        handler.TokenIsAnonymous = false;
        handler.TokenExpirationTimestampMs = 0;
        var expired = await adapter.GetUserPlaylistsAsync(Context(), new(new ProviderPageRequest()));
        Assert.Equal(ProviderErrorKind.Unauthorized, expired.Error!.Kind);
        Assert.Empty(handler.ApiPaths);

        handler.TokenExpirationTimestampMs = null;
        handler.TokenStatus = HttpStatusCode.Forbidden;
        var forbidden = await adapter.GetUserPlaylistsAsync(Context(), new(new ProviderPageRequest()));
        Assert.Equal(ProviderErrorKind.Forbidden, forbidden.Error!.Kind);
        Assert.Empty(handler.ApiPaths);
    }

    [Fact]
    public async Task Incompatible_raw_track_slot_fails_closed_instead_of_publishing_a_partial_page()
    {
        var handler = new SpotifyFakeHandler { ReturnMalformedTrack = true };
        var adapter = new SpotifyPlaylistCapabilityAdapter(
            new HttpClient(handler),
            new FakeSecretAccessor("cookie"));
        var playlist = new ProviderExternalResourceId(
            "spotify",
            ProviderResourceKind.Playlist,
            "playlist-opaque");

        var outcome = await adapter.GetPlaylistTracksAsync(
            Context(),
            new(playlist, new ProviderPageRequest(2, "2")));

        Assert.False(outcome.IsSuccess);
        Assert.Equal("provider-contract-changed", outcome.Error!.Code);
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
        Assert.Equal("provider-contract-changed", outcome.Error.Code);
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
        var adapter = new SpotifyPlaylistCapabilityAdapter(new HttpClient(handler), new FakeSecretAccessor("cookie"), cache: new TestMemoryApplicationCache());
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
    public async Task Mutation_creates_and_writes_every_track_in_order_across_provider_batches()
    {
        var handler = new SpotifyFakeHandler();
        var adapter = new SpotifyPlaylistCapabilityAdapter(
            new HttpClient(handler),
            new FakeSecretAccessor("cookie"));
        Assert.True(adapter.MutationSupport.CanCreate);
        Assert.True(adapter.MutationSupport.CanReplaceExisting);
        var trackIds = Enumerable.Range(0, 102)
            .Select(index => new ProviderExternalResourceId(
                "spotify",
                ProviderResourceKind.Track,
                index == 101 ? "track-50" : $"track-{index}"))
            .ToArray();

        var outcome = await adapter.MutatePlaylistAsync(
            Context(),
            new ProviderPlaylistMutationRequest(
                "spotify",
                "Road trip",
                trackIds,
                ProviderPlaylistConflictBehavior.Reconcile,
                description: "Drive"));

        Assert.True(outcome.IsSuccess, outcome.Error?.ToString());
        var receipt = outcome.RequireValue();
        Assert.True(receipt.Applied);
        Assert.Equal("created-playlist", receipt.PlaylistId.Value);
        Assert.Equal("snapshot-3", receipt.Revision);
        Assert.Equal(102, receipt.TrackCount);
        Assert.Equal([
            "POST /v1/me/playlists",
            "PUT /v1/playlists/created-playlist/items",
            "POST /v1/playlists/created-playlist/items"
        ], handler.MutationRequests);
        var written = handler.MutationBodies.Skip(1).SelectMany(body =>
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.GetProperty("uris").EnumerateArray()
                .Select(item => item.GetString()).ToArray();
        }).ToArray();
        Assert.Equal(trackIds.Select(item => $"spotify:track:{item.Value}"), written);
        Assert.All(handler.ApiAuthorizationHeaders, value =>
            Assert.Equal("Bearer account-access-token", value));
    }

    [Fact]
    public async Task Mutation_returns_no_op_when_revision_metadata_and_duplicate_order_match()
    {
        var handler = new SpotifyFakeHandler { PlaylistTotalCount = 2 };
        var adapter = new SpotifyPlaylistCapabilityAdapter(
            new HttpClient(handler),
            new FakeSecretAccessor("cookie"));

        var outcome = await adapter.MutatePlaylistAsync(
            Context(),
            new ProviderPlaylistMutationRequest(
                "spotify",
                "Road Mix",
                [
                    new("spotify", ProviderResourceKind.Track, "track-a"),
                    new("spotify", ProviderResourceKind.Track, "track-a")
                ],
                ProviderPlaylistConflictBehavior.FailIfChanged,
                new("spotify", ProviderResourceKind.Playlist, "playlist-opaque"),
                "snapshot-9",
                "Description"));

        Assert.True(outcome.IsSuccess, outcome.Error?.ToString());
        Assert.False(outcome.RequireValue().Applied);
        Assert.Equal("snapshot-9", outcome.RequireValue().Revision);
        Assert.Empty(handler.MutationRequests);
    }

    [Fact]
    public void Registration_activates_the_operational_typed_capabilities()
    {
        var adapter = new SpotifyPlaylistCapabilityAdapter(new HttpClient(new SpotifyFakeHandler()), new FakeSecretAccessor("cookie"));
        var lyrics = Mock.Of<IProviderLyricsCapability>(item =>
            item.ProviderId == "spotify" && item.Capability == ProviderCapabilityKind.Lyrics);
        var registration = SpotifyPlaylistCapabilityAdapter.CreateRegistration(adapter, lyrics);
        var validated = ProviderRegistrationValidator.Validate(registration);
        var playlist = validated.Descriptor.Capabilities.Single(capability => capability.Capability == ProviderCapabilityKind.Playlist);

        Assert.Equal(ProviderCapabilitySupportState.Supported, playlist.SupportState);
        Assert.Equal(ProviderAccountRequirement.Optional, playlist.AccountRequirement);
        Assert.True(playlist.SupportsPublicRead);
        Assert.Contains("mutatePlaylist", playlist.Hooks);
        Assert.Same(adapter, validated.Implementations.Single(item => item.Capability == ProviderCapabilityKind.Playlist));
        Assert.Same(lyrics, validated.Implementations.Single(item => item.Capability == ProviderCapabilityKind.Lyrics));
        Assert.DoesNotContain(validated.Descriptor.Capabilities,
            capability => capability.Capability is ProviderCapabilityKind.Streaming or ProviderCapabilityKind.Download);

        var services = new ServiceCollection();
        services.AddSpotifyPlaylistCapability();
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IProviderAccountSecretAccessor) &&
                                                descriptor.ImplementationType == typeof(EncryptedProviderAccountSecretAccessor));
    }

    private static ProviderExecutionContext Context(bool includeSecretReference = true)
    {
        var user = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var account = new ProviderAccountContext(
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            "spotify",
            ProviderAccountScope.Personal,
            1,
            ownerUserId: user,
            secretReferenceId: includeSecretReference ? Guid.Parse("44444444-4444-4444-4444-444444444444") : null);
        return new(
            new ProviderActorContext(ProviderActorKind.User, user,
                new ProviderBackendPrincipal("jellyfin", "backend", "principal")),
            "spotify",
            account,
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
        public TaskCompletionSource? TokenRequested { get; set; }
        public Task? TokenRelease { get; set; }
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
        public bool ReturnMalformedTrack { get; set; }
        public string TokenValue { get; set; } = "account-access-token";
        public bool TokenIsAnonymous { get; set; }
        public long? TokenExpirationTimestampMs { get; set; }
        public int PlaylistTotalCount { get; set; } = 8;
        public List<int> ProfileLimits { get; } = [];
        public string? ProfilePayload { get; set; }
        public int ProfileCount { get; set; } = 4;
        public bool GenerateProfilePlaylists { get; set; }
        public List<RequestProfile> WebPlayerProfiles { get; } = [];
        public List<string> MutationRequests { get; } = [];
        public List<string> MutationBodies { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.PathAndQuery);
            if (request.RequestUri.Host is "open.spotify.com" or "api-partner.spotify.com")
            {
                WebPlayerProfiles.Add(new(
                    request.Headers.UserAgent.ToString(),
                    request.Headers.GetValues("app-platform").Single(),
                    request.Headers.GetValues("spotify-app-version").Single()));
            }
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
                CookieHeader = request.Headers.TryGetValues("Cookie", out var cookies) ? cookies.Single() : null;
                TokenRequested?.TrySetResult();
                return TokenResponseAsync(cancellationToken);
            }

            ApiAuthorizationHeaders.Add(request.Headers.Authorization!.ToString());
            ApiPaths.Add($"{request.RequestUri.Scheme}://{request.RequestUri.Host}{request.RequestUri.AbsolutePath}");
            if (request.RequestUri.Host == "api.spotify.com")
            {
                MutationRequests.Add($"{request.Method} {request.RequestUri.AbsolutePath}");
                MutationBodies.Add(request.Content!.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult());
                var snapshot = $"snapshot-{MutationRequests.Count}";
                object responseBody = request.RequestUri.AbsolutePath == "/v1/me/playlists"
                    ? new { id = "created-playlist", snapshot_id = snapshot }
                    : new { snapshot_id = snapshot };
                return Task.FromResult(Json(
                    request.RequestUri.AbsolutePath == "/v1/me/playlists"
                        ? HttpStatusCode.Created
                        : HttpStatusCode.OK,
                    responseBody));
            }
            if (ApiStatus != null)
            {
                var failure = Json(ApiStatus.Value, new { error = "upstream-secret-body" });
                if (ApiStatus == HttpStatusCode.TooManyRequests)
                    failure.Headers.RetryAfter = UseRetryAfterDate
                        ? new System.Net.Http.Headers.RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(12))
                        : new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(12));
                return Task.FromResult(failure);
            }
            if (request.RequestUri.Host == "spclient.wg.spotify.com")
            {
                var limit = int.Parse(request.RequestUri.Query.TrimStart('?').Split('&')
                    .Single(item => item.StartsWith("playlist_limit=", StringComparison.Ordinal)).Split('=')[1]);
                ProfileLimits.Add(limit);
                if (ProfilePayload != null)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent(ProfilePayload, Encoding.UTF8, "application/json") });
                if (GenerateProfilePlaylists)
                    return Task.FromResult(Json(HttpStatusCode.OK, new
                    {
                        total_public_playlists_count = ProfileCount,
                        public_playlists = Enumerable.Range(0, Math.Min(limit, ProfileCount)).Select(index => new
                        { uri = $"spotify:playlist:playlist-{index}", owner_uri = "spotify:user:profile-owner", name = $"Playlist {index}" })
                    }));
                var playlists = new[]
                {
                    new { uri = "spotify:playlist:first", owner_uri = "spotify:user:profile-owner", name = "First", revisionId = "first-revision" },
                    new { uri = "spotify:playlist:second", owner_uri = "spotify:user:profile-owner", name = "Second", revisionId = "second-revision" },
                    new { uri = "spotify:playlist:second", owner_uri = "spotify:user:profile-owner", name = "Second", revisionId = "second-revision" },
                    new { uri = "spotify:playlist:third", owner_uri = "spotify:user:other-owner", name = "Third", revisionId = "third-revision" }
                };
                return Task.FromResult(Json(HttpStatusCode.OK, new { total_public_playlists_count = ProfileCount, public_playlists = playlists.Take(Math.Min(limit, ProfileCount)) }));
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
            if (decoded.Contains("operationName=fetchLibraryTracks", StringComparison.Ordinal))
                return Task.FromResult(Json(HttpStatusCode.OK, new { data = new { me = new { library = new { tracks = new { totalCount = 0, items = System.Array.Empty<object>() } } } } }));
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

        private async Task<HttpResponseMessage> TokenResponseAsync(CancellationToken cancellationToken)
        {
            if (TokenRelease != null) await TokenRelease.WaitAsync(cancellationToken);
            return Json(TokenStatus, new
            {
                accessToken = TokenValue,
                isAnonymous = TokenIsAnonymous,
                accessTokenExpirationTimestampMs = TokenExpirationTimestampMs
            });
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
                                    name = " Direct Mix\u0007 ",
                                    description = new { text = " Nested description\u0007 " },
                                    ownerV2 = new { data = new { username = " owner ", name = " Owner " } },
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
                                    revisionId = $" snapshot-direct-{new string('x', 320)} ",
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

        private object PlaylistResponse() => new
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
                        totalCount = PlaylistTotalCount,
                        items = ReturnMalformedTrack
                            ? new object[] { new { itemV2 = new { } }, Track("track-a", "Duplicate") }
                            : new object[]
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

        public sealed record RequestProfile(
            string UserAgent,
            string AppPlatform,
            string AppVersion);
    }
}

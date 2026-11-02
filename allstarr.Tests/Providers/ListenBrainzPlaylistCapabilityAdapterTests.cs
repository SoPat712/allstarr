using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using allstarr.Core.Capabilities;
using allstarr.Core.Playlists.Sources;
using allstarr.Core.Providers.ListenBrainz;
using allstarr.Core.Providers.Spotify;
using allstarr.Core.Storage;

namespace allstarr.Tests;

public sealed class ListenBrainzPlaylistCapabilityAdapterTests
{
    private const string PlaylistId = "11111111-1111-4111-8111-111111111111";
    private const string NewPlaylistId = "22222222-2222-4222-8222-222222222222";
    private const string RecordingId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private static readonly Guid Listener = Guid.Parse("44444444-4444-4444-8444-444444444444");

    [Fact]
    public void RegistrationOffersPublicReadsAndPreservesExistingAccountAudiences()
    {
        var adapter = new ListenBrainzPlaylistCapabilityAdapter(new HttpClient(new Handler(_ => throw new InvalidOperationException())), new Secrets());
        var registry = new ProviderRegistry([ListenBrainzPlaylistCapabilityAdapter.CreateRegistration(adapter)]);
        var descriptor = registry.GetRequired("listenbrainz").Capabilities.Single();
        Assert.True(descriptor.SupportsPublicRead);
        Assert.Equal(ProviderAccountRequirement.Optional, descriptor.AccountRequirement);
        Assert.Contains(ProviderAccountScope.Shared, allstarr.Core.Identity.ProviderAccountAudiences.Supported(registry, "listenbrainz"));
        Assert.Same(adapter, registry.GetRequiredCapability<IProviderPlaylistCapability>("listenbrainz", ProviderCapabilityKind.Playlist));
    }

    [Fact]
    public async Task MissingOptionalPlaylistFieldsAndNullDurationRemainReadable()
    {
        var root = JsonNode.Parse(Fixture())!;
        var playlist = root["playlist"]!.AsObject();
        playlist.Remove("title"); playlist.Remove("creator"); playlist.Remove("date"); playlist.Remove("extension");
        playlist["track"]![0]!["duration"] = null;
        var adapter = new ListenBrainzPlaylistCapabilityAdapter(new HttpClient(new Handler(_ => Json(root.ToJsonString()))), new Secrets());
        var outcome = await adapter.GetPlaylistTracksAsync(Context(), new(Resource(PlaylistId), new()));
        Assert.True(outcome.IsSuccess);
        Assert.Null(outcome.RequireValue().Playlist.CreatedDate);
        Assert.Null(outcome.RequireValue().Tracks.Items[0].Metadata!.Duration);
    }

    [Fact]
    public async Task PublicJspfPreservesOrderRepeatsMissingFieldsAndRecordingEvidence()
    {
        var handler = new Handler(_ => Json(Fixture()));
        var secrets = new Secrets();
        var adapter = new ListenBrainzPlaylistCapabilityAdapter(new HttpClient(handler), secrets);
        var result = await new ProviderPlaylistSnapshotCollector().CollectAsync(adapter, Context(), new(Resource(PlaylistId), PageSize: 2));

        Assert.True(result.IsSuccess);
        var snapshot = result.Snapshot!;
        Assert.Equal(5, snapshot.Entries.Count);
        Assert.Equal([0, 1, 2, 3, 4], snapshot.Entries.Select(item => item.SourcePosition));
        Assert.Equal(snapshot.Entries[0].ProviderTrackIdHash, snapshot.Entries[2].ProviderTrackIdHash);
        Assert.NotEqual(snapshot.Entries[0].SourceEntryIdHash, snapshot.Entries[2].SourceEntryIdHash);
        Assert.Equal(RecordingId, snapshot.Entries[0].MusicBrainzRecordingId);
        Assert.Null(snapshot.Entries[3].MusicBrainzRecordingId);
        Assert.Null(snapshot.Entries[4].MusicBrainzRecordingId);
        Assert.NotEqual(snapshot.Entries[3].ProviderTrackIdHash, snapshot.Entries[4].ProviderTrackIdHash);
        Assert.Equal("Unknown", snapshot.Entries[4].Title);
        Assert.Equal(180000, snapshot.Entries[0].DurationMilliseconds);
        Assert.Equal("Weekly Jams", snapshot.Name);
        Assert.Contains("troi-bot", snapshot.Description);
        Assert.Contains("weekly-jams", snapshot.Description);
        Assert.Contains("2026-10-19", snapshot.Description);
        Assert.All(handler.Requests, item => Assert.Null(item.Authorization));
        Assert.Empty(secrets.Accounts);
    }

    [Fact]
    public async Task PrivatePlaylistRequiresTheSelectedListenersToken()
    {
        var handler = new Handler(request => request.Headers.Authorization == null
            ? new(HttpStatusCode.NotFound) : Json(Fixture()));
        var secrets = new Secrets();
        var adapter = new ListenBrainzPlaylistCapabilityAdapter(new HttpClient(handler), secrets);
        var denied = await adapter.GetPlaylistTracksAsync(Context(), new(Resource(PlaylistId), new()));
        Assert.Equal(ProviderErrorKind.NotFound, denied.Error!.Kind);
        var personal = Context(personal: true);
        var allowed = await adapter.GetPlaylistTracksAsync(personal, new(Resource(PlaylistId), new()));
        Assert.True(allowed.IsSuccess);
        Assert.Equal([personal.Account!.AccountId], secrets.Accounts);
        Assert.Equal("Token fixture-token", handler.Requests.Last().Authorization);
    }

    [Fact]
    public async Task DiscoveryUsesValidatedTokenIdentityForOwnAndCreatedForPlaylists()
    {
        var handler = new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("validate-token")
            ? Json("{\"valid\":true,\"user_name\":\"listener\"}") : Json(Listing(Fixture())));
        var adapter = new ListenBrainzPlaylistCapabilityAdapter(new HttpClient(handler), new Secrets());
        var result = await adapter.GetUserPlaylistsAsync(Context(personal: true), new(new()));
        Assert.True(result.IsSuccess);
        Assert.NotEmpty(result.RequireValue().Items);
        Assert.Contains(handler.Requests, item => item.Path.Contains("/user/listener/playlists?", StringComparison.Ordinal));
        Assert.Contains(handler.Requests, item => item.Path.Contains("/user/listener/playlists/createdfor?", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, item => item.Path.Contains("untrusted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FollowNewestChangesResolvedSourceOnTheNextCollectionAndKeepsOneKind()
    {
        var newest = false;
        var handler = new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("validate-token")) return Json("{\"valid\":true,\"user_name\":\"listener\"}");
            if (path.EndsWith("createdfor")) return Json(newest
                ? Listing(Newer("weekly-exploration", "33333333-3333-4333-8333-333333333333"), Fixture(), Newer("weekly-jams", NewPlaylistId))
                : Listing(Fixture()));
            return Json(path.EndsWith(NewPlaylistId) ? Newer("weekly-jams", NewPlaylistId) : Fixture());
        });
        var adapter = new ListenBrainzPlaylistCapabilityAdapter(new HttpClient(handler), new Secrets());
        var context = Context(personal: true);
        var follow = await adapter.FollowNewestAsync(context, Resource(PlaylistId));
        Assert.True(follow.IsSuccess);
        Assert.Equal("createdfor:weekly-jams", follow.RequireValue().Value);
        var collector = new ProviderPlaylistSnapshotCollector();
        var first = await collector.CollectAsync(adapter, context, new(follow.RequireValue()));
        newest = true;
        var second = await collector.CollectAsync(adapter, context, new(follow.RequireValue()));
        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.NotEqual(first.Snapshot!.SourceRevision, second.Snapshot!.SourceRevision);
        Assert.Equal(PlaylistId, first.Snapshot.SourceETag);
        Assert.Equal(NewPlaylistId, second.Snapshot.SourceETag);
        Assert.Contains(handler.Requests, item => item.Path.EndsWith("/playlist/" + NewPlaylistId, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrEmptyGeneratedPlaylistIsASuccessfulEmptySnapshotWithReason(bool published)
    {
        var empty = JsonNode.Parse(Fixture())!;
        empty["playlist"]!["track"] = new JsonArray();
        var handler = new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("validate-token")
            ? Json("{\"valid\":true,\"user_name\":\"listener\"}")
            : request.RequestUri.AbsolutePath.EndsWith("createdfor")
                ? Json(published ? Listing(empty.ToJsonString()) : Listing()) : Json(empty.ToJsonString()));
        var adapter = new ListenBrainzPlaylistCapabilityAdapter(new HttpClient(handler), new Secrets());
        var result = await new ProviderPlaylistSnapshotCollector().CollectAsync(adapter, Context(personal: true), new(Resource("createdfor:weekly-jams")));
        Assert.True(result.IsSuccess);
        Assert.Empty(result.Snapshot!.Entries);
        Assert.Contains(published ? "empty" : "not generated", result.Snapshot.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GeneratedPlaylistDisappearingAfterListingReturnsAnEmptySnapshot()
    {
        var handler = new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("validate-token")
            ? Json("{\"valid\":true,\"user_name\":\"listener\"}")
            : request.RequestUri.AbsolutePath.EndsWith("createdfor") ? Json(Listing(Fixture())) : new(HttpStatusCode.NotFound));
        var adapter = new ListenBrainzPlaylistCapabilityAdapter(new HttpClient(handler), new Secrets());
        var result = await new ProviderPlaylistSnapshotCollector().CollectAsync(adapter, Context(personal: true), new(Resource("createdfor:weekly-jams")));
        Assert.True(result.IsSuccess);
        Assert.Empty(result.Snapshot!.Entries);
        Assert.Contains("unavailable", result.Snapshot.Description);
    }

    [Fact]
    public async Task InvalidValidatedTokenStopsBeforePersonalDiscovery()
    {
        var handler = new Handler(_ => Json("{\"valid\":false}"));
        var adapter = new ListenBrainzPlaylistCapabilityAdapter(new HttpClient(handler), new Secrets());
        var outcome = await adapter.GetUserPlaylistsAsync(Context(personal: true), new(new()));
        Assert.Equal(ProviderErrorKind.AccountNeedsReauthentication, outcome.Error!.Kind);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, ProviderErrorKind.RateLimited)]
    [InlineData(HttpStatusCode.Unauthorized, ProviderErrorKind.AccountNeedsReauthentication)]
    [InlineData(HttpStatusCode.Forbidden, ProviderErrorKind.Forbidden)]
    public async Task UpstreamFailuresAreClassifiedWithoutRetrying(HttpStatusCode status, ProviderErrorKind expected)
    {
        var handler = new Handler(_ =>
        {
            var response = new HttpResponseMessage(status);
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(45));
            return response;
        });
        var adapter = new ListenBrainzPlaylistCapabilityAdapter(new HttpClient(handler), new Secrets());
        var result = await adapter.GetPlaylistTracksAsync(Context(personal: true), new(Resource(PlaylistId), new()));
        Assert.Equal(expected, result.Error!.Kind);
        if (status == HttpStatusCode.TooManyRequests) Assert.Equal(TimeSpan.FromSeconds(45), result.Error.RetryAfter);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task SharedAndUnscopedDiscoveryNeverOpenSecretsOrMakeRequests()
    {
        var handler = new Handler(_ => throw new InvalidOperationException());
        var secrets = new Secrets();
        var adapter = new ListenBrainzPlaylistCapabilityAdapter(new HttpClient(handler), secrets);
        Assert.False((await adapter.GetUserPlaylistsAsync(Context(), new(new()))).IsSuccess);
        Assert.False((await adapter.GetUserPlaylistsAsync(Context(shared: true), new(new()))).IsSuccess);
        Assert.False((await adapter.GetPlaylistTracksAsync(Context(shared: true), new(Resource(PlaylistId), new()))).IsSuccess);
        Assert.Empty(handler.Requests);
        Assert.Empty(secrets.Accounts);
        var personal = Context(personal: true);
        var other = new ProviderActorContext(ProviderActorKind.User, Guid.NewGuid(), new("jellyfin", "backend", "other"));
        Assert.Throws<UnauthorizedAccessException>(() => new ProviderExecutionContext(other, "listenbrainz", personal.Account,
            personal.Policy, "playlist", "fixture", DateTimeOffset.UtcNow.AddMinutes(1), default));
    }

    [Theory]
    [InlineData("11111111-1111-4111-8111-111111111111", true)]
    [InlineData("https://listenbrainz.org/playlist/11111111-1111-4111-8111-111111111111", true)]
    [InlineData("https://listenbrainz.org.evil.test/playlist/11111111-1111-4111-8111-111111111111", false)]
    [InlineData("https://listenbrainz.org/playlist/11111111-1111-4111-8111-111111111111/extra", false)]
    public void ReferencesAcceptOnlyPlaylistMbidsAndOwnedUrls(string reference, bool valid) =>
        Assert.Equal(valid, ListenBrainzPlaylistCapabilityAdapter.TryNormalize(reference, out _));

    [Fact]
    public async Task RecommendedFollowsListenBrainzRecommendationsAndWaitsWhenThereAreNone()
    {
        var empty = true;
        var handler = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/1/validate-token" => Json("""{"valid":true,"user_name":"listener"}"""),
            "/1/cf/recommendation/user/listener/recording" => empty ? new HttpResponseMessage(HttpStatusCode.NoContent)
                : Json($$$"""{"payload":{"mbids":[{"recording_mbid":"{{{RecordingId}}}","score":1}]}}"""),
            "/1/metadata/recording/" => Json("{\"" + RecordingId + "\":" + """{"recording":{"name":"Song","length":200000},"artist":{"name":"Artist"},"release":{"name":"Album"}}}"""),
            _ => throw new InvalidOperationException(request.RequestUri.AbsolutePath)
        });
        var adapter = new ListenBrainzPlaylistCapabilityAdapter(new HttpClient(handler), new Secrets());

        var none = (await adapter.GetPlaylistTracksAsync(Context(personal: true), new(Resource("recommended"), new()))).RequireValue();
        empty = false;
        var page = (await adapter.GetPlaylistTracksAsync(Context(personal: true), new(Resource("recommended"), new()))).RequireValue();

        Assert.Empty(none.Tracks.Items);
        var track = Assert.Single(page.Tracks.Items);
        Assert.Equal("Song", track.Metadata!.Title);
        Assert.Equal("Album", track.Metadata.AlbumTitle);
        Assert.Equal(TimeSpan.FromMilliseconds(200000), track.Metadata.Duration);
        Assert.Equal("Recommended for you", page.Playlist.Name);
    }

    [Fact]
    public async Task FreshReleasesIsListedAfterRecommendedAndFollowsItself()
    {
        var handler = new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("validate-token")
            ? Json("{\"valid\":true,\"user_name\":\"listener\"}") : Json(Listing()));
        var adapter = new ListenBrainzPlaylistCapabilityAdapter(new HttpClient(handler), new Secrets());

        var items = (await adapter.GetUserPlaylistsAsync(Context(personal: true), new(new()))).RequireValue().Items;
        var follow = await adapter.FollowNewestAsync(Context(personal: true), Resource("fresh-releases"));

        Assert.Equal(new[] { "recommended", "fresh-releases" }, items.Take(2).Select(item => item.Id.Value));
        Assert.Equal("Fresh releases", items[1].Name);
        Assert.True(ListenBrainzPlaylistCapabilityAdapter.TryNormalize("fresh-releases", out var normalized));
        Assert.Equal("fresh-releases", normalized);
        Assert.Equal("fresh-releases", follow.RequireValue().Value);
    }

    [Fact]
    public async Task FreshReleasesTakesOneSongFromEachRecentReleaseNewestFirst()
    {
        const string album = "55555555-5555-4555-8555-555555555555", single = "66666666-6666-4666-8666-666666666666";
        const string repeat = "77777777-7777-4777-8777-777777777777", unlisted = "88888888-8888-4888-8888-888888888888";
        const string upcoming = "99999999-9999-4999-8999-999999999999", old = "12121212-1212-4212-8212-121212121212";
        const string opener = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb", title = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";
        const string other = "dddddddd-dddd-4ddd-8ddd-dddddddddddd";
        var handler = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/1/validate-token" => Json("""{"valid":true,"user_name":"listener"}"""),
            "/1/user/listener/fresh_releases" => Json(FreshReleaseList(FreshRelease(album, "2026-10-30", "Album One", 1),
                FreshRelease(single, "2026-10-30", "Title Song", 9), FreshRelease(unlisted, "2026-10-20", "Not Listed Yet", 50),
                FreshRelease(repeat, "2026-10-25", "Title Song", 3), FreshRelease(upcoming, "2026-11-09", "Upcoming", 99),
                FreshRelease(old, "2026-09-01", "Old", 99))),
            "/1/metadata/release_group/" => Json(new JsonObject
            {
                [album] = Songs((2, [Song(1, "Disc Two", other)]), (1, [Song(2, "Second", other), Song(1, "Opener", opener)])),
                [single] = Songs((1, [Song(1, "B-side", other), Song(2, "Title Song", title)])),
                [repeat] = Songs((1, [Song(1, "Title Song", title)]))
            }.ToJsonString()),
            _ => throw new InvalidOperationException(request.RequestUri.AbsolutePath)
        });
        var adapter = new ListenBrainzPlaylistCapabilityAdapter(new HttpClient(handler), new Secrets(), new Clock(Today));

        var page = (await adapter.GetPlaylistTracksAsync(Context(personal: true), new(Resource("fresh-releases"), new()))).RequireValue();
        var again = (await adapter.GetPlaylistTracksAsync(Context(personal: true), new(Resource("fresh-releases"), new()))).RequireValue();

        Assert.Equal(new[] { "Title Song", "Opener" }, page.Tracks.Items.Select(item => item.Metadata!.Title));
        Assert.Equal(new[] { 0, 1 }, page.Tracks.Items.Select(item => item.Position));
        var track = page.Tracks.Items[1];
        Assert.Equal(opener, track.TrackId.Value);
        Assert.Equal("Singer & Guest", Assert.Single(track.Metadata!.Artists).Name);
        Assert.Equal("Album One", track.Metadata.AlbumTitle);
        Assert.Equal(TimeSpan.FromMilliseconds(200000), track.Metadata.Duration);
        Assert.Equal(opener, track.Metadata.ProviderIds["musicbrainzrecording"]);
        Assert.Equal("Fresh releases", page.Playlist.Name);
        Assert.Equal(2, page.Playlist.TrackCount);
        Assert.Equal(page.Playlist.SourceRevision, again.Playlist.SourceRevision);
        Assert.Contains(handler.Requests, item => item.Path == "/1/user/listener/fresh_releases?days=30&past=true&future=false");
        var lookup = Uri.UnescapeDataString(handler.Requests.First(item => item.Path.StartsWith("/1/metadata/release_group/", StringComparison.Ordinal)).Path);
        Assert.EndsWith("&inc=recording", lookup);
        Assert.All(new[] { single, album, repeat, unlisted }, group => Assert.Contains(group, lookup));
        Assert.DoesNotContain(upcoming, lookup);
        Assert.DoesNotContain(old, lookup);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("empty")]
    [InlineData("upcoming")]
    [InlineData("unlisted")]
    public async Task FreshReleasesIsEmptyWithAReasonWhenThereIsNothingToPlay(string state)
    {
        const string album = "55555555-5555-4555-8555-555555555555";
        var handler = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/1/validate-token" => Json("""{"valid":true,"user_name":"listener"}"""),
            "/1/user/listener/fresh_releases" => state switch
            {
                "none" => new HttpResponseMessage(HttpStatusCode.NoContent),
                "empty" => Json(FreshReleaseList()),
                _ => Json(FreshReleaseList(FreshRelease(album, state == "upcoming" ? "2026-11-03" : "2026-11-02", "Album One", 1)))
            },
            "/1/metadata/release_group/" => Json("{}"),
            _ => throw new InvalidOperationException(request.RequestUri.AbsolutePath)
        });
        var adapter = new ListenBrainzPlaylistCapabilityAdapter(new HttpClient(handler), new Secrets(), new Clock(Today));

        var page = (await adapter.GetPlaylistTracksAsync(Context(personal: true), new(Resource("fresh-releases"), new()))).RequireValue();

        Assert.Empty(page.Tracks.Items);
        Assert.Equal(0, page.Playlist.TrackCount);
        Assert.Contains(state == "unlisted" ? "doesn't have the songs" : "hasn't found new releases", page.Playlist.Description);
        Assert.Equal(state == "unlisted", handler.Requests.Any(item => item.Path.StartsWith("/1/metadata/", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task FreshReleasesLooksUpAtMostOneHundredReleasesAndKeepsFiftySongs()
    {
        var groups = Enumerable.Range(1, 120).Select(index => $"{index:x8}-1111-4111-8111-111111111111").ToArray();
        var handler = new Handler(request =>
        {
            var metadata = new JsonObject();
            for (var index = 0; index < groups.Length; index++)
                metadata[groups[index]] = Songs((1, [Song(1, "Song " + index, $"{index + 1:x8}-2222-4222-8222-222222222222")]));
            return request.RequestUri!.AbsolutePath switch
            {
                "/1/validate-token" => Json("""{"valid":true,"user_name":"listener"}"""),
                "/1/user/listener/fresh_releases" => Json(FreshReleaseList(groups.Select((group, index) =>
                    FreshRelease(group, "2026-10-20", "Release " + index, index)).ToArray())),
                "/1/metadata/release_group/" => Json(metadata.ToJsonString()),
                _ => throw new InvalidOperationException(request.RequestUri.AbsolutePath)
            };
        });
        var adapter = new ListenBrainzPlaylistCapabilityAdapter(new HttpClient(handler), new Secrets(), new Clock(Today));

        var page = (await adapter.GetPlaylistTracksAsync(Context(personal: true), new(Resource("fresh-releases"), new(200)))).RequireValue();

        Assert.Equal(50, page.Tracks.Items.Count);
        Assert.Equal(50, page.Playlist.TrackCount);
        Assert.Equal("Song 119", page.Tracks.Items[0].Metadata!.Title);
        var lookup = Uri.UnescapeDataString(handler.Requests.Single(item => item.Path.StartsWith("/1/metadata/release_group/", StringComparison.Ordinal)).Path);
        Assert.Equal(100, lookup.Split("release_group_mbids=")[1].Split('&')[0].Split(',').Length);
    }

    private static readonly DateTimeOffset Today = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private static ProviderExternalResourceId Resource(string id) => new("listenbrainz", ProviderResourceKind.Playlist, id);
    private static string Fixture() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ListenBrainz", "playlist.json"));
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private static string Listing(params string[] playlists) => new JsonObject
    {
        ["playlists"] = new JsonArray(playlists.Select(item => JsonNode.Parse(item)).ToArray()),
        ["playlist_count"] = playlists.Length,
        ["count"] = 100,
        ["offset"] = 0
    }.ToJsonString();
    private static string Newer(string kind, string id)
    {
        var root = JsonNode.Parse(Fixture())!;
        root["playlist"]!["identifier"] = "https://listenbrainz.org/playlist/" + id;
        root["playlist"]!["date"] = "2026-10-26T00:00:00+00:00";
        root["playlist"]!["extension"]!["https://musicbrainz.org/doc/jspf#playlist"]!["additional_metadata"]!["algorithm_metadata"]!["source_patch"] = kind;
        return root.ToJsonString();
    }
    private static string FreshReleaseList(params JsonObject[] releases) => new JsonObject
    {
        ["payload"] = new JsonObject { ["releases"] = new JsonArray(releases), ["user_id"] = "listener" }
    }.ToJsonString();
    private static JsonObject FreshRelease(string group, string date, string name, int confidence) => new()
    {
        ["artist_credit_name"] = "Release Artist",
        ["confidence"] = confidence,
        ["release_date"] = date,
        ["release_group_mbid"] = group,
        ["release_group_primary_type"] = "Album",
        ["release_mbid"] = group,
        ["release_name"] = name
    };
    private static JsonObject Songs(params (int Position, JsonObject[] Tracks)[] mediums) => new()
    {
        ["release_group"] = new JsonObject(),
        ["recording"] = new JsonObject
        {
            ["mediums"] = new JsonArray(mediums.Select(medium => (JsonNode?)new JsonObject
            {
                ["position"] = medium.Position,
                ["tracks"] = new JsonArray(medium.Tracks)
            }).ToArray())
        }
    };
    private static JsonObject Song(int position, string name, string recording) => new()
    {
        ["position"] = position,
        ["name"] = name,
        ["recording_mbid"] = recording,
        ["length"] = 200000,
        ["artists"] = new JsonArray(new JsonObject { ["artist_credit_name"] = "Singer", ["join_phrase"] = " & " },
            new JsonObject { ["artist_credit_name"] = "Guest", ["join_phrase"] = "" })
    };
    private static ProviderExecutionContext Context(bool personal = false, bool shared = false)
    {
        var actor = personal || shared ? new ProviderActorContext(ProviderActorKind.User, Listener, new("jellyfin", "backend", "principal"))
            : new ProviderActorContext(ProviderActorKind.PublicRead, null);
        return new(actor, "listenbrainz", personal || shared ? new(Guid.NewGuid(), "listenbrainz",
            shared ? ProviderAccountScope.Shared : ProviderAccountScope.Personal, 1, ownerUserId: shared ? null : Listener) : null,
            new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, false), ProviderExplicitContentPolicy.Allow, false, shared, false),
            "playlist", "fixture", DateTimeOffset.UtcNow.AddMinutes(5), default);
    }
    private sealed class Secrets : IProviderAccountSecretAccessor
    {
        public List<Guid> Accounts { get; } = [];
        public Task<T> UseAsync<T>(ProviderAccountContext account, Func<ReadOnlyMemory<byte>, Task<T>> operation, CancellationToken cancellationToken)
        {
            Accounts.Add(account.AccountId);
            return operation(Encoding.UTF8.GetBytes("{\"token\":\"fixture-token\",\"username\":\"untrusted\"}"));
        }
    }
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Path, string? Authorization)> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.PathAndQuery, request.Headers.Authorization?.ToString()));
            return Task.FromResult(respond(request));
        }
    }
}

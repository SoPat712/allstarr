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

    private static ProviderExternalResourceId Resource(string id) => new("listenbrainz", ProviderResourceKind.Playlist, id);
    private static string Fixture() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ListenBrainz", "playlist.json"));
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private static string Listing(params string[] playlists) => new JsonObject
    {
        ["playlists"] = new JsonArray(playlists.Select(item => JsonNode.Parse(item)).ToArray()),
        ["playlist_count"] = playlists.Length, ["count"] = 100, ["offset"] = 0
    }.ToJsonString();
    private static string Newer(string kind, string id)
    {
        var root = JsonNode.Parse(Fixture())!;
        root["playlist"]!["identifier"] = "https://listenbrainz.org/playlist/" + id;
        root["playlist"]!["date"] = "2026-10-26T00:00:00+00:00";
        root["playlist"]!["extension"]!["https://musicbrainz.org/doc/jspf#playlist"]!["additional_metadata"]!["algorithm_metadata"]!["source_patch"] = kind;
        return root.ToJsonString();
    }
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

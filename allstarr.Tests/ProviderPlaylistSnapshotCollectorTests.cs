using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Playlists.Sources;
using allstarr.Core.Providers;
using allstarr.Core.Storage;

namespace allstarr.Tests;

public sealed class ProviderPlaylistSnapshotCollectorTests
{
    [Fact]
    public async Task Collector_pages_in_order_preserves_duplicates_metadata_revision_etag_and_hashes_opaque_ids()
    {
        var playlistId = PlaylistId("opaque-playlist-secret");
        var summary = Summary(playlistId);
        var capability = new FakePlaylistCapability("spotify",
            Page(summary, [Track(0, "opaque-track-a"), Track(1, "opaque-track-b")], "cursor-2", snapshot: "page-snapshot"),
            Page(summary, [Track(2, "opaque-track-a")], snapshot: "page-snapshot"));
        var context = Context("spotify");

        var result = await new ProviderPlaylistSnapshotCollector().CollectAsync(
            capability, context, new(playlistId, PageSize: 2));

        Assert.Equal(PlaylistSnapshotCollectionStatus.Fresh, result.Status);
        Assert.Equal(2, result.PagesRead);
        var snapshot = result.Snapshot!;
        Assert.Equal(context.Account!.AccountId, snapshot.ProviderAccountId);
        Assert.Equal("revision-17", snapshot.SourceRevision);
        Assert.Equal("etag-17", snapshot.SourceETag);
        Assert.Equal("Road Mix", snapshot.Name);
        Assert.Equal("Source description", snapshot.Description);
        Assert.StartsWith("provider-artwork:", snapshot.ArtworkReferenceKey, StringComparison.Ordinal);
        Assert.Equal([0, 1, 2], snapshot.Entries.Select(entry => entry.SourcePosition));
        Assert.Equal(snapshot.Entries[0].ProviderTrackIdHash, snapshot.Entries[2].ProviderTrackIdHash);
        Assert.NotEqual(snapshot.Entries[0].SourceEntryIdHash, snapshot.Entries[2].SourceEntryIdHash);
        var json = JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain("opaque-playlist-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("opaque-track-a", json, StringComparison.Ordinal);
        Assert.DoesNotContain("opaque-art-secret", json, StringComparison.Ordinal);
        Assert.Equal([null, "cursor-2"], capability.Cursors);
        Assert.Equal([null, "revision-17"], capability.ExpectedRevisions);
        Assert.Equal(0, capability.NonPlaylistCallCount);
    }

    [Fact]
    public async Task Collector_requires_exactly_one_explicit_matching_provider_account()
    {
        var playlist = PlaylistId("playlist");
        var capability = new FakePlaylistCapability("spotify", Page(Summary(playlist), []));

        await Assert.ThrowsAsync<InvalidOperationException>(() => new ProviderPlaylistSnapshotCollector().CollectAsync(
            capability, Context("spotify", includeAccount: false), new(playlist)));
        await Assert.ThrowsAsync<ArgumentException>(() => new ProviderPlaylistSnapshotCollector().CollectAsync(
            capability, Context("qobuz"), new(playlist)));
    }

    [Theory]
    [InlineData(ProviderErrorKind.TransientFailure)]
    [InlineData(ProviderErrorKind.CapabilityUnavailable)]
    public async Task Retryable_failure_returns_only_the_matching_last_known_good_snapshot(ProviderErrorKind errorKind)
    {
        var playlist = PlaylistId("playlist");
        var context = Context("spotify");
        var lastGood = LastGood(context, playlist);
        var capability = new FakePlaylistCapability("spotify", new ProviderError(errorKind));

        var result = await new ProviderPlaylistSnapshotCollector().CollectAsync(
            capability, context, new(playlist, LastKnownGood: lastGood));

        Assert.Equal(PlaylistSnapshotCollectionStatus.LastKnownGood, result.Status);
        Assert.Same(lastGood, result.Snapshot);
        Assert.Equal(errorKind, result.Error!.Kind);
    }

    [Fact]
    public async Task Rate_limit_returns_last_good_but_auth_failure_and_contract_drift_never_return_partial_data()
    {
        var playlist = PlaylistId("playlist");
        var context = Context("spotify");
        var lastGood = LastGood(context, playlist);
        var rateLimited = new FakePlaylistCapability("spotify", new ProviderError(ProviderErrorKind.RateLimited, TimeSpan.FromSeconds(10)));
        var rateResult = await new ProviderPlaylistSnapshotCollector().CollectAsync(rateLimited, context, new(playlist, LastKnownGood: lastGood));
        Assert.Equal(PlaylistSnapshotCollectionStatus.LastKnownGood, rateResult.Status);

        var unauthorized = new FakePlaylistCapability("spotify", new ProviderError(ProviderErrorKind.Unauthorized));
        var authResult = await new ProviderPlaylistSnapshotCollector().CollectAsync(unauthorized, context, new(playlist, LastKnownGood: lastGood));
        Assert.Equal(PlaylistSnapshotCollectionStatus.Failed, authResult.Status);
        Assert.Null(authResult.Snapshot);

        var first = Summary(playlist);
        var changed = new ProviderPlaylistSummary(playlist, "Changed", first.Owner, "other-revision");
        var drift = new FakePlaylistCapability("spotify",
            Page(first, [Track(0, "a")], "next", snapshot: "one"),
            Page(changed, [Track(1, "b")], snapshot: "two"));
        var driftResult = await new ProviderPlaylistSnapshotCollector().CollectAsync(drift, context, new(playlist, LastKnownGood: lastGood));
        Assert.Equal(PlaylistSnapshotCollectionStatus.Failed, driftResult.Status);
        Assert.Equal(ProviderErrorKind.PermanentFailure, driftResult.Error!.Kind);
        Assert.Null(driftResult.Snapshot);
    }

    [Fact]
    public async Task Cancellation_never_substitutes_stale_data()
    {
        var playlist = PlaylistId("playlist");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var context = Context("spotify", cancellationToken: cancellation.Token);
        var capability = new FakePlaylistCapability("spotify", Page(Summary(playlist), []));

        var result = await new ProviderPlaylistSnapshotCollector().CollectAsync(
            capability, context, new(playlist, LastKnownGood: LastGood(context, playlist)));

        Assert.Equal(PlaylistSnapshotCollectionStatus.Failed, result.Status);
        Assert.Equal(ProviderErrorKind.Canceled, result.Error!.Kind);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public async Task Oversized_page_or_declared_playlist_is_rejected_before_unbounded_collection()
    {
        var playlist = PlaylistId("playlist");
        var context = Context("spotify");
        var summary = Summary(playlist);
        var oversizedPage = new FakePlaylistCapability("spotify",
            Page(summary, [Track(0, "a"), Track(1, "b"), Track(2, "c")]));

        var pageResult = await new ProviderPlaylistSnapshotCollector().CollectAsync(
            oversizedPage, context, new(playlist, PageSize: 2));

        Assert.Equal(PlaylistSnapshotCollectionStatus.Failed, pageResult.Status);
        Assert.Equal(ProviderErrorKind.PermanentFailure, pageResult.Error!.Kind);

        var excessiveSummary = Summary(playlist, 100_001);
        var excessivePlaylist = new FakePlaylistCapability("spotify", Page(excessiveSummary, []));
        var countResult = await new ProviderPlaylistSnapshotCollector().CollectAsync(
            excessivePlaylist, context, new(playlist));

        Assert.Equal(PlaylistSnapshotCollectionStatus.Failed, countResult.Status);
        Assert.Equal(ProviderErrorKind.PermanentFailure, countResult.Error!.Kind);
        Assert.Null(countResult.Snapshot);
    }

    private static ProviderExternalResourceId PlaylistId(string value) => new("spotify", ProviderResourceKind.Playlist, value);

    private static ProviderPlaylistSummary Summary(ProviderExternalResourceId id, int trackCount = 3) => new(
        id,
        "Road Mix",
        new ProviderPlaylistOwner("owner"),
        "revision-17",
        "Source description",
        new ProviderArtworkReference(new ProviderExternalResourceId("spotify", ProviderResourceKind.Playlist, "opaque-art-secret"), revision: "art-rev"),
        trackCount: trackCount,
        sourceETag: "etag-17");

    private static ProviderPlaylistTrack Track(int position, string id) => new(
        position,
        new ProviderExternalResourceId("spotify", ProviderResourceKind.Track, id),
        metadata: new ProviderTrackMetadata(
            new ProviderExternalResourceId("spotify", ProviderResourceKind.Track, id),
            $"Track {position}",
            [new ProviderArtistCredit("Artist", new ProviderExternalResourceId("spotify", ProviderResourceKind.Artist, $"artist-{position}"))],
            albumId: new ProviderExternalResourceId("spotify", ProviderResourceKind.Album, $"album-{position}"),
            albumTitle: "Album",
            duration: TimeSpan.FromMinutes(3),
            isrc: $"USABC{position:0000000}",
            isExplicit: false));

    private static ProviderPlaylistTrackPage Page(
        ProviderPlaylistSummary summary,
        IEnumerable<ProviderPlaylistTrack> tracks,
        string? cursor = null,
        string? snapshot = null) =>
        new(summary, new ProviderPage<ProviderPlaylistTrack>("spotify", tracks, cursor, cursor != null, snapshot));

    private static ProviderExecutionContext Context(
        string provider,
        bool includeAccount = true,
        CancellationToken cancellationToken = default)
    {
        var tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var user = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var actor = new ProviderActorContext(
            tenant,
            ProviderActorKind.User,
            user,
            new ProviderBackendPrincipal("jellyfin", "backend", "principal"));
        var account = includeAccount
            ? new ProviderAccountContext(
                Guid.Parse("33333333-3333-3333-3333-333333333333"),
                provider,
                ProviderAccountScope.User,
                1,
                tenantId: tenant,
                ownerUserId: user)
            : null;
        return new(
            actor,
            provider,
            account,
            null,
            new ProviderExecutionPolicy(
                new ProviderQualityPolicy(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true),
                ProviderExplicitContentPolicy.Allow,
                true,
                false,
                false,
                [provider]),
            "playlist-snapshot",
            "correlation",
            DateTimeOffset.UtcNow.AddMinutes(1),
            cancellationToken);
    }

    private static CollectedPlaylistSourceSnapshot LastGood(
        ProviderExecutionContext context,
        ProviderExternalResourceId playlist) => new(
        context.ProviderId,
        context.Account!.AccountId,
        ProviderPlaylistSnapshotCollector.HashResource(playlist),
        "old-revision",
        "old-etag",
        "Old",
        null,
        null,
        []);

    private sealed class FakePlaylistCapability : IProviderPlaylistCapability
    {
        private readonly Queue<object> _responses;

        public FakePlaylistCapability(string providerId, params object[] responses)
        {
            ProviderId = providerId;
            _responses = new(responses);
        }

        public string ProviderId { get; }
        public ProviderCapabilityKind Capability => ProviderCapabilityKind.Playlist;
        public List<string?> Cursors { get; } = [];
        public List<string?> ExpectedRevisions { get; } = [];
        public int NonPlaylistCallCount { get; private set; }

        public Task<ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>> GetUserPlaylistsAsync(ProviderExecutionContext context, ProviderUserPlaylistsRequest request)
        {
            NonPlaylistCallCount++;
            throw new NotSupportedException();
        }

        public Task<ProviderOutcome<ProviderPlaylistTrackPage>> GetPlaylistTracksAsync(ProviderExecutionContext context, ProviderPlaylistTracksRequest request)
        {
            Cursors.Add(request.Page.Cursor);
            ExpectedRevisions.Add(request.ExpectedRevision);
            var response = _responses.Dequeue();
            return Task.FromResult(response is ProviderError error
                ? ProviderOutcome<ProviderPlaylistTrackPage>.Failure(error)
                : ProviderOutcome<ProviderPlaylistTrackPage>.Success((ProviderPlaylistTrackPage)response));
        }

        public Task<ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>> SearchPlaylistsAsync(ProviderExecutionContext context, ProviderPlaylistSearchRequest request)
        {
            NonPlaylistCallCount++;
            throw new NotSupportedException();
        }
    }
}

using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Deezer;
using allstarr.Core.Matching;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Models.Subsonic;
using allstarr.Services;
using Moq;

namespace allstarr.Tests;

public sealed class DeezerMetadataCapabilityAdapterTests
{
    [Fact]
    public async Task SearchTracks_MapsLegacyResultsToTypedProviderIds()
    {
        var legacy = new Mock<IConcreteMetadataService>(MockBehavior.Strict);
        legacy.Setup(item => item.SearchSongsAsync(
                "daft punk",
                2,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new Song
                {
                    Id = "ext-deezer-3135556",
                    ExternalId = "3135556",
                    ExternalProvider = "deezer",
                    Title = "Harder Better Faster Stronger",
                    Artist = "Daft Punk",
                    Artists = ["Daft Punk"],
                    ArtistIds = ["27"],
                    Album = "Discovery",
                    AlbumId = "302127",
                    Duration = 224,
                    Isrc = "GBDUW0000059",
                    CoverArtUrlLarge = "https://cdn.example.invalid/cover.jpg"
                }
            ]);
        var adapter = new DeezerMetadataCapabilityAdapter(legacy.Object);

        var outcome = await adapter.SearchTracksAsync(
            Context(),
            new ProviderMetadataSearchRequest(
                "daft punk",
                new ProviderPageRequest(limit: 2)));

        Assert.True(outcome.IsSuccess);
        var page = outcome.RequireValue();
        Assert.Equal("deezer", page.ProviderId);
        var track = Assert.Single(page.Items);
        Assert.Equal("3135556", track.Id.Value);
        Assert.Equal(ProviderResourceKind.Track, track.Id.ResourceKind);
        Assert.Equal("27", Assert.Single(track.Artists).ArtistId!.Value);
        Assert.Equal("302127", track.AlbumId!.Value);
        Assert.Equal(TimeSpan.FromSeconds(224), track.Duration);
        Assert.Equal("https://cdn.example.invalid/cover.jpg", track.Artwork!.PublicUri!.AbsoluteUri);
    }

    [Fact]
    public async Task SearchTracks_StripsTypedCompatibilityPrefixFromAlbumId()
    {
        var legacy = new Mock<IConcreteMetadataService>(MockBehavior.Strict);
        legacy.Setup(item => item.SearchSongsAsync("track", 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new Song
                {
                    Id = "ext-deezer-song-123",
                    ExternalId = "123",
                    ExternalProvider = "deezer",
                    Title = "Track",
                    Artist = "Artist",
                    Artists = ["Artist"],
                    ArtistIds = ["789"],
                    Album = "Album",
                    AlbumId = "ext-deezer-album-456",
                    Duration = 180
                }
            ]);
        var adapter = new DeezerMetadataCapabilityAdapter(legacy.Object);

        var outcome = await adapter.SearchTracksAsync(
            Context(),
            new ProviderMetadataSearchRequest("track", new ProviderPageRequest(1)));

        Assert.Equal("456", Assert.Single(outcome.RequireValue().Items).AlbumId!.Value);
    }

    [Fact]
    public async Task CursorThatLegacyProviderCannotHonor_ReturnsTypedNotSupported()
    {
        var legacy = new Mock<IConcreteMetadataService>(MockBehavior.Strict);
        var adapter = new DeezerMetadataCapabilityAdapter(legacy.Object);

        var outcome = await adapter.SearchTracksAsync(
            Context(),
            new ProviderMetadataSearchRequest(
                "query",
                new ProviderPageRequest(cursor: "next-page")));

        Assert.False(outcome.IsSuccess);
        Assert.Equal(ProviderErrorKind.NotSupported, outcome.Error!.Kind);
        legacy.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MissingLookup_ReturnsTypedNotFound()
    {
        var legacy = new Mock<IConcreteMetadataService>(MockBehavior.Strict);
        legacy.Setup(item => item.GetSongAsync(
                "deezer",
                "missing",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Song?)null);
        var adapter = new DeezerMetadataCapabilityAdapter(legacy.Object);

        var outcome = await adapter.GetTrackAsync(
            Context(),
            new ProviderTrackLookupRequest(new ProviderExternalResourceId(
                "deezer",
                ProviderResourceKind.Track,
                "missing")));

        Assert.False(outcome.IsSuccess);
        Assert.Equal(ProviderErrorKind.NotFound, outcome.Error!.Kind);
    }

    [Fact]
    public async Task ProviderException_CannotEscapeThroughOutcomeText()
    {
        var legacy = new Mock<IConcreteMetadataService>(MockBehavior.Strict);
        legacy.Setup(item => item.FindSongByIsrcAsync(
                "GBDUW0000059",
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(
                "Authorization: Bearer secret-token https://signed.example.invalid/media"));
        var adapter = new DeezerMetadataCapabilityAdapter(legacy.Object);

        var outcome = await adapter.LookupByIsrcAsync(
            Context(),
            new ProviderIsrcLookupRequest("GBDUW0000059"));

        Assert.False(outcome.IsSuccess);
        Assert.Equal(ProviderErrorKind.TransientFailure, outcome.Error!.Kind);
        Assert.DoesNotContain("secret-token", outcome.Error.SafeMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("signed.example", outcome.Error.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationSwallowedByConcreteServiceStillReturnsCanceled()
    {
        using var cancellation = new CancellationTokenSource();
        var legacy = new Mock<IConcreteMetadataService>(MockBehavior.Strict);
        legacy.Setup(item => item.SearchSongsAsync("query", 1, cancellation.Token))
            .ReturnsAsync(() =>
            {
                cancellation.Cancel();
                return [];
            });
        var adapter = new DeezerMetadataCapabilityAdapter(legacy.Object);

        var outcome = await adapter.SearchTracksAsync(
            Context(cancellation.Token),
            new ProviderMetadataSearchRequest("query", new ProviderPageRequest(1)));

        Assert.Equal(ProviderErrorKind.Canceled, outcome.Error!.Kind);
    }

    [Fact]
    public void BuiltInRegistration_IsAtomicAndResolvesTypedImplementation()
    {
        var adapter = new DeezerMetadataCapabilityAdapter(
            new Mock<IConcreteMetadataService>(MockBehavior.Strict).Object);
        var registry = new ProviderRegistry(
            [DeezerMetadataCapabilityAdapter.CreateRegistration(adapter, Playlist(), Download(), Streaming())]);

        var descriptor = registry.GetRequired("deezer");
        var resolved = registry.GetRequiredCapability<IProviderMetadataCapability>(
            "deezer",
            ProviderCapabilityKind.Metadata);

        Assert.Equal(ProviderOrigin.BuiltIn, descriptor.Origin);
        Assert.Same(adapter, resolved);
        Assert.Single(registry.FindByCapability(ProviderCapabilityKind.Metadata));
    }

    [Fact]
    public async Task ProviderRouter_RoutesAndExecutesTheRealBuiltInAdapter()
    {
        var legacy = new Mock<IConcreteMetadataService>(MockBehavior.Strict);
        legacy.Setup(item => item.SearchSongsAsync(
                "route me",
                1,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new Song
                {
                    Id = "ext-deezer-route-result",
                    ExternalId = "route-result",
                    ExternalProvider = "deezer",
                    Title = "Routed result",
                    Artist = "Artist",
                    Artists = ["Artist"],
                    ArtistIds = ["artist-result"],
                    Album = "Album",
                    AlbumId = "album-result",
                    Duration = 180,
                    Isrc = "USRC17607839"
                }
            ]);
        var adapter = new DeezerMetadataCapabilityAdapter(legacy.Object);
        var registry = new ProviderRegistry(
            [DeezerMetadataCapabilityAdapter.CreateRegistration(adapter, Playlist(), Download(), Streaming())]);
        var router = new ProviderRouter(
            registry,
            new Mock<IProviderRouteAccountResolver>(MockBehavior.Strict).Object,
            new Mock<IProviderRouteHealthSource>(MockBehavior.Strict).Object,
            new Mock<IProviderRouteSidecarSource>(MockBehavior.Strict).Object,
            new Mock<ITrackIdentityService>(MockBehavior.Strict).Object);
        var context = Context();
        var route = new ProviderRouteRequest(
            ProviderCapabilityKind.Metadata,
            context.Actor,
            context.Policy,
            "route-deezer-metadata",
            "route-deezer-fixture",
            DateTimeOffset.UtcNow.AddMinutes(1),
            providerPriority: ["deezer"]);

        var plan = await router.PlanAsync<IProviderMetadataCapability>(route);
        var candidate = Assert.Single(plan.Candidates);
        var outcome = await candidate.Implementation.SearchTracksAsync(
            candidate.Context,
            new ProviderMetadataSearchRequest("route me", new ProviderPageRequest(1)));

        Assert.Same(adapter, candidate.Implementation);
        Assert.Equal("deezer", plan.Decision.SelectedProviderId);
        Assert.Equal("route-result", Assert.Single(outcome.RequireValue().Items).Id.Value);
    }

    [Fact]
    public async Task ArtistAlbumsAndTracksUseTypedPagedResults()
    {
        var legacy = new Mock<IConcreteMetadataService>(MockBehavior.Strict);
        legacy.Setup(item => item.GetArtistAlbumsAsync("deezer", "artist-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new Album { ExternalId = "album-1", Title = "First", Artist = "Artist" },
                new Album { ExternalId = "album-2", Title = "Second", Artist = "Artist" },
                new Album { ExternalId = "album-3", Title = "Third", Artist = "Artist" }
            ]);
        legacy.Setup(item => item.GetArtistTracksAsync("deezer", "artist-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new Song { ExternalId = "track-1", Title = "First", Artist = "Artist", Artists = ["Artist"] },
                new Song { ExternalId = "track-2", Title = "Second", Artist = "Artist", Artists = ["Artist"] }
            ]);
        var adapter = new DeezerMetadataCapabilityAdapter(legacy.Object);
        var artist = new ProviderExternalResourceId("deezer", ProviderResourceKind.Artist, "artist-1");

        var albums = (await adapter.GetArtistAlbumsAsync(
            Context(), new(artist, new(limit: 2)))).RequireValue();
        var remaining = (await adapter.GetArtistAlbumsAsync(
            Context(), new(artist, new(limit: 2, cursor: albums.NextCursor)))).RequireValue();
        var tracks = (await adapter.GetArtistTracksAsync(
            Context(), new(artist, new(limit: 2)))).RequireValue();

        Assert.Equal(["album-1", "album-2"], albums.Items.Select(item => item.Id.Value));
        Assert.Equal("2", albums.NextCursor);
        Assert.True(albums.IsPartial);
        Assert.Equal("album-3", Assert.Single(remaining.Items).Id.Value);
        Assert.Null(remaining.NextCursor);
        Assert.Equal(["track-1", "track-2"], tracks.Items.Select(item => item.Id.Value));
        legacy.VerifyAll();
    }

    [Fact]
    public async Task AlbumLookup_PreservesProtocolVisibleAlbumAndTrackFacts()
    {
        var legacy = new Mock<IConcreteMetadataService>(MockBehavior.Strict);
        legacy.Setup(item => item.GetAlbumAsync("deezer", "album-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Album
            {
                ExternalId = "album-1",
                Title = "Album",
                Artist = "Artist",
                ArtistId = "artist-1",
                Year = 2024,
                Genre = "Electronic",
                SongCount = 1,
                Songs =
                [
                    new Song
                    {
                        ExternalId = "track-1",
                        Title = "Track",
                        Artist = "Artist",
                        Artists = ["Artist"],
                        ArtistIds = ["artist-1"],
                        Album = "Album",
                        AlbumId = "album-1",
                        Track = 2,
                        DiscNumber = 1,
                        TotalTracks = 10,
                        Year = 2024,
                        Genre = "Electronic",
                        Bpm = 120,
                        ReleaseDate = "2024-02-03",
                        AlbumArtist = "Album Artist",
                        Composer = "Composer",
                        Label = "Label",
                        Copyright = "Copyright",
                        Contributors = ["Contributor"],
                        ExplicitContentLyrics = 3
                    }
                ]
            });
        var adapter = new DeezerMetadataCapabilityAdapter(legacy.Object);

        var outcome = await adapter.GetAlbumAsync(
            Context(),
            new ProviderAlbumLookupRequest(new("deezer", ProviderResourceKind.Album, "album-1")));

        var album = outcome.RequireValue();
        Assert.Equal(2024, album.Year);
        Assert.Equal("Electronic", album.Genre);
        var track = Assert.Single(album.Tracks);
        Assert.Equal(2, track.TrackNumber);
        Assert.Equal(1, track.DiscNumber);
        Assert.Equal(10, track.TotalTracks);
        Assert.Equal(120, track.Bpm);
        Assert.Equal("2024-02-03", track.ReleaseDate);
        Assert.Equal("Album Artist", track.AlbumArtist);
        Assert.Equal("Composer", track.Composer);
        Assert.Equal("Label", track.Label);
        Assert.Equal("Copyright", track.Copyright);
        Assert.Equal(["Contributor"], track.Contributors);
        Assert.Equal(3, track.ExplicitContentLyrics);
        legacy.VerifyAll();
    }

    [Fact]
    public async Task PlaylistAdapter_PreservesSummaryAndOrderedTrackFacts()
    {
        var created = new DateTime(2023, 4, 5, 0, 0, 0, DateTimeKind.Utc);
        var playlist = new ExternalPlaylist
        {
            ExternalId = "playlist-1",
            Name = "Road",
            Description = "Drive",
            CuratorName = "Curator",
            Provider = "deezer",
            TrackCount = 2,
            Duration = 420,
            CoverUrl = "https://images.example.test/playlist.webp",
            CreatedDate = created
        };
        var legacy = new Mock<IConcreteMetadataService>(MockBehavior.Strict);
        legacy.Setup(item => item.SearchPlaylistsAsync("road", 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync([playlist]);
        legacy.Setup(item => item.GetPlaylistAsync("deezer", "playlist-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(playlist);
        legacy.Setup(item => item.GetPlaylistTracksAsync("deezer", "playlist-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new Song
                {
                    ExternalId = "track-1", Title = "First", Artist = "Artist", Artists = ["Artist"],
                    Track = 1, DiscNumber = 1, Year = 2023
                },
                new Song
                {
                    ExternalId = "track-2", Title = "Second", Artist = "Artist", Artists = ["Artist"],
                    Track = 2, DiscNumber = 1, Year = 2023
                }
            ]);
        var metadata = new DeezerMetadataCapabilityAdapter(legacy.Object);
        var adapter = new DeezerPlaylistCapabilityAdapter(legacy.Object, metadata);
        var context = PlaylistContext();

        var search = (await adapter.SearchPlaylistsAsync(
            context, new("road", new ProviderPageRequest(5)))).RequireValue();
        var summary = Assert.Single(search.Items);
        var read = (await adapter.GetPlaylistTracksAsync(
            context,
            new(new("deezer", ProviderResourceKind.Playlist, "playlist-1"),
                new ProviderPageRequest(1), summary.SourceRevision))).RequireValue();

        Assert.Equal("Curator", summary.Owner.DisplayName);
        Assert.Equal(420, summary.DurationSeconds);
        Assert.Equal(created, summary.CreatedDate);
        Assert.Equal("https://images.example.test/playlist.webp", summary.Artwork!.PublicUri!.AbsoluteUri);
        Assert.Equal("1", read.Tracks.NextCursor);
        var first = Assert.Single(read.Tracks.Items);
        Assert.Equal(0, first.Position);
        Assert.Equal("track-1", first.TrackId.Value);
        Assert.Equal(1, first.Metadata!.TrackNumber);
        Assert.Equal(2023, first.Metadata.Year);
        legacy.VerifyAll();
    }

    private static ProviderExecutionContext Context(CancellationToken cancellationToken = default)
    {
        var actor = new ProviderActorContext(
            Guid.CreateVersion7(),
            ProviderActorKind.User,
            Guid.CreateVersion7(),
            new ProviderBackendPrincipal("jellyfin", "fixture", "fixture-user"));
        return new ProviderExecutionContext(
            actor,
            "deezer",
            account: null,
            library: null,
            new ProviderExecutionPolicy(
                new ProviderQualityPolicy(
                    ProviderAudioQuality.Any,
                    ProviderAudioQuality.HighResolution,
                    allowTranscode: true),
                ProviderExplicitContentPolicy.Allow,
                allowFallback: true,
                allowSharedAccount: false,
                allowManagedDownloads: false,
                allowedProviderIds: ["deezer"]),
            operationId: "metadata-search",
            correlationId: "metadata-search-fixture",
            deadline: DateTimeOffset.UtcNow.AddMinutes(1),
            cancellationToken: cancellationToken);
    }

    private static ProviderExecutionContext PlaylistContext()
    {
        var context = Context();
        return new(
            context.Actor,
            context.ProviderId,
            new ProviderAccountContext(
                Guid.CreateVersion7(),
                "deezer",
                ProviderAccountScope.User,
                1,
                tenantId: context.Actor.TenantId,
                ownerUserId: context.Actor.EffectiveUserId),
            context.Library,
            context.Policy,
            context.OperationId,
            context.CorrelationId,
            context.Deadline,
            context.CancellationToken);
    }

    private static IProviderDownloadCapability Download() =>
        Mock.Of<IProviderDownloadCapability>(item =>
            item.ProviderId == "deezer" && item.Capability == ProviderCapabilityKind.Download);

    private static IProviderPlaylistCapability Playlist() =>
        Mock.Of<IProviderPlaylistCapability>(item =>
            item.ProviderId == "deezer" && item.Capability == ProviderCapabilityKind.Playlist);

    private static IProviderStreamingCapability Streaming() =>
        Mock.Of<IProviderStreamingCapability>(item =>
            item.ProviderId == "deezer" && item.Capability == ProviderCapabilityKind.Streaming);
}

using allstarr.Core.Capabilities;
using allstarr.Core.Enrichment;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Services.MusicBrainz;
using Moq;

namespace allstarr.Tests;

public sealed partial class LibraryPromotionTests
{
    [Fact]
    public async Task PromotionCatalog_PrefersKnownReleaseTracklistAndMetadata()
    {
        var recordingMbid = Guid.NewGuid().ToString("D");
        var releaseId = Guid.NewGuid().ToString("D");
        var groupId = Guid.NewGuid().ToString("D");
        var artistId = Guid.NewGuid().ToString("D");
        var song = new Song
        {
            CanonicalRecordingId = recordingId,
            Title = "Provider title",
            ExternalProvider = "deezer",
            ExternalId = "track",
            AlbumId = "album",
            ProviderIds = new() { ["musicbrainz_releaseid"] = releaseId }
        };
        var identities = CatalogIdentities(song, recordingMbid);
        var providers = new Mock<IProtocolProviderGateway>();
        var musicBrainz = new Mock<IMusicBrainzCatalogClient>();
        musicBrainz.Setup(item => item.LookupReleaseByMbidAsync(releaseId, It.IsAny<CancellationToken>())).ReturnsAsync(new MusicBrainzRelease
        {
            Id = releaseId,
            Title = "Release title",
            Date = "2020-01-02",
            ReleaseGroup = new() { Id = groupId, FirstReleaseDate = "1990-03-04" },
            ArtistCredit = [new() { Name = "Artist", Artist = new() { Id = artistId, Name = "Artist" } }],
            Media = [new() { Position = 1, TrackCount = 1, Tracks = [new() { Position = 1, Title = "Release track", Recording = new() { Id = recordingMbid } }] }]
        });
        var catalog = new PromotionCatalog(factory, identities.Object, providers.Object, musicBrainz.Object);
        var resolved = Assert.Single(await catalog.ResolveAsync(Context, recordingId, "album", default));
        Assert.Equal("Release track", resolved.Title);
        Assert.Equal("Release title", resolved.Album);
        Assert.Equal("2020-01-02", resolved.ReleaseDate);
        Assert.Equal("1990-03-04", resolved.ProviderIds["originaldate"]);
        Assert.Equal(groupId, resolved.ProviderIds["musicbrainz_releasegroupid"]);
        Assert.Equal(artistId, resolved.ProviderIds["musicbrainz_artistid"]);
        Assert.Equal(1, resolved.Track);
        Assert.Equal(1, resolved.DiscNumber);
    }

    [Theory]
    [InlineData("deezer", "album", "deezer")]
    [InlineData("deezer", "ext-deezer-album-album", "deezer")]
    [InlineData("apple-musickit", "album", "apple-musickit")]
    [InlineData("apple-musickit", "ext-apple-musickit-album-album", "apple-musickit")]
    [InlineData("apple-musickit", "ext-apple-download-album-album", "apple-download")]
    public async Task PromotionCatalog_UsesCompleteProviderAlbumWhenReleaseIsUnknown(string provider, string albumId, string albumProvider)
    {
        var song = new Song { CanonicalRecordingId = recordingId, Title = "Song", ExternalProvider = provider, ExternalId = "track", AlbumId = albumId };
        var identities = CatalogIdentities(song);
        var providers = new Mock<IProtocolProviderGateway>();
        providers.Setup(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), albumProvider, "album")).ReturnsAsync(new Album
        {
            SongCount = 2,
            Songs = [song, new() { CanonicalRecordingId = Guid.NewGuid(), Title = "Second song" }]
        });
        var musicBrainz = new Mock<IMusicBrainzCatalogClient>(MockBehavior.Strict);
        var catalog = new PromotionCatalog(factory, identities.Object, providers.Object, musicBrainz.Object);
        Assert.Equal(2, (await catalog.ResolveAsync(Context, recordingId, "album", default)).Count);
        providers.VerifyAll();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PromotionCatalog_FreshSongMetadataCanPreviewAndQueueItsAlbum(bool missingCachedAlbum)
    {
        var song = new Song
        {
            Title = "Song",
            Artist = "Artist",
            Album = "Release",
            Codec = "flac",
            Container = "flac",
            BitDepth = 16,
            SampleRate = 44100,
            Track = 3,
            Year = 2026,
            ReleaseDate = "2026-01-02",
            ExternalProvider = "apple-musickit",
            ExternalId = "track",
            AlbumId = "ext-apple-download-album-release"
        };
        var identities = CatalogIdentities(song);
        identities.Setup(item => item.GetStoredSongsAsync(It.IsAny<ProviderActorContext>(), recordingId.ToString("D"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Song>());
        var providers = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        providers.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "allstarr", recordingId.ToString("D")))
            .ReturnsAsync(missingCachedAlbum ? new Song
            {
                Title = song.Title,
                Artist = song.Artist,
                Album = song.Album,
                ExternalProvider = song.ExternalProvider,
                ExternalId = song.ExternalId
            } : song);
        if (missingCachedAlbum)
            providers.Setup(item => item.GetExactSongAsync(It.IsAny<ProtocolExecutionContext>(), "apple-musickit", "track"))
                .ReturnsAsync(song);
        providers.Setup(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "apple-download", "release"))
            .ReturnsAsync(new Album
            {
                SongCount = 2,
                Year = 2026,
                ReleaseDate = "2026-01-02",
                Songs = [song, new() { CanonicalRecordingId = Guid.NewGuid(), Title = "Second song", Artist = "Artist", Album = "Release", Track = 4, Year = 2026, ReleaseDate = "2026-01-02" }]
            });
        var catalog = new PromotionCatalog(factory, identities.Object, providers.Object, Mock.Of<IMusicBrainzCatalogClient>(MockBehavior.Strict));
        var promotions = BatchService(catalog, Mock.Of<IPromotionAcquisition>(), identityService: identities.Object);
        await new PromotionProfiles(factory).SaveMineAsync(Context, new()
        {
            LibraryRootId = rootId,
            NamingConfirmed = true,
            NamingScript = PromotionProfile.DefaultNamingScript
        });
        var request = Request() with { ArtifactId = Guid.Empty, Unit = "album" };

        var preview = await promotions.PreviewAsync(Context, request);
        Assert.Equal("Release", preview.Album!.Title);
        Assert.Equal(2, preview.Album.SongCount);
        Assert.Contains("2026", preview.DestinationPath);
        Assert.Equal(rootId, preview.RootId);
        var run = await promotions.StartAsync(Context, request);
        Assert.Equal("Queued", run.Outcome);
        Assert.Equal(2, run.Tracks.Count);
        Assert.Contains(run.Tracks, track => track.RecordingId == recordingId && track.Song.Title == "Song");
        providers.VerifyAll();
    }

    [Fact]
    public async Task PromotionCatalog_RefreshesIncompleteSourceAndAgreesForSingleAndAlbum()
    {
        const string isrc = "US-AAA-26-00003";
        var cached = new Song
        {
            Title = "I Like Ur Look",
            Artist = "Kim Petras",
            Codec = "flac",
            Container = "flac",
            BitDepth = 16,
            SampleRate = 44100,
            Album = "Detour",
            Isrc = isrc,
            ExternalProvider = "apple-musickit",
            ExternalId = "track",
            AlbumId = "ext-apple-download-album-detour"
        };
        var current = new Song
        {
            Title = "I Like Ur Look",
            Artist = "Kim Petras",
            Codec = "flac",
            Container = "flac",
            BitDepth = 16,
            SampleRate = 44100,
            Album = "Detour",
            Track = 3,
            Year = 2026,
            ReleaseDate = "2026-01-02",
            Isrc = isrc,
            ExternalProvider = "apple-download",
            ExternalId = "track",
            AlbumId = "ext-apple-download-album-detour"
        };
        var identities = CatalogIdentities(cached);
        var providers = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        providers.Setup(item => item.GetExactSongAsync(It.IsAny<ProtocolExecutionContext>(), "apple-musickit", "track"))
            .ReturnsAsync(current);
        providers.Setup(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "apple-download", "detour"))
            .ReturnsAsync(new Album
            {
                Id = "ext-apple-download-album-detour",
                ExternalProvider = "apple-download",
                ExternalId = "detour",
                Title = "Detour",
                Artist = "Kim Petras",
                Year = 2026,
                ReleaseDate = "2026-01-02",
                SongCount = 2,
                Songs =
                [
                    new Song { CanonicalRecordingId = Guid.NewGuid(), ExternalProvider = "apple-download", ExternalId = "other", Track = 1 },
                    new Song { ExternalProvider = "apple-download", ExternalId = "track", Isrc = isrc, Track = 3 }
                ]
            });
        var catalog = new PromotionCatalog(factory, identities.Object, providers.Object, Mock.Of<IMusicBrainzCatalogClient>(MockBehavior.Strict));

        var album = await catalog.ResolveAsync(Context, recordingId, "album", default);
        var single = await catalog.ResolveAsync(Context, recordingId, "song", default);
        var albumSong = Assert.Single(album, item => item.CanonicalRecordingId == recordingId);
        var singleSong = Assert.Single(single);

        Assert.Equal("Detour", albumSong.Album);
        Assert.Equal(2026, albumSong.Year);
        Assert.Equal(3, albumSong.Track);
        Assert.Equal(albumSong.Album, singleSong.Album);
        Assert.Equal(albumSong.Year, singleSong.Year);
        Assert.Equal(albumSong.Track, singleSong.Track);
        providers.Verify(item => item.GetExactSongAsync(It.IsAny<ProtocolExecutionContext>(), "apple-musickit", "track"), Times.Exactly(2));
        await new PromotionProfiles(factory).SaveMineAsync(Context, new() { NamingConfirmed = true });
        var promotions = BatchService(catalog, Mock.Of<IPromotionAcquisition>(), identityService: identities.Object);
        foreach (var unit in new[] { "song", "album" })
        {
            var preview = await promotions.PreviewAsync(Context, Request() with { Unit = unit, ArtifactId = Guid.Empty });
            Assert.EndsWith("Kim Petras/Detour (2026)/03 - I Like Ur Look.flac", preview.DestinationPath.Replace('\\', '/'));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PromotionCatalog_RequiresAUniqueReleaseTrack(bool duplicateIdentity)
    {
        var source = new Song
        {
            Title = "Song",
            AlbumId = "album",
            Year = 2026,
            Track = 3,
            ExternalProvider = "deezer",
            ExternalId = "track",
            Isrc = "USAAA2600003"
        };
        var tracks = new[]
        {
            new Song { ExternalProvider = "deezer", ExternalId = duplicateIdentity ? "track" : "first", Isrc = source.Isrc },
            new Song { ExternalProvider = "deezer", ExternalId = duplicateIdentity ? "track" : "second", Isrc = source.Isrc }
        };
        var providers = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        providers.Setup(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "album"))
            .ReturnsAsync(new Album { Songs = tracks.ToList(), SongCount = 2, Year = 2026 });
        var catalog = new PromotionCatalog(factory, CatalogIdentities(source).Object, providers.Object, Mock.Of<IMusicBrainzCatalogClient>(MockBehavior.Strict));
        var failure = await Assert.ThrowsAsync<PromotionException>(() => catalog.ResolveAsync(Context, recordingId, "song", default));
        Assert.Equal(PromotionFailure.ReleaseMetadataUnavailable, failure.Failure);
        Assert.All(tracks, track => Assert.Null(track.CanonicalRecordingId));
    }

    private Mock<ITrackIdentityService> CatalogIdentities(Song song, string? mbid = null)
    {
        var identities = new Mock<ITrackIdentityService>();
        identities.Setup(item => item.ResolveRecordingAsync(It.IsAny<ProviderActorContext>(), recordingId.ToString("D"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CanonicalRecordingIdentity(recordingId, owner, null, mbid, 1));
        identities.Setup(item => item.GetStoredSongsAsync(It.IsAny<ProviderActorContext>(), recordingId.ToString("D"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { song });
        return identities;
    }
}

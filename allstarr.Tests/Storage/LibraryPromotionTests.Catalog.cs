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
        var catalog = new PromotionCatalog(factory, identities.Object, providers.Object, UnknownToMusicBrainz());
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
        var catalog = new PromotionCatalog(factory, identities.Object, providers.Object, UnknownToMusicBrainz());

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

    [Theory]
    // One song, then the whole EP: the service's songs keep the ids that play them and take the release's names.
    [InlineData("song", true, true)]
    [InlineData("album", true, true)]
    // A release where one of the album's songs sits elsewhere tags none of them, so the album is never tagged two ways.
    [InlineData("album", false, false)]
    public async Task PromotionCatalog_FindsTheReleaseByIsrcAndNamesTheServicesSongsFromIt(string unit, bool secondFits, bool named)
    {
        var recordingMbid = Guid.NewGuid().ToString("D");
        var otherMbid = Guid.NewGuid().ToString("D");
        var releaseId = Guid.NewGuid().ToString("D");
        var vinylId = Guid.NewGuid().ToString("D");
        var groupId = Guid.NewGuid().ToString("D");
        var artistId = Guid.NewGuid().ToString("D");
        Song Service(string id, string title, int place) => new()
        {
            Title = title,
            Artist = "Ashnikko",
            Album = "Hi, It's Me - EP",
            AlbumId = "ext-apple-download-album-1",
            ExternalProvider = "apple-download",
            ExternalId = id,
            Track = place,
            ReleaseDate = "2019-07-12",
            Year = 2019
        };
        var song = Service("track", "Working Bitch", 2);
        song.CanonicalRecordingId = recordingId;
        song.Isrc = "GBAYE1900594";
        var first = Service("first", secondFits ? "Hi, It's Me" : "Another Song", 1);
        first.CanonicalRecordingId = Guid.NewGuid();
        var identities = CatalogIdentities(song);
        var providers = new Mock<IProtocolProviderGateway>();
        providers.Setup(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "apple-download", "1"))
            .ReturnsAsync(new Album { Title = "Hi, It's Me - EP", SongCount = 2, Songs = [first, song] });
        var credit = new List<MusicBrainzArtistCredit> { new() { Name = "Ashnikko", Artist = new() { Id = artistId, Name = "Ashnikko", SortName = "Ashnikko" } } };
        var musicBrainz = new Mock<IMusicBrainzCatalogClient>();
        musicBrainz.Setup(item => item.LookupByIsrcAsync("GBAYE1900594", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MusicBrainzRecording { Id = recordingMbid, Title = "Working Bitch", Isrcs = ["GBAYE1900594"] });
        // The releases a recording is on come with the recording itself, not with the ISRC read.
        // MusicBrainz answers "busy" to the first ask, as it often does, and is asked again.
        PromotionCatalog.BusyWait = TimeSpan.FromMilliseconds(1);
        musicBrainz.SetupSequence(item => item.LookupByMbidAsync(recordingMbid, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new MusicBrainzLookupException("busy", "MusicBrainz is busy.", true)).ReturnsAsync(new MusicBrainzRecording
        {
            Id = recordingMbid,
            Title = "Working Bitch",
            Releases =
            [
                // Another album the song is on, a bootleg of this one, and a pressing with more songs are all passed over.
                new() { Id = Guid.NewGuid().ToString("D"), Title = "Demidevil", Status = "Official", Date = "2021-01-15" },
                new() { Id = Guid.NewGuid().ToString("D"), Title = "Hi, It's Me", Status = "Bootleg", Date = "2019-07-12" },
                new() { Id = vinylId, Title = "Hi, It's Me", Status = "Official", Date = "2019-07-12", Country = "GB" },
                new() { Id = releaseId, Title = "Hi, It's Me", Status = "Official", Date = "2019-07-12", Country = "XW" }
            ]
        });
        MusicBrainzRelease Release(string id, params (string Mbid, string Title)[] tracks) => new()
        {
            Id = id,
            Title = "Hi, It's Me",
            Date = "2019-07-12",
            Country = "XW",
            Status = "Official",
            Barcode = "190295416812",
            TextRepresentation = new() { Language = "eng", Script = "Latn" },
            ArtistCredit = credit,
            LabelInfo = [new() { CatalogNumber = "DP019", Label = new() { Name = "Digital Picnic" } }],
            ReleaseGroup = new() { Id = groupId, PrimaryType = "EP", FirstReleaseDate = "2019-07-12" },
            Media =
            [
                new()
                {
                    Position = 1,
                    Format = "Digital Media",
                    TrackCount = tracks.Length,
                    Tracks = tracks.Select((track, index) => new MusicBrainzReleaseTrack
                    {
                        Id = Guid.NewGuid().ToString("D"),
                        Position = index + 1,
                        Title = track.Title,
                        Recording = new() { Id = track.Mbid },
                        ArtistCredit = credit
                    }).ToList()
                }
            ]
        };
        musicBrainz.Setup(item => item.LookupReleaseByMbidAsync(releaseId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Release(releaseId, (otherMbid, "Hi, It's Me"), (recordingMbid, "Working Bitch")));
        musicBrainz.Setup(item => item.LookupReleaseByMbidAsync(vinylId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Release(vinylId, (otherMbid, "Hi, It's Me"), (recordingMbid, "Working Bitch"), (Guid.NewGuid().ToString("D"), "Bonus")));
        var catalog = new PromotionCatalog(factory, identities.Object, providers.Object, musicBrainz.Object);

        var resolved = await catalog.ResolveAsync(Context, recordingId, unit, default);

        Assert.Equal(unit == "song" ? 1 : 2, resolved.Count);
        var added = resolved.Single(item => item.ExternalId == "track");
        // The song still plays from the service's own id.
        Assert.Equal("apple-download", added.ExternalProvider);
        Assert.Equal(recordingId, added.CanonicalRecordingId);
        if (!named)
        {
            Assert.All(resolved, item => Assert.Equal("Hi, It's Me - EP", item.Album));
            Assert.All(resolved, item => Assert.DoesNotContain("musicbrainz_releaseid", item.ProviderIds.Keys));
            return;
        }
        Assert.All(resolved, item => Assert.Equal("Hi, It's Me", item.Album));
        Assert.All(resolved, item => Assert.Equal(releaseId, item.ProviderIds["musicbrainz_releaseid"]));
        Assert.Equal(recordingMbid, added.ProviderIds["musicbrainz_recordingid"]);
        Assert.Equal(groupId, added.ProviderIds["musicbrainz_releasegroupid"]);
        Assert.Equal(artistId, added.ProviderIds["musicbrainz_artistid"]);
        Assert.Equal(artistId, added.ProviderIds["musicbrainz_albumartistid"]);
        Assert.True(Guid.TryParse(added.ProviderIds["musicbrainz_releasetrackid"], out _));
        Assert.Equal("190295416812", added.ProviderIds["barcode"]);
        Assert.Equal("Digital Picnic", added.ProviderIds["label"]);
        Assert.Equal("DP019", added.ProviderIds["catalognumber"]);
        Assert.Equal("Digital Media", added.ProviderIds["media"]);
        Assert.Equal("Latn", added.ProviderIds["script"]);
        Assert.Equal("XW", added.ProviderIds["releasecountry"]);
        Assert.Equal("official", added.ProviderIds["releasestatus"]);
        Assert.Equal("ep", added.ProviderIds["releasetype"]);
        Assert.Equal("2019-07-12", added.ProviderIds["originaldate"]);
        Assert.Equal("2019", added.ProviderIds["originalyear"]);
        Assert.Equal("Ashnikko", added.ProviderIds["artists"]);
        Assert.Equal("Ashnikko", added.ProviderIds["artistsort"]);
        Assert.Equal(2, added.Track);
        Assert.Equal(2, added.TotalTracks);
        Assert.Equal(1, added.TotalDiscs);
    }

    [Fact]
    public async Task PromotionCatalog_KeepsTheServicesTagsWhenMusicBrainzDoesNotAnswerOrDoesNotKnowTheAlbum()
    {
        var song = new Song
        {
            CanonicalRecordingId = recordingId,
            Title = "Song",
            Artist = "Artist",
            Album = "Album - Single",
            ExternalProvider = "deezer",
            ExternalId = "track",
            AlbumId = "album",
            Isrc = "USAT21900001",
            Track = 1,
            Year = 2020
        };
        var providers = new Mock<IProtocolProviderGateway>();
        providers.Setup(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "album")).ReturnsAsync(new Album { SongCount = 1, Songs = [song] });
        PromotionCatalog.BusyWait = TimeSpan.FromMilliseconds(1);
        var failing = new Mock<IMusicBrainzCatalogClient>();
        failing.Setup(item => item.LookupByIsrcAsync("USAT21900001", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new MusicBrainzLookupException("unavailable", "MusicBrainz is unavailable.", true));
        Assert.Equal("Album - Single", Assert.Single(await new PromotionCatalog(factory, CatalogIdentities(song).Object, providers.Object, failing.Object)
            .ResolveAsync(Context, recordingId, "song", default)).Album);

        // The recording is known, but only on an album of another name: nothing is guessed.
        var elsewhere = new Mock<IMusicBrainzCatalogClient>(MockBehavior.Strict);
        var known = Guid.NewGuid().ToString("D");
        elsewhere.Setup(item => item.LookupByIsrcAsync("USAT21900001", It.IsAny<CancellationToken>())).ReturnsAsync(new MusicBrainzRecording { Id = known });
        var onAnotherAlbum = new MusicBrainzRecording
        {
            Id = known,
            Title = "Song",
            ArtistCredit = [new() { Name = "Artist" }],
            Releases = [new() { Id = Guid.NewGuid().ToString("D"), Title = "Greatest Hits", Status = "Official" }]
        };
        elsewhere.Setup(item => item.LookupByMbidAsync(known, It.IsAny<CancellationToken>())).ReturnsAsync(onAnotherAlbum);
        // Asked by name next, MusicBrainz offers the same recording, which is not looked at twice.
        elsewhere.Setup(item => item.SearchRecordingsAsync("Song", "Artist", 10, It.IsAny<CancellationToken>())).ReturnsAsync(new[] { onAnotherAlbum });
        var kept = Assert.Single(await new PromotionCatalog(factory, CatalogIdentities(song).Object, providers.Object, elsewhere.Object)
            .ResolveAsync(Context, recordingId, "song", default));
        Assert.Equal("Album - Single", kept.Album);
        Assert.DoesNotContain("musicbrainz_releaseid", kept.ProviderIds.Keys);
    }

    [Theory]
    // No ISRC: the song's name, artist and length find the recording, and its place on the album confirms the release.
    [InlineData(true)]
    // With neither a length nor a track number there is nothing to tell the song from a namesake: MusicBrainz is not
    // asked, and the service's tags stay.
    [InlineData(false)]
    public async Task PromotionCatalog_WithoutAnIsrcFindsTheReleaseByNameArtistAndLength(bool placed)
    {
        var mbid = Guid.NewGuid().ToString("D");
        var releaseId = Guid.NewGuid().ToString("D");
        var liveReleaseId = Guid.NewGuid().ToString("D");
        var song = new Song
        {
            CanonicalRecordingId = recordingId,
            Title = "Juicy",
            Artist = "Doja Cat",
            Artists = ["Doja Cat"],
            Album = "Amala (Deluxe Version)",
            AlbumId = "ext-spotiflac-ytmusic-album-1",
            ExternalProvider = "spotiflac-ytmusic",
            ExternalId = "track",
            Track = placed ? 2 : null,
            Duration = placed ? 203 : null
        };
        var providers = new Mock<IProtocolProviderGateway>();
        providers.Setup(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "spotiflac-ytmusic", "1"))
            .ReturnsAsync(new Album { Title = "Amala (Deluxe Version)", SongCount = 2, Songs = [new Song { Title = "Go to Town", Artist = "Doja Cat", Track = 1, ExternalProvider = "spotiflac-ytmusic", ExternalId = "first", CanonicalRecordingId = Guid.NewGuid() }, song] });
        var credit = new List<MusicBrainzArtistCredit> { new() { Name = "Doja Cat", Artist = new() { Id = Guid.NewGuid().ToString("D"), Name = "Doja Cat", SortName = "Doja Cat" } } };
        MusicBrainzRecording Found(string id, int length, string release) => new()
        {
            Id = id,
            Title = "Juicy",
            Length = length,
            Score = 100,
            ArtistCredit = credit,
            Releases = [new() { Id = release, Title = "Amala (Deluxe Version)", Status = "Official", Date = "2019-03-01" }]
        };
        var musicBrainz = new Mock<IMusicBrainzCatalogClient>(MockBehavior.Strict);
        // A live take of the same name on a same-named release is a minute longer: it is never the song.
        musicBrainz.Setup(item => item.SearchRecordingsAsync("Juicy", "Doja Cat", 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Found(Guid.NewGuid().ToString("D"), 263000, liveReleaseId), Found(mbid, 203400, releaseId) });
        musicBrainz.Setup(item => item.LookupReleaseByMbidAsync(releaseId, It.IsAny<CancellationToken>())).ReturnsAsync(new MusicBrainzRelease
        {
            Id = releaseId,
            Title = "Amala (Deluxe Version)",
            Date = "2019-03-01",
            Status = "Official",
            ArtistCredit = credit,
            ReleaseGroup = new() { Id = Guid.NewGuid().ToString("D"), PrimaryType = "Album", FirstReleaseDate = "2018-03-30" },
            Media =
            [
                new()
                {
                    Position = 1,
                    Format = "Digital Media",
                    TrackCount = 2,
                    Tracks =
                    [
                        new() { Id = Guid.NewGuid().ToString("D"), Position = 1, Title = "Go to Town", Recording = new() { Id = Guid.NewGuid().ToString("D") }, ArtistCredit = credit },
                        new() { Id = Guid.NewGuid().ToString("D"), Position = 2, Title = "Juicy", Recording = new() { Id = mbid }, ArtistCredit = credit }
                    ]
                }
            ]
        });
        var catalog = new PromotionCatalog(factory, CatalogIdentities(song).Object, providers.Object, musicBrainz.Object);

        var added = Assert.Single(await catalog.ResolveAsync(Context, recordingId, "song", default));

        Assert.Equal("spotiflac-ytmusic", added.ExternalProvider);
        if (!placed)
        {
            Assert.DoesNotContain("musicbrainz_releaseid", added.ProviderIds.Keys);
            musicBrainz.VerifyNoOtherCalls();
            return;
        }
        Assert.Equal(mbid, added.ProviderIds["musicbrainz_recordingid"]);
        Assert.Equal(releaseId, added.ProviderIds["musicbrainz_releaseid"]);
        Assert.Equal("2018", added.ProviderIds["originalyear"]);
        musicBrainz.Verify(item => item.LookupReleaseByMbidAsync(liveReleaseId, It.IsAny<CancellationToken>()), Times.Never);
    }

    // A MusicBrainz that knows neither the song's ISRC nor its name.
    private static IMusicBrainzCatalogClient UnknownToMusicBrainz()
    {
        var musicBrainz = new Mock<IMusicBrainzCatalogClient>(MockBehavior.Strict);
        musicBrainz.Setup(item => item.LookupByIsrcAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((MusicBrainzRecording?)null);
        musicBrainz.Setup(item => item.SearchRecordingsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<MusicBrainzRecording>());
        return musicBrainz.Object;
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

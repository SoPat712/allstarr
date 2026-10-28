using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolProviderStreamingGatewayTests
{
    [Theory]
    [InlineData(ProtocolKind.Jellyfin, "Housewife", "Tate McRae", "Housewife", 208, "B0HMZ6W375", "B0HMZ6W376")]
    [InlineData(ProtocolKind.Subsonic, "Housewife", "Tate McRae", "Housewife", 208, "B0HMZ6W375", "B0HMZ6W376")]
    [InlineData(ProtocolKind.Jellyfin, "Starving Artist", "ADÉLA", "PRIMA", 180, "B0HHC1HLZZ", "B0H9P8D5Z9")]
    [InlineData(ProtocolKind.Subsonic, "Starving Artist", "ADÉLA", "PRIMA", 180, "B0HHC1HLZZ", "B0H9P8D5Z9")]
    public async Task SearchCombinesMultipleSparseAmazonCopiesUnderAKnownRecording(ProtocolKind protocol,
        string title, string artist, string album, int seconds, string firstId, string secondId)
    {
        var anchor = RecordingSearchTrack("deezer", "known", title, artist, seconds, album);
        var sparse = new[] { firstId, secondId }.Select(id => new ProviderTrackMetadata(
            new("spotiflac-amazon", ProviderResourceKind.Track, id), title, [new(artist)], isExplicit: false)).ToArray();
        var (gateway, _) = SearchRankingGateway("spotiflac-amazon,deezer", "spotiflac-amazon,deezer",
            RecordingSearchCapability("spotiflac-amazon", sparse), RecordingSearchCapability("deezer", anchor));

        var song = Assert.Single((await gateway.SearchAsync(SearchRankingContext(protocol), $"{title} {artist}", 20, 0, 0)).Songs);

        Assert.Equal("known", song.ExternalId);
        Assert.Equal(seconds, song.Duration);
        Assert.Equal(album, song.Album);
        Assert.Equal(new[] { firstId, secondId, "known" }.Order(), song.SearchRoutes.Select(route => route.Value).Order());
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin)]
    [InlineData(ProtocolKind.Subsonic)]
    public async Task SparseCopiesNeverBridgeIncompatibleKnownRecordingsOrAbsorbVersions(ProtocolKind protocol)
    {
        var (gateway, _) = SearchRankingGateway("spotiflac-amazon,deezer,qobuz", "spotiflac-amazon,deezer,qobuz",
            RecordingSearchCapability("spotiflac-amazon",
                new(new("spotiflac-amazon", ProviderResourceKind.Track, "sparse-one"), "Housewife", [new("Tate McRae")]),
                new(new("spotiflac-amazon", ProviderResourceKind.Track, "sparse-two"), "Housewife", [new("Tate McRae")]),
                new(new("spotiflac-amazon", ProviderResourceKind.Track, "live"), "Housewife (Live)", [new("Tate McRae")]),
                new(new("spotiflac-amazon", ProviderResourceKind.Track, "remix"), "Housewife (Club Remix)", [new("Tate McRae")]),
                new(new("spotiflac-amazon", ProviderResourceKind.Track, "other-artist"), "Housewife", [new("Other Artist")])),
            RecordingSearchCapability("deezer", RecordingSearchTrack("deezer", "first", "Housewife", "Tate McRae", 208, "Housewife")),
            RecordingSearchCapability("qobuz",
                RecordingSearchTrack("qobuz", "different-duration", "Housewife", "Tate McRae", 220, "Housewife"),
                RecordingSearchTrack("qobuz", "different-album", "Housewife", "Tate McRae", 208, "Other Album")));

        var songs = (await gateway.SearchAsync(SearchRankingContext(protocol), "Housewife Tate McRae", 20, 0, 0)).Songs;

        Assert.Equal(6, songs.Count);
        foreach (var id in new[] { "first", "different-duration", "different-album", "live", "remix", "other-artist" })
            Assert.Contains(songs, song => song.ExternalId == id);
        Assert.Equal(3, Assert.Single(songs, song => song.ExternalId == "first").SearchRoutes.Count);
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin)]
    [InlineData(ProtocolKind.Subsonic)]
    public async Task SparseOnlyResultsRemainSeparateWithoutAKnownDurationAnchor(ProtocolKind protocol)
    {
        var (gateway, _) = SearchRankingGateway("deezer,qobuz", "deezer,qobuz",
            RecordingSearchCapability("deezer", new ProviderTrackMetadata(new("deezer", ProviderResourceKind.Track, "one"), "Housewife", [new("Tate McRae")])),
            RecordingSearchCapability("qobuz", new ProviderTrackMetadata(new("qobuz", ProviderResourceKind.Track, "two"), "Housewife", [new("Tate McRae")])));

        Assert.Equal(2, (await gateway.SearchAsync(SearchRankingContext(protocol), "Housewife Tate McRae", 20, 0, 0)).Songs.Count);
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin, "artistgrid", ProviderUseMode.Automatic)]
    [InlineData(ProtocolKind.Subsonic, "artistgrid", ProviderUseMode.Automatic)]
    [InlineData(ProtocolKind.Jellyfin, "spotiflac-amazon", ProviderUseMode.ManualOnly)]
    [InlineData(ProtocolKind.Subsonic, "spotiflac-amazon", ProviderUseMode.ManualOnly)]
    public async Task SparseArtistGridAndManualSelectionsRemainSeparate(ProtocolKind protocol, string provider, ProviderUseMode mode)
    {
        var (gateway, _) = SearchRankingGateway($"deezer,{provider}", $"deezer,{provider}",
            RecordingSearchCapability("deezer", RecordingSearchTrack("deezer", "known", "Housewife", "Tate McRae", 208, "Housewife")),
            RecordingSearchCapability(provider, new ProviderTrackMetadata(new(provider, ProviderResourceKind.Track, "sparse"), "Housewife", [new("Tate McRae")])));

        Assert.Equal(2, (await gateway.SearchAsync(DisplayPolicyContext(protocol, provider, mode), "Housewife Tate McRae", 20, 0, 0)).Songs.Count);
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin, null, 180, null)]
    [InlineData(ProtocolKind.Subsonic, null, 180, null)]
    [InlineData(ProtocolKind.Jellyfin, "PRIMA", 0, null)]
    [InlineData(ProtocolKind.Subsonic, "PRIMA", 0, null)]
    [InlineData(ProtocolKind.Jellyfin, "PRIMA", 180, 0)]
    [InlineData(ProtocolKind.Subsonic, "PRIMA", 180, 0)]
    public async Task SearchWithDifferentIsrcsRequiresAlbumDurationAndCompatibleExplicitness(
        ProtocolKind protocol, string? album, int seconds, int? explicitLyrics)
    {
        var first = new ProviderTrackMetadata(new("deezer", ProviderResourceKind.Track, "original"), "Starving Artist",
            [new("ADÉLA")], albumTitle: "PRIMA", duration: TimeSpan.FromSeconds(180), isrc: "USUM72606037", explicitContentLyrics: 1);
        var other = new ProviderTrackMetadata(new("tidal", ProviderResourceKind.Track, "other"), "Starving Artist",
            [new("Adéla")], albumTitle: album, duration: TimeSpan.FromSeconds(seconds), isrc: "USUM72607796", explicitContentLyrics: explicitLyrics);
        var (gateway, _) = SearchRankingGateway("deezer,tidal", "deezer,tidal",
            RecordingSearchCapability("deezer", first), RecordingSearchCapability("tidal", other));

        Assert.Equal(2, (await gateway.SearchAsync(SearchRankingContext(protocol), "Starving Artist ADELA", 20, 0, 0)).Songs.Count);
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin)]
    [InlineData(ProtocolKind.Subsonic)]
    public async Task SearchCombinesReleaseIsrcsWhenTheOrdinaryRecordingMetadataAgrees(ProtocolKind protocol)
    {
        var (gateway, _) = SearchRankingGateway("deezer,tidal", "deezer,tidal",
            RecordingSearchCapability("deezer", RecordingSearchTrack("deezer", "original", "Starving Artist", "ADÉLA", 180,
                "PRIMA", "USUM72606037")),
            RecordingSearchCapability("tidal",
                RecordingSearchTrack("tidal", "release", "Starving Artist", "Adéla", 180, "PRIMA", "USUM72607796"),
                RecordingSearchTrack("tidal", "live", "Starving Artist (Live)", "ADÉLA", 180, "PRIMA", "USUM72607797"),
                RecordingSearchTrack("tidal", "remix", "Starving Artist (Club Remix)", "ADÉLA", 180, "PRIMA", "USUM72607798"),
                RecordingSearchTrack("tidal", "different-length", "Starving Artist", "ADÉLA", 187, "PRIMA", "USUM72607799"),
                RecordingSearchTrack("tidal", "namesake", "Starving Artist", "Adela Valero", 158, "PRIMA", "USUM72607790")));

        var songs = (await gateway.SearchAsync(SearchRankingContext(protocol), "Starving Artist ADELA", 20, 0, 0)).Songs;

        Assert.Equal(5, songs.Count);
        var original = Assert.Single(songs, song => song.ExternalId == "original");
        Assert.Equal(["original", "release"], original.SearchRoutes.Select(route => route.Value).Order());
        Assert.DoesNotContain(songs, song => song.ExternalId == "release");
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin)]
    [InlineData(ProtocolKind.Subsonic)]
    public async Task SearchCombinesSeparatelyRememberedCopiesWithoutChangingTheirStoredIdentities(ProtocolKind protocol)
    {
        var capabilities = new[]
        {
            RecordingSearchCapability("deezer", RecordingSearchTrack("deezer", "newer", "Starving Artist", "ADÉLA", 180, "PRIMA")),
            RecordingSearchCapability("qobuz", RecordingSearchTrack("qobuz", "saved", "Starving Artist", "Adéla", 180, "PRIMA")),
            RecordingSearchCapability("youtube", RecordingSearchTrack("youtube", "other-artist", "Starving Artist", "Adela Valero", 158))
        };
        var registry = MetadataRegistry(capabilities);
        var (_, router) = SearchRankingGateway("deezer,qobuz,youtube", "deezer,qobuz,youtube", capabilities);
        var older = Guid.Parse("01910000-0000-7000-8000-000000000001");
        var newer = Guid.Parse("01910000-0001-7000-8000-000000000001");
        var identities = new Mock<ITrackIdentityService>(MockBehavior.Strict);
        identities.Setup(item => item.ResolveManyAsync(It.IsAny<IReadOnlyList<TrackIdentityLookup>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TrackIdentityLookup> lookups, CancellationToken _) => lookups.Select(lookup =>
                lookup.ExternalId.ProviderId == "youtube" ? null : new TrackIdentityResolution(
                    lookup.ExternalId.ProviderId == "qobuz" ? older : newer, Guid.CreateVersion7(), lookup.ExternalId,
                    ProviderIdentityScope.Catalog, null, ProviderIdentityVerification.Verified, "played", 1)
                { PublicId = lookup.ExternalId.ProviderId == "qobuz" ? "ext-qobuz-song-saved" : "ext-deezer-song-newer" }).ToArray());
        var gateway = new ProtocolProviderGateway(router.Object, registry, new HttpClientFactory(), identities: identities.Object, audioVerifier: FixtureAudioVerifier.Instance);

        var songs = (await gateway.SearchAsync(SearchRankingContext(protocol), "Starving Artist ADELA", 20, 0, 0)).Songs;

        Assert.Equal(2, songs.Count);
        var original = Assert.Single(songs, song => song.ExternalId != "other-artist");
        Assert.Equal("ext-qobuz-song-saved", original.Id);
        Assert.Equal(older, original.CanonicalRecordingId);
        Assert.Equal(["newer", "saved"], original.SearchRoutes.Select(route => route.Value).Order());
        identities.Verify(item => item.ResolveManyAsync(It.IsAny<IReadOnlyList<TrackIdentityLookup>>(), It.IsAny<CancellationToken>()), Times.Once);
        identities.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin, "Get Lucky Daft Punk")]
    [InlineData(ProtocolKind.Subsonic, "Get Lucky Daft Punk")]
    [InlineData(ProtocolKind.Jellyfin, "Get Lucky")]
    [InlineData(ProtocolKind.Subsonic, "Get Lucky")]
    public async Task SearchPrefersTheAlbumRecordingToTitleCreditsAndDerivatives(ProtocolKind protocol, string query)
    {
        var capabilities = RecordingSearchCapabilities();
        var (gateway, _) = SearchRankingGateway("deezer,soundcloud,youtube,qobuz",
            "deezer,soundcloud,youtube,qobuz", capabilities);

        var result = await gateway.SearchAsync(SearchRankingContext(protocol), query, 25, 0, 0);

        Assert.Equal("album", result.Songs[0].ExternalId);
        var genuine = new[] { "album", "radio", "radio-compilation" };
        Assert.Equal(3, result.Songs.Count(song => genuine.Contains(song.ExternalId)));
        if (query.Contains("Daft Punk", StringComparison.Ordinal))
            Assert.All(result.Songs.Take(3), song => Assert.Contains(song.ExternalId, genuine));
        Assert.Equal(20, result.Songs.Count);
        Assert.DoesNotContain(result.Songs, song => song.ExternalId == "album-alias");
        Assert.DoesNotContain(result.Songs, song => song.ExternalId == "unknown");
        Assert.Contains(result.Songs[0].SearchRoutes, route => route.Value == "unknown");

        var merged = MusicSearchService.Merge<object>(query, [], result.Songs, [], [], [], false);
        Assert.Equal(result.Songs.Select(song => song.ExternalId), merged.Songs.Select(row => row.Song!.ExternalId));
        Assert.Equal("album", MusicSearchService.RankTogether(query, merged).First().Song!.ExternalId);
        foreach (var capability in capabilities)
        {
            Mock.Get(capability).Verify(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(),
                It.IsAny<ProviderMetadataSearchRequest>()), Times.Once);
            Mock.Get(capability).Verify(item => item.SearchAlbumsAsync(It.IsAny<ProviderExecutionContext>(),
                It.IsAny<ProviderMetadataSearchRequest>()), Times.Never);
            Mock.Get(capability).Verify(item => item.SearchArtistsAsync(It.IsAny<ProviderExecutionContext>(),
                It.IsAny<ProviderMetadataSearchRequest>()), Times.Never);
        }
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin)]
    [InlineData(ProtocolKind.Subsonic)]
    public async Task SearchForTheCoverArtistPrefersThatArtistsRecording(ProtocolKind protocol)
    {
        var (gateway, _) = SearchRankingGateway("deezer,soundcloud,youtube,qobuz",
            "deezer,soundcloud,youtube,qobuz", RecordingSearchCapabilities());

        var result = await gateway.SearchAsync(SearchRankingContext(protocol), "Get Lucky Halestorm", 25, 0, 0);

        Assert.Equal("halestorm", result.Songs[0].ExternalId);
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin)]
    [InlineData(ProtocolKind.Subsonic)]
    public async Task SearchForRadioEditPrefersTheGenuineRadioEdit(ProtocolKind protocol)
    {
        var (gateway, _) = SearchRankingGateway("deezer,soundcloud,youtube,qobuz",
            "deezer,soundcloud,youtube,qobuz", RecordingSearchCapabilities());

        var result = await gateway.SearchAsync(SearchRankingContext(protocol), "Get Lucky Daft Punk radio edit", 25, 0, 0);

        Assert.Contains(result.Songs[0].ExternalId, new[] { "radio", "radio-compilation" });
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin)]
    [InlineData(ProtocolKind.Subsonic)]
    public async Task SearchUsesAlbumArtistAndKeepsUnrelatedProvenRecordingsBelowRelevantOnes(ProtocolKind protocol)
    {
        var tracks = RecordingSearchCapability("deezer",
            RecordingSearchTrack("deezer", "title-credit", "Daft Punk - Get Lucky", "Various Artists", 242),
            RecordingSearchTrack("deezer", "unrelated", "Something Else", "Someone Else", 300, isrc: "USAA10000001"),
            new(new("deezer", ProviderResourceKind.Track, "album-artist"), "Get Lucky", [new("Pharrell Williams")],
                duration: TimeSpan.FromSeconds(369), albumArtist: "Daft Punk"));
        var (gateway, _) = SearchRankingGateway("deezer", "deezer", tracks);

        var result = await gateway.SearchAsync(SearchRankingContext(protocol), "Get Lucky Daft Punk", 25, 0, 0);

        Assert.Equal("album-artist", result.Songs[0].ExternalId);
        Assert.Equal("unrelated", result.Songs[^1].ExternalId);
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin)]
    [InlineData(ProtocolKind.Subsonic)]
    public void NativeRecordingWinsTiesWhileARelevantProviderRecordingBeatsALocalCover(ProtocolKind protocol)
    {
        var original = new Song
        {
            Id = "original",
            Title = "Get Lucky",
            Artist = "Daft Punk",
            Duration = 369,
            Isrc = "USQX91300105"
        };
        var native = NativeRecordingSearchRow(protocol, "native", "Get Lucky", "Daft Punk", 369);
        var cover = NativeRecordingSearchRow(protocol, "cover", "Get Lucky (Daft Punk)", "Someone Else", 240);

        var result = MusicSearchService.Merge("Get Lucky Daft Punk", [cover, native], [original], [], [], [], false);

        Assert.Equal(["native", "original", "cover"], result.Songs.Select(row => row.Native?.Id ?? row.Song!.Id));
        Assert.Equal(["native", "original", "cover"], MusicSearchService.RankTogether("Get Lucky Daft Punk", result)
            .Select(row => row.Native?.Id ?? row.Song!.Id));
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin)]
    [InlineData(ProtocolKind.Subsonic)]
    public void NativeDurationAndAlbumMetadataParticipateInTheSharedRanking(ProtocolKind protocol)
    {
        var native = new[]
        {
            NativeRecordingSearchRow(protocol, "unknown", "Get Lucky", "Daft Punk", 0),
            NativeRecordingSearchRow(protocol, "short", "Get Lucky", "Daft Punk", 27),
            NativeRecordingSearchRow(protocol, "mix", "Get Lucky", "Daft Punk", 369, "DJ Mix"),
            NativeRecordingSearchRow(protocol, "album", "Get Lucky", "Daft Punk", 369)
        };

        var result = MusicSearchService.Merge<object>("Get Lucky Daft Punk", native, [], [], [], [], false);

        Assert.Equal("album", result.Songs[0].Native!.Id);
        Assert.Equal(4, result.Songs.Count);
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin)]
    [InlineData(ProtocolKind.Subsonic)]
    public async Task SearchCombinesAlbumRoutesAndKeepsSpecialEditionsBelowGenuineVersions(ProtocolKind protocol)
    {
        var capabilities = new[]
        {
            RecordingSearchCapability("deezer",
                RecordingSearchTrack("deezer", "album", "Get Lucky", "Daft Punk, Pharrell Williams & Nile Rodgers", 369,
                    "Random Access Memories", "USQX91300105"),
                RecordingSearchTrack("deezer", "drumless", "Get Lucky (Drumless Edition) (feat. Pharrell Williams and Nile Rodgers)",
                    "Daft Punk", 369, "Random Access Memories (Drumless Edition)"),
                RecordingSearchTrack("deezer", "radio", "Get Lucky (Radio Edit)", "Daft Punk", 248),
                RecordingSearchTrack("deezer", "sped", "Get Lucky (Sped Up)", "Daft Punk", 310),
                RecordingSearchTrack("deezer", "mixed", "Get Lucky (Mixed)", "Daft Punk", 120, "DJ Mix")),
            RecordingSearchCapability("tidal",
                RecordingSearchTrack("tidal", "compilation", "Get lucky feat Nile Rodgers Pharrell Williams", "Daft Punk", 370,
                    "Until the morning 2", "USQX91300105")),
            RecordingSearchCapability("youtube",
                RecordingSearchTrack("youtube", "video", "Get Lucky (feat. Pharrell Williams and Nile Rodgers)",
                    "Daft Punk", 370, "Random Access Memories"))
        };
        var (gateway, _) = SearchRankingGateway("deezer,tidal,youtube", "deezer,tidal,youtube", capabilities);

        var result = await gateway.SearchAsync(SearchRankingContext(protocol), "Get Lucky Daft Punk", 25, 0, 0);

        Assert.Equal(["album", "radio"], result.Songs.Take(2).Select(song => song.ExternalId));
        Assert.Equal(5, result.Songs.Count);
        Assert.Equal(["album", "compilation", "video"], result.Songs[0].SearchRoutes.Select(route => route.Value).Order());
        Assert.DoesNotContain(result.Songs, song => song.ExternalId is "compilation" or "video");
        Assert.True(result.Songs.FindIndex(song => song.ExternalId == "mixed") >
                    result.Songs.FindIndex(song => song.ExternalId == "radio"));
        foreach (var label in new[] { "drumless", "sped up" })
        {
            var requested = await gateway.SearchAsync(SearchRankingContext(protocol), $"Get Lucky Daft Punk {label}", 25, 0, 0);
            Assert.Equal(label == "drumless" ? "drumless" : "sped", requested.Songs[0].ExternalId);
        }
        foreach (var capability in capabilities)
            Mock.Get(capability).Verify(item => item.GetTrackAsync(It.IsAny<ProviderExecutionContext>(),
                It.IsAny<ProviderTrackLookupRequest>()), Times.Never);
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin)]
    [InlineData(ProtocolKind.Subsonic)]
    public async Task SearchWithoutIsrcPrefersAlbumMetadataAndRetainsTheFirstId(ProtocolKind protocol)
    {
        var (gateway, _) = SearchRankingGateway("youtube,deezer", "youtube,deezer",
            RecordingSearchCapability("youtube", RecordingSearchTrack("youtube", "first",
                "Get Lucky (feat. Pharrell Williams and Nile Rodgers)", "Daft Punk", 370)),
            RecordingSearchCapability("deezer", RecordingSearchTrack("deezer", "album",
                "Get Lucky (Album Version) - 2013 Remaster", "Daft Punk, Pharrell Williams & Nile Rodgers", 369,
                "Random Access Memories")));

        var song = Assert.Single((await gateway.SearchAsync(SearchRankingContext(protocol), "Get Lucky Daft Punk", 25, 0, 0)).Songs);

        Assert.Equal("ext-youtube-song-first", song.Id);
        Assert.Null(song.CanonicalRecordingId);
        Assert.Equal("deezer", song.ExternalProvider);
        Assert.Equal("album", song.ExternalId);
        Assert.Equal("Random Access Memories", song.Album);
        Assert.Equal(["album", "first"], song.SearchRoutes.Select(route => route.Value).Order());
    }

    [Theory]
    [InlineData("Get Lucky", "Daft Punk", 374, "Random Access Memories")]
    [InlineData("Get Lucky", "Daft Punk", 371.1, "Random Access Memories")]
    [InlineData("Get Lucky (Radio Edit)", "Daft Punk", 369, "Random Access Memories")]
    [InlineData("Get Lucky (Drumless Edition)", "Daft Punk", 369, "Random Access Memories")]
    [InlineData("Get Lucky (Club Mix)", "Daft Punk", 369, "Random Access Memories")]
    [InlineData("Get Lucky (Other Artist Remix)", "Daft Punk", 369, "Random Access Memories")]
    [InlineData("Get Lucky (Live)", "Daft Punk", 369, "Random Access Memories")]
    [InlineData("Get Lucky", "Other Artist", 369, "Random Access Memories")]
    [InlineData("Get Lucky", "Daft Punk", 369, "Until the morning 2")]
    [InlineData("Get Lucky (Mixed)", "Daft Punk", 369, "Random Access Memories")]
    public async Task SearchWithoutIsrcKeepsDifferentRecordingsSeparate(string title, string artist, double seconds, string album)
    {
        var candidate = new ProviderTrackMetadata(new("youtube", ProviderResourceKind.Track, "other"), title,
            [new(artist)], albumTitle: album, duration: TimeSpan.FromSeconds(seconds));
        var (gateway, _) = SearchRankingGateway("deezer,youtube", "deezer,youtube",
            RecordingSearchCapability("deezer", RecordingSearchTrack("deezer", "album", "Get Lucky", "Daft Punk", 369,
                "Random Access Memories")), RecordingSearchCapability("youtube", candidate));

        Assert.Equal(2, (await gateway.SearchAsync(SearchRankingContext(ProtocolKind.Jellyfin), "Get Lucky", 25, 0, 0)).Songs.Count);
    }

    [Theory]
    [InlineData("Drumless Edition")]
    [InlineData("Instrumental")]
    [InlineData("Acapella")]
    [InlineData("Karaoke")]
    [InlineData("Sped Up")]
    [InlineData("Slowed + Reverb")]
    [InlineData("Nightcore")]
    [InlineData("8D")]
    [InlineData("Extended Version")]
    [InlineData("Club Mix")]
    public async Task SearchWithoutIsrcKeepsSpecialEditionRoutesSeparate(string label)
    {
        var title = $"Get Lucky ({label})";
        var (gateway, _) = SearchRankingGateway("deezer,youtube", "deezer,youtube",
            RecordingSearchCapability("deezer", RecordingSearchTrack("deezer", "first", title, "Daft Punk", 369)),
            RecordingSearchCapability("youtube", RecordingSearchTrack("youtube", "second", title, "Daft Punk", 369)));

        Assert.Equal(2, (await gateway.SearchAsync(SearchRankingContext(ProtocolKind.Jellyfin), "Get Lucky", 25, 0, 0)).Songs.Count);
    }

    [Theory]
    [InlineData("Get Lucky", 374)]
    [InlineData("Get Lucky (Radio Edit)", 369)]
    [InlineData("Get Lucky (Drumless Edition)", 369)]
    public async Task SharedIsrcStillRequiresCompatibleDurationAndVersion(string title, int seconds)
    {
        var (gateway, _) = SearchRankingGateway("deezer,youtube", "deezer,youtube",
            RecordingSearchCapability("deezer", RecordingSearchTrack("deezer", "album", "Get Lucky", "Daft Punk", 369,
                isrc: "USQX91300105")),
            RecordingSearchCapability("youtube", RecordingSearchTrack("youtube", "other", title, "Daft Punk", seconds,
                isrc: "USQX91300105")));

        Assert.Equal(2, (await gateway.SearchAsync(SearchRankingContext(ProtocolKind.Jellyfin), "Get Lucky", 25, 0, 0)).Songs.Count);
    }

    [Fact]
    public async Task SearchWithoutIsrcDoesNotBridgeDurationOrAlbumDifferences()
    {
        var (gateway, _) = SearchRankingGateway("deezer,youtube,tidal", "deezer,youtube,tidal",
            RecordingSearchCapability("deezer", RecordingSearchTrack("deezer", "first", "Get Lucky", "Daft Punk", 369,
                "Random Access Memories")),
            RecordingSearchCapability("youtube", RecordingSearchTrack("youtube", "middle", "Get Lucky", "Daft Punk", 371)),
            RecordingSearchCapability("tidal", RecordingSearchTrack("tidal", "last", "Get Lucky", "Daft Punk", 373,
                "Until the morning 2")));

        var songs = (await gateway.SearchAsync(SearchRankingContext(ProtocolKind.Jellyfin), "Get Lucky", 25, 0, 0)).Songs;

        Assert.Equal(2, songs.Count);
        Assert.Contains(songs, song => song.ExternalId == "last");
    }

    [Theory]
    [InlineData("Get Lucky", 371)]
    [InlineData("Get Lucky (Remastered)", 369)]
    [InlineData("Get Lucky (Album Version)", 369)]
    public async Task SearchWithoutIsrcCombinesGenuineAnnotationsWithinTwoSeconds(string title, double seconds)
    {
        var (gateway, _) = SearchRankingGateway("deezer,youtube", "deezer,youtube",
            RecordingSearchCapability("deezer", RecordingSearchTrack("deezer", "album", "Get Lucky", "Daft Punk", 369,
                "Random Access Memories")),
            RecordingSearchCapability("youtube", new ProviderTrackMetadata(new("youtube", ProviderResourceKind.Track, "video"),
                title, [new("Daft Punk")], albumTitle: "Random Access Memories", duration: TimeSpan.FromSeconds(seconds))));

        Assert.Single((await gateway.SearchAsync(SearchRankingContext(ProtocolKind.Jellyfin), "Get Lucky", 25, 0, 0)).Songs);
    }

    [Fact]
    public async Task MetadataGroupingRespectsEveryDurationInAnIsrcGroup()
    {
        var (gateway, _) = SearchRankingGateway("deezer,qobuz,youtube", "deezer,qobuz,youtube",
            RecordingSearchCapability("deezer", RecordingSearchTrack("deezer", "album", "Get Lucky", "Daft Punk", 369,
                "Random Access Memories", "USQX91300105")),
            RecordingSearchCapability("qobuz", RecordingSearchTrack("qobuz", "alias", "Get Lucky", "Daft Punk", 367,
                "Random Access Memories", "USQX91300105")),
            RecordingSearchCapability("youtube", RecordingSearchTrack("youtube", "video", "Get Lucky", "Daft Punk", 371,
                "Random Access Memories")));

        Assert.Equal(2, (await gateway.SearchAsync(SearchRankingContext(ProtocolKind.Jellyfin), "Get Lucky", 25, 0, 0)).Songs.Count);
    }

    private static NativeMusicItem<object> NativeRecordingSearchRow(ProtocolKind protocol, string id,
        string title, string artist, int seconds, string album = "Random Access Memories")
    {
        if (protocol == ProtocolKind.Subsonic)
            return allstarr.Services.Subsonic.SubsonicModelMapper.ToNativeItem(new Dictionary<string, object>
            {
                ["id"] = id,
                ["title"] = title,
                ["artist"] = artist,
                ["duration"] = seconds,
                ["album"] = album
            }, MusicItemKind.Song);
        var row = allstarr.Controllers.JellyfinController.ToNativeSearchItem(new Dictionary<string, object?>
        {
            ["Id"] = id,
            ["Name"] = title,
            ["Type"] = "Audio",
            ["Artists"] = new[] { artist },
            ["RunTimeTicks"] = seconds * TimeSpan.TicksPerSecond,
            ["Album"] = album
        })!;
        return new(row.Kind, row.Id, row.Name, row.ArtistText, row.Payload)
        {
            AlbumText = row.AlbumText,
            AlbumArtistText = row.AlbumArtistText,
            DurationSeconds = row.DurationSeconds
        };
    }

    private static IProviderMetadataCapability[] RecordingSearchCapabilities() =>
    [
        RecordingSearchCapability("deezer",
            RecordingSearchTrack("deezer", "radio", "Get Lucky (Radio Edit - feat. Pharrell Williams)", "Daft Punk", 248),
            RecordingSearchTrack("deezer", "tribute", "Get Lucky", "Daft Punk Experience", 204),
            RecordingSearchTrack("deezer", "metal", "Get Lucky (Daft Punk)", "Charlie Parra Del Riego", 222, "Rock and Pop hits go metal"),
            RecordingSearchTrack("deezer", "compilation", "Daft Punk - Get Lucky", "Various Artists", 242, "Electronic Dance Music"),
            RecordingSearchTrack("deezer", "album", "Get Lucky", "Daft Punk, Pharrell Williams & Nile Rodgers", 369,
                "Random Access Memories", "USQX91300105"),
            RecordingSearchTrack("deezer", "radio-compilation", "Get Lucky (Radio Edit)", "Daft Punk", 248, "2010s Hits"),
            RecordingSearchTrack("deezer", "originally", "Get Lucky (Originally performed by Daft Punk)", "Lowland", 240),
            RecordingSearchTrack("deezer", "lullaby", "Get Lucky (Lullaby Version of Daft Punk)", "Lullaby Players", 240),
            RecordingSearchTrack("deezer", "music-box", "Get Lucky (Music Box Version of Daft Punk)", "Music Box Players", 240),
            RecordingSearchTrack("deezer", "ringtone", "Get Lucky (In the Style of Daft Punk)", "Ringtones", 27),
            RecordingSearchTrack("deezer", "originally-by", "Get Lucky (Originally by Daft Punk)", "Other Players", 240),
            RecordingSearchTrack("deezer", "karaoke", "Get Lucky (Karaoke)", "Daft Punk Karaoke", 240),
            RecordingSearchTrack("deezer", "bossa", "Get Lucky (Bossa Nova Version [Originally Performed by Daft Punk])", "Bossa Players", 240),
            RecordingSearchTrack("deezer", "famous", "Get Lucky (Made Famous by Daft Punk)", "Other Players", 240),
            RecordingSearchTrack("deezer", "8-bit", "Get Lucky (8-bit Version)", "Other Players", 240),
            RecordingSearchTrack("deezer", "piano", "Get Lucky (Piano Cover)", "Other Players", 240),
            RecordingSearchTrack("deezer", "halestorm", "Get Lucky (Cover)", "Halestorm", 240)),
        RecordingSearchCapability("soundcloud",
            RecordingSearchTrack("soundcloud", "maitre", "Get Lucky (Daft Punk)", "Maitre 80", 173),
            RecordingSearchTrack("soundcloud", "teenage", "Get Lucky - Daft Punk", "Teenage Kicks.", 244)),
        RecordingSearchCapability("youtube",
            RecordingSearchTrack("youtube", "unknown", "Get Lucky", "Daft Punk", 0),
            RecordingSearchTrack("youtube", "unknown-prefixed", "Daft Punk - Get Lucky", "Daft Punk", 0)),
        RecordingSearchCapability("qobuz",
            RecordingSearchTrack("qobuz", "album-alias", "Get Lucky", "Daft Punk", 369,
                "Random Access Memories", "USQX91300105"))
    ];

    private static ProviderTrackMetadata RecordingSearchTrack(string provider, string id, string title,
        string artist, int seconds, string? album = null, string? isrc = null) =>
        new(new(provider, ProviderResourceKind.Track, id), title, [new(artist)], albumTitle: album,
            duration: TimeSpan.FromSeconds(seconds), isrc: isrc);

    private static IProviderMetadataCapability RecordingSearchCapability(string provider, params ProviderTrackMetadata[] tracks)
    {
        var capability = Mock.Get(SearchRankingCapability(provider));
        capability.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new(provider, tracks)));
        return capability.Object;
    }
}

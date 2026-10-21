using allstarr.Core.Capabilities;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolProviderStreamingGatewayTests
{
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
        Assert.Equal(21, result.Songs.Count);
        Assert.DoesNotContain(result.Songs, song => song.ExternalId == "album-alias");

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

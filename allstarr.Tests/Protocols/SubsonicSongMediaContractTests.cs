using System.Text.Json;
using System.Xml.Linq;
using allstarr.Core.Capabilities;
using allstarr.Core.Favorites;
using allstarr.Core.Matching;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Protocols.Subsonic;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Services.Subsonic;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace allstarr.Tests;

public sealed class SubsonicSongMediaContractTests
{
    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    public void FeishinAndTempo_SparseProviderSongHasConsumableMediaFields(string format)
    {
        var song = new Song { Id = "ext-deezer-song-42", Title = "Sparse", ExternalProvider = "deezer" };
        var result = new SubsonicResponseBuilder().CreateSongResponse(format, song);

        AssertSparseMedia(ReadSong(result, format));
        Assert.Null(song.LocalPath);
        Assert.Null(song.Bitrate);
    }

    [Theory]
    [InlineData("mp3", "audio/mpeg")]
    [InlineData("flac", "audio/flac")]
    [InlineData("ogg", "audio/ogg")]
    [InlineData("m4a", "audio/mp4")]
    [InlineData("aac", "audio/aac")]
    [InlineData("wav", "audio/wav")]
    public void KnownMediaFacts_AgreeInBothFormats(string suffix, string contentType)
    {
        var song = new Song
        {
            Id = "ext-deezer-song-42",
            ExternalProvider = "deezer",
            Title = "Known",
            LocalPath = $"/fixture/audio.{suffix}"
        };
        var builder = new SubsonicResponseBuilder();
        foreach (var format in new[] { "json", "xml" })
        {
            var media = ReadSong(builder.CreateSongResponse(format, song), format);
            Assert.Equal(contentType, media["contentType"]);
            Assert.Equal(suffix, media["suffix"]);
            Assert.False(media.ContainsKey("size"));
            Assert.False(media.ContainsKey("bitRate"));
        }
    }

    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    public void SavedProviderFavorite_DoesNotNeedMediaLookupToNormalize(string format)
    {
        const string id = "ext-deezer-song-42";
        var favorite = new FavoriteViewItem(id, new("deezer", ProviderResourceKind.Track, "42"),
            DateTimeOffset.UnixEpoch, FavoriteDisplaySnapshot.Placeholder(id, ProviderResourceKind.Track));
        var builder = new SubsonicResponseBuilder();
        var media = format == "json"
            ? ReadJson(JsonSerializer.SerializeToElement(builder.ConvertFavoriteToJson(favorite)))
            : ReadXml(builder.ConvertFavoriteToXml(favorite, XNamespace.None));

        AssertSparseMedia(media);
        Assert.True(media.ContainsKey("starred"));
    }

    [Theory]
    [InlineData("ext-deezer-song-42", null)]
    [InlineData("ext-qobuz-song-42", "qobuz")]
    [InlineData("ext-applemusic-song-42", "applemusic")]
    [InlineData("ext-extension-song-42", "extension")]
    public void UnknownProviderMedia_DoesNotGuessFromProviderName(string id, string? provider)
    {
        var song = new Song { Id = id, ExternalProvider = provider, Title = "Sparse" };
        var builder = new SubsonicResponseBuilder();
        foreach (var format in new[] { "json", "xml" })
            AssertSparseMedia(ReadSong(builder.CreateSongResponse(format, song), format));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/fixture/song")]
    [InlineData("/fixture/song.unknown")]
    public void ProviderCatalogMetadata_DoesNotInventMediaFacts(string? path)
    {
        var song = ProtocolProviderGateway.Map(new ProviderTrackMetadata(
            new("deezer", ProviderResourceKind.Track, "42"), "Sparse", [new("Artist")]));
        Assert.Null(song.LocalPath);
        song.LocalPath = path;
        var builder = new SubsonicResponseBuilder();
        foreach (var format in new[] { "json", "xml" })
            AssertSparseMedia(ReadSong(builder.CreateSongResponse(format, song), format));
    }

    [Fact]
    public void NativeUnknownMedia_RemainsAbsentEvenWithProviderProvenance()
    {
        var song = new Song { Id = "native", IsLocal = true, ExternalProvider = "deezer", Title = "Native" };
        var builder = new SubsonicResponseBuilder();
        foreach (var format in new[] { "json", "xml" })
        {
            var media = ReadSong(builder.CreateSongResponse(format, song), format);
            Assert.False(media.ContainsKey("contentType"));
            Assert.False(media.ContainsKey("suffix"));
        }
    }

    [Theory]
    [InlineData("json", TrackRouteKind.External)]
    [InlineData("xml", TrackRouteKind.External)]
    [InlineData("json", TrackRouteKind.Unresolved)]
    [InlineData("xml", TrackRouteKind.Unresolved)]
    [InlineData("json", TrackRouteKind.Local)]
    [InlineData("xml", TrackRouteKind.Local)]
    public async Task VirtualPlaylist_OneSparseRowDoesNotBreakClientNormalization(string format, TrackRouteKind route)
    {
        const string native = """{"id":"native","title":"Native","contentType":"audio/flac","suffix":"flac","bitRate":901,"size":4500,"future":"kept"}""";
        var linkId = Guid.Parse("0198a537-719c-7ea8-9e5a-17e1f2f963f0");
        var id = PlaylistVirtualizationService.CreateProtocolId(linkId);
        var playlist = new VirtualPlaylistReadModel(id, linkId, Guid.NewGuid(), "Fixture", null, null,
            "spotify", "fixture", "1", PlaylistLinkMode.Virtual,
            [
                new(0, "native", "Native", "Artist", null, null, null, null,
                    TrackMatchState.Accepted, RouteKind: TrackRouteKind.Local, NativeEntryJson: native),
                new(1, route == TrackRouteKind.External ? "ext-deezer-song-42" : "allstarr-unresolved-fixture",
                    "Sparse", "Artist", null, null, null, null, TrackMatchState.Unresolved, RouteKind: route)
            ]);
        var service = new Mock<IPlaylistVirtualizationService>(MockBehavior.Strict);
        service.Setup(item => item.ReadAsync(It.IsAny<ProtocolExecutionContext>(), id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(playlist);
        var context = new ProtocolExecutionContext(ProtocolKind.Subsonic, "backend", "viewer", null,
            "fixture", DateTimeOffset.UtcNow.AddMinutes(1), default);
        var adapter = new SubsonicVirtualPlaylistProtocolAdapter(service.Object,
            new Mock<ISubsonicPlaylistMutationResolver>(MockBehavior.Strict).Object);

        var result = await adapter.ReadAsync(context, id, format, default);

        Dictionary<string, string?>[] entries;
        if (format == "json")
        {
            var root = JsonSerializer.SerializeToElement(Assert.IsType<JsonResult>(result).Value);
            var songs = root.GetProperty("subsonic-response").GetProperty("playlist").GetProperty("entry");
            Assert.Equal(native, songs[0].GetRawText());
            entries = songs.EnumerateArray().Select(ReadJson).ToArray();
        }
        else
        {
            entries = XDocument.Parse(Assert.IsType<ContentResult>(result).Content!).Descendants()
                .Where(element => element.Name.LocalName == "entry").Select(ReadXml).ToArray();
            Assert.Equal("audio/flac", entries[0]["contentType"]);
            Assert.Equal("901", entries[0]["bitRate"]);
            Assert.Equal("4500", entries[0]["size"]);
            Assert.Equal("kept", entries[0]["future"]);
        }
        Assert.Equal(2, entries.Length);
        if (route == TrackRouteKind.Local)
        {
            Assert.False(entries[1].ContainsKey("contentType"));
            Assert.False(entries[1].ContainsKey("suffix"));
        }
        else AssertSparseMedia(entries[1]);
        service.VerifyAll();
        service.VerifyNoOtherCalls();
    }

    internal static void AssertSparseMedia(IReadOnlyDictionary<string, string?> song)
    {
        // Feishin calls startsWith before it returns any normalized song in a collection.
        var contentType = song["contentType"]!;
        var container = contentType.StartsWith("audio/", StringComparison.Ordinal) ? contentType.Split('/')[1] : null;
        Assert.False(string.IsNullOrWhiteSpace(contentType));
        Assert.NotEqual(string.Empty, container);
        Assert.Equal("application/octet-stream", contentType);
        Assert.False(song.ContainsKey("suffix"));
        // Tempo copies these optional fields without substituting measured media facts.
        Assert.False(song.ContainsKey("bitRate"));
        Assert.False(song.ContainsKey("size"));
    }

    private static Dictionary<string, string?> ReadSong(IActionResult result, string format) => format == "json"
        ? ReadJson(JsonSerializer.SerializeToElement(Assert.IsType<JsonResult>(result).Value)
            .GetProperty("subsonic-response").GetProperty("song"))
        : ReadXml(XDocument.Parse(Assert.IsType<ContentResult>(result).Content!).Root!.Elements().Single());

    internal static Dictionary<string, string?> ReadJson(JsonElement song) => song.EnumerateObject()
        .ToDictionary(property => property.Name, property => (string?)property.Value.ToString(), StringComparer.Ordinal);

    internal static Dictionary<string, string?> ReadXml(XElement song) => song.Attributes()
        .ToDictionary(attribute => attribute.Name.LocalName, attribute => (string?)attribute.Value, StringComparer.Ordinal);
}

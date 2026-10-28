using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Protocols.Jellyfin;
using allstarr.Models.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    private const string MixProviderSongId = "ext-deezer-song-43";
    private const string MixNativeSongId = "native-song-43";

    [Theory]
    [InlineData("Similar", "ext-deezer-song-44,native-song-43,ext-deezer-song-45")]
    [InlineData("InstantMix", "ext-deezer-song-42,ext-deezer-song-44,native-song-43,ext-deezer-song-45")]
    public async Task JellyfinMixNativeReplacementPreservesRecommendationOrder(string method, string expected)
    {
        var gateway = MixGateway();
        var limit = method == "InstantMix" ? 4 : 3;
        gateway.Setup(service => service.GetSimilarSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42", limit))
            .ReturnsAsync([MixSong("ext-deezer-song-44", "First"), MixSong(MixProviderSongId, "Second"),
                MixSong("ext-deezer-song-45", "Third")]);
        var index = new Mock<ILibraryIndexService>();
        index.Setup(service => service.FindKnownRecordingTracksAsync(It.IsAny<ProtocolExecutionContext>(),
                It.IsAny<IReadOnlyList<LibraryRecordingLookup>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProtocolExecutionContext _, IReadOnlyList<LibraryRecordingLookup> lookups, CancellationToken _) =>
                (IReadOnlyDictionary<string, string>)(lookups.Any(lookup => lookup.PublicId == MixProviderSongId)
                    ? new Dictionary<string, string> { [MixProviderSongId] = MixNativeSongId }
                    : new Dictionary<string, string>()));
        var interaction = new Mock<IJellyfinInteractionProtocolAdapter>();
        interaction.Setup(adapter => adapter.CanRunOptionalUserWork(It.IsAny<ProtocolExecutionContext?>())).Returns(true);
        interaction.Setup(adapter => adapter.ShapeInstantMix(It.IsAny<IReadOnlyList<Dictionary<string, object?>>>()))
            .Returns((IReadOnlyList<Dictionary<string, object?>> items) => new JellyfinInteractionProtocolAdapter().ShapeInstantMix(items));
        using var factory = new ProtocolFactory("Jellyfin", request => request.RequestUri!.AbsolutePath switch
        {
            "/Users/Me" => Json(200, "{\"Id\":\"user-1\"}"),
            "/Users/user-1/Items/native-song-43" => Json(200,
                "{\"Id\":\"native-song-43\",\"Type\":\"Audio\",\"Name\":\"Native second\",\"FutureField\":\"retained\"}"),
            _ => throw new InvalidOperationException("Unexpected backend request")
        }, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>(); services.AddSingleton(gateway.Object);
            services.RemoveAll<ILibraryIndexService>(); services.AddSingleton(index.Object);
            services.RemoveAll<IJellyfinInteractionProtocolAdapter>(); services.AddSingleton(interaction.Object);
        });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/Items/ext-deezer-song-42/{method}?Limit={limit}&api_key=fixture-key");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = document.RootElement.GetProperty("Items").EnumerateArray().ToArray();
        Assert.Equal(expected.Split(','), items.Select(item => item.GetProperty("Id").GetString()));
        Assert.Equal("retained", items.Single(item => item.GetProperty("Id").GetString() == MixNativeSongId)
            .GetProperty("FutureField").GetString());
    }

    [Theory]
    [InlineData("/Items/ext-deezer-song-42/Similar?Limit=1&api_key=fixture-key", true)]
    [InlineData("/Items/ext-deezer-song-42/Similar?Limit=1&api_key=fixture-key", false)]
    [InlineData("/Items/ext-deezer-song-42/InstantMix?Limit=2&api_key=fixture-key", true)]
    [InlineData("/Items/ext-deezer-song-42/InstantMix?Limit=2&api_key=fixture-key", false)]
    public async Task JellyfinExternalMix_ProjectsReadableNativePayloadAndRetainsUnreadableProvider(
        string path, bool nativeReadable)
    {
        var gateway = MixGateway();
        var index = MixIndex();
        var isRadio = path.Contains("InstantMix", StringComparison.Ordinal);
        var expectedCount = isRadio ? 2 : 1;
        var interaction = new Mock<IJellyfinInteractionProtocolAdapter>(MockBehavior.Strict);
        interaction.Setup(adapter => adapter.CanRunOptionalUserWork(It.IsAny<ProtocolExecutionContext?>()))
            .Returns(true);
        interaction.Setup(adapter => adapter.ShapeInstantMix(
                It.Is<IReadOnlyList<Dictionary<string, object?>>>(items => items.Count == expectedCount)))
            .Returns((IReadOnlyList<Dictionary<string, object?>> items) =>
                new JellyfinInteractionProtocolAdapter().ShapeInstantMix(items));

        var nativeLookups = new List<string>();
        using var factory = new ProtocolFactory(
            "Jellyfin",
            request =>
            {
                var requestPath = request.RequestUri!.AbsolutePath;
                if (requestPath == "/Users/Me")
                    return Json(StatusCodes.Status200OK, "{\"Id\":\"user-1\"}");
                if (requestPath == $"/Users/user-1/Items/{MixNativeSongId}")
                {
                    nativeLookups.Add(requestPath);
                    return nativeReadable
                        ? Json(StatusCodes.Status200OK,
                            "{\"Id\":\"native-song-43\",\"Type\":\"Audio\",\"Name\":\"Native related\",\"MediaType\":\"Audio\",\"RunTimeTicks\":32100000,\"UnknownFutureField\":{\"Keep\":\"native\"}}")
                        : Json(StatusCodes.Status404NotFound, "{}");
                }
                throw new InvalidOperationException($"Unexpected upstream request: {request.RequestUri}");
            },
            services =>
            {
                services.RemoveAll<IProtocolProviderGateway>();
                services.AddSingleton(gateway.Object);
                services.RemoveAll<ILibraryIndexService>();
                services.AddSingleton(index.Object);
                services.RemoveAll<IJellyfinInteractionProtocolAdapter>();
                services.AddSingleton(interaction.Object);
            });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = body.RootElement.GetProperty("Items").EnumerateArray().ToArray();
        Assert.Equal(expectedCount, items.Length);
        if (isRadio) Assert.Equal("ext-deezer-song-42", items[0].GetProperty("Id").GetString());
        var item = items[^1];

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        if (nativeReadable)
        {
            Assert.Equal(MixNativeSongId, item.GetProperty("Id").GetString());
            Assert.Equal("Native related", item.GetProperty("Name").GetString());
            Assert.Equal("Audio", item.GetProperty("MediaType").GetString());
            Assert.Equal("native", item.GetProperty("UnknownFutureField").GetProperty("Keep").GetString());
        }
        else
        {
            Assert.Equal(MixProviderSongId, item.GetProperty("Id").GetString());
            Assert.Equal("Related [A]", item.GetProperty("Name").GetString());
            Assert.False(item.TryGetProperty("UnknownFutureField", out _));
        }

        Assert.Equal(["/Users/user-1/Items/native-song-43"], nativeLookups);
        index.Verify(item => item.FindKnownRecordingTracksAsync(
            It.Is<ProtocolExecutionContext>(context => context.Protocol == ProtocolKind.Jellyfin &&
                context.VerifiedBackendPrincipalId == "user-1"),
            It.Is<IReadOnlyList<LibraryRecordingLookup>>(lookups =>
                lookups.Any(lookup => lookup.PublicId == MixProviderSongId)),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        VerifyMixGateway(gateway);
        if (isRadio && nativeReadable)
            gateway.Verify(service => service.CanPlaySongAsync(It.IsAny<ProtocolExecutionContext>(),
                It.Is<Song>(song => song.Id == MixProviderSongId)), Times.Never);
        if (isRadio)
            interaction.Verify(adapter => adapter.ShapeInstantMix(
                It.Is<IReadOnlyList<Dictionary<string, object?>>>(items => items.Count == expectedCount)), Times.Once);
    }

    [Theory]
    [InlineData("json", true)]
    [InlineData("json", false)]
    [InlineData("xml", true)]
    [InlineData("xml", false)]
    public async Task SubsonicSimilarSongs2_ProjectsReadableNativePayloadAndRetainsUnreadableProvider(
        string format, bool nativeReadable)
    {
        var gateway = MixGateway();
        var index = MixIndex();
        var nativeLookups = new List<string>();
        using var factory = new ProtocolFactory(
            "Subsonic",
            request =>
            {
                var requestPath = request.RequestUri!.AbsolutePath;
                if (requestPath == "/rest/ping.view")
                    return Json(StatusCodes.Status200OK,
                        "{\"subsonic-response\":{\"status\":\"ok\",\"version\":\"1.16.1\"}}");
                if (requestPath == "/rest/getSong")
                {
                    var id = QueryHelpers.ParseQuery(request.RequestUri.Query)["id"].ToString();
                    Assert.Equal(MixNativeSongId, id);
                    nativeLookups.Add(id);
                    if (!nativeReadable)
                        return Json(StatusCodes.Status404NotFound, "{}");
                    return format == "json"
                        ? Json(StatusCodes.Status200OK,
                            "{\"subsonic-response\":{\"status\":\"ok\",\"version\":\"1.16.1\",\"song\":{\"id\":\"native-song-43\",\"title\":\"Native related\",\"artist\":\"Native Artist\",\"album\":\"Native Album\",\"duration\":321,\"futureField\":{\"keep\":\"native\"}}}}")
                        : Xml("<subsonic-response xmlns=\"http://subsonic.org/restapi\" status=\"ok\" version=\"1.16.1\"><song id=\"native-song-43\" title=\"Native related\" artist=\"Native Artist\" album=\"Native Album\" duration=\"321\" futureField=\"native\"/></subsonic-response>");
                }
                throw new InvalidOperationException($"Unexpected upstream request: {request.RequestUri}");
            },
            services =>
            {
                services.RemoveAll<IProtocolProviderGateway>();
                services.AddSingleton(gateway.Object);
                services.RemoveAll<ILibraryIndexService>();
                services.AddSingleton(index.Object);
            });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            $"/rest/getSimilarSongs2.view?u=fixture&p=fixture-password&v=1.16.1&c=fixture&f={format}&id=ext-deezer-song-42&count=2");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(nativeLookups);
        if (format == "json")
        {
            using var document = JsonDocument.Parse(body);
            var songs = document.RootElement.GetProperty("subsonic-response")
                .GetProperty("similarSongs2").GetProperty("song").EnumerateArray().ToArray();
            Assert.Equal(2, songs.Length);
            Assert.Equal("ext-deezer-song-42", songs[0].GetProperty("id").GetString());
            var song = songs[1];
            Assert.Equal(nativeReadable ? MixNativeSongId : MixProviderSongId, song.GetProperty("id").GetString());
            if (nativeReadable)
            {
                Assert.Equal("Native related", song.GetProperty("title").GetString());
                Assert.Equal("Native Artist", song.GetProperty("artist").GetString());
                Assert.Equal("native", song.GetProperty("futureField").GetProperty("keep").GetString());
            }
        }
        else
        {
            var songs = XDocument.Parse(body).Descendants(
                XName.Get("song", "http://subsonic.org/restapi")).ToArray();
            Assert.Equal(2, songs.Length);
            Assert.Equal("ext-deezer-song-42", songs[0].Attribute("id")?.Value);
            var song = songs[1];
            Assert.Equal(nativeReadable ? MixNativeSongId : MixProviderSongId, song.Attribute("id")?.Value);
            if (nativeReadable)
            {
                Assert.Equal("Native related", song.Attribute("title")?.Value);
                Assert.Equal("Native Artist", song.Attribute("artist")?.Value);
                Assert.Equal("native", song.Attribute("futureField")?.Value);
            }
        }

        index.Verify(item => item.FindKnownRecordingTracksAsync(
            It.Is<ProtocolExecutionContext>(context => context.Protocol == ProtocolKind.Subsonic &&
                context.VerifiedBackendPrincipalId == "fixture"),
            It.Is<IReadOnlyList<LibraryRecordingLookup>>(lookups =>
                lookups.Any(lookup => lookup.PublicId == MixProviderSongId)),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        VerifyMixGateway(gateway);
        if (nativeReadable)
            gateway.Verify(service => service.CanPlaySongAsync(It.IsAny<ProtocolExecutionContext>(),
                It.Is<Song>(song => song.Id == MixProviderSongId)), Times.Never);
    }

    private static Mock<IProtocolProviderGateway> MixGateway()
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(service => service.GetSongAsync(
                It.IsAny<ProtocolExecutionContext>(), "deezer", "42"))
            .ReturnsAsync(MixSong("ext-deezer-song-42", "Seed"));
        gateway.Setup(service => service.GetSimilarSongsAsync(
                It.IsAny<ProtocolExecutionContext>(), "deezer", "42", It.IsAny<int>()))
            .ReturnsAsync([MixSong(MixProviderSongId, "Related")]);
        gateway.Setup(service => service.CanPlaySongAsync(
                It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>()))
            .ReturnsAsync(true);
        return gateway;
    }

    private static void VerifyMixGateway(Mock<IProtocolProviderGateway> gateway)
    {
        gateway.Verify(service => service.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42"), Times.Once);
        gateway.Verify(service => service.GetSimilarSongsAsync(
            It.IsAny<ProtocolExecutionContext>(), "deezer", "42", It.IsAny<int>()), Times.Once);
    }

    private static Mock<ILibraryIndexService> MixIndex()
    {
        var index = new Mock<ILibraryIndexService>(MockBehavior.Strict);
        index.Setup(service => service.FindKnownRecordingTracksAsync(
                It.IsAny<ProtocolExecutionContext>(),
                It.Is<IReadOnlyList<LibraryRecordingLookup>>(lookups =>
                    lookups.Any(lookup => lookup.PublicId == MixProviderSongId)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string> { [MixProviderSongId] = MixNativeSongId });
        index.Setup(service => service.FindKnownRecordingTracksAsync(
                It.IsAny<ProtocolExecutionContext>(),
                It.Is<IReadOnlyList<LibraryRecordingLookup>>(lookups =>
                    lookups.All(lookup => lookup.PublicId != MixProviderSongId)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());
        return index;
    }

    private static Song MixSong(string id, string title) => new()
    {
        Id = id,
        Title = title,
        Artist = "Fixture Artist",
        ExternalProvider = "deezer",
        ExternalId = id[(id.LastIndexOf('-') + 1)..],
        IsLocal = false
    };

    private static HttpResponseMessage Xml(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/xml")
    };
}

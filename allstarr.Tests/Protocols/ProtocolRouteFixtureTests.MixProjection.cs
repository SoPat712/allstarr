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
    [InlineData("/Items/ext-deezer-song-42/Similar?Limit=1&api_key=fixture-key", true)]
    [InlineData("/Items/ext-deezer-song-42/Similar?Limit=1&api_key=fixture-key", false)]
    [InlineData("/Items/ext-deezer-song-42/InstantMix?Limit=1&api_key=fixture-key", true)]
    [InlineData("/Items/ext-deezer-song-42/InstantMix?Limit=1&api_key=fixture-key", false)]
    public async Task JellyfinExternalMix_ProjectsReadableNativePayloadAndRetainsUnreadableProvider(
        string path, bool nativeReadable)
    {
        var gateway = MixGateway();
        var index = MixIndex();
        var interaction = new Mock<IJellyfinInteractionProtocolAdapter>(MockBehavior.Strict);
        interaction.Setup(adapter => adapter.CanRunOptionalUserWork(It.IsAny<ProtocolExecutionContext?>()))
            .Returns(true);
        interaction.Setup(adapter => adapter.ShapeInstantMix(
                It.Is<IReadOnlyList<Dictionary<string, object?>>>(items => items.Count == 1)))
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
        var item = Assert.Single(body.RootElement.GetProperty("Items").EnumerateArray());

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
                lookups.Count == 1 && lookups[0].PublicId == MixProviderSongId),
            It.IsAny<CancellationToken>()), Times.Once);
        gateway.VerifyAll();
        if (path.Contains("InstantMix", StringComparison.Ordinal))
            interaction.Verify(adapter => adapter.ShapeInstantMix(
                It.Is<IReadOnlyList<Dictionary<string, object?>>>(items => items.Count == 1)), Times.Once);
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
            $"/rest/getSimilarSongs2.view?u=fixture&p=fixture-password&v=1.16.1&c=fixture&f={format}&id=ext-deezer-song-42&count=1");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(nativeLookups);
        if (format == "json")
        {
            using var document = JsonDocument.Parse(body);
            var song = Assert.Single(document.RootElement.GetProperty("subsonic-response")
                .GetProperty("similarSongs2").GetProperty("song").EnumerateArray());
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
            var song = Assert.Single(XDocument.Parse(body).Descendants(
                XName.Get("song", "http://subsonic.org/restapi")));
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
                lookups.Count == 1 && lookups[0].PublicId == MixProviderSongId),
            It.IsAny<CancellationToken>()), Times.Once);
        gateway.VerifyAll();
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
        return gateway;
    }

    private static Mock<ILibraryIndexService> MixIndex()
    {
        var index = new Mock<ILibraryIndexService>(MockBehavior.Strict);
        index.Setup(service => service.FindKnownRecordingTracksAsync(
                It.IsAny<ProtocolExecutionContext>(),
                It.Is<IReadOnlyList<LibraryRecordingLookup>>(lookups =>
                    lookups.Count == 1 && lookups[0].PublicId == MixProviderSongId),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string> { [MixProviderSongId] = MixNativeSongId });
        return index;
    }

    private static Song MixSong(string id, string title) => new()
    {
        Id = id,
        Title = title,
        Artist = "Fixture Artist",
        ExternalProvider = "deezer",
        ExternalId = id.EndsWith("42", StringComparison.Ordinal) ? "42" : "43",
        IsLocal = false
    };

    private static HttpResponseMessage Xml(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/xml")
    };
}

using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Protocols.Jellyfin;
using allstarr.Models.Domain;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData("Jellyfin", "/Songs/ext-deezer-song-42/InstantMix?Limit=500&api_key=fixture-key", "json", "")]
    [InlineData("Jellyfin", "/Items/ext-deezer-song-42/InstantMix?Limit=500&api_key=fixture-key", "json", "")]
    [InlineData("Subsonic", "/rest/getSimilarSongs.view?u=fixture&p=fixture-password&v=1.16.1&c=fixture&f=json&id=ext-deezer-song-42&count=500", "json", "similarSongs")]
    [InlineData("Subsonic", "/rest/getSimilarSongs2.view?u=fixture&p=fixture-password&v=1.16.1&c=fixture&f=xml&id=ext-deezer-song-42&count=500", "xml", "similarSongs2")]
    public async Task ExternalSongRadio_KeepsTheSeedFirstAndCapsTheProviderRequestAndResponse(
        string backend, string path, string format, string element)
    {
        var seed = RadioSong("42", "Seed");
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(service => service.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42"))
            .ReturnsAsync(seed);
        gateway.Setup(service => service.GetSimilarSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42", 50))
            .ReturnsAsync(new[] { seed }.Concat(Enumerable.Range(43, 60)
                .Select(id => RadioSong(id.ToString(), $"Recommendation {id}"))).ToList());
        gateway.Setup(service => service.CanPlaySongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>()))
            .ReturnsAsync(true);
        using var factory = RadioFactory(backend, gateway);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path);
        var ids = ReadRadioIds(await response.Content.ReadAsStringAsync(), backend, format, element);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { seed.Id }.Concat(Enumerable.Range(43, 49)
            .Select(id => $"ext-deezer-song-{id}")), ids);
        gateway.Verify(service => service.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42"), Times.Once);
        gateway.Verify(service => service.GetSimilarSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42", 50), Times.Once);
        gateway.Verify(service => service.CanPlaySongAsync(It.IsAny<ProtocolExecutionContext>(),
            It.Is<Song>(song => song.Id == seed.Id)), Times.Once);
        gateway.VerifyAll();
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("Jellyfin", "/Artists/ext-deezer-artist-42/InstantMix?Limit=500&api_key=fixture-key", "json", "")]
    [InlineData("Jellyfin", "/Artists/InstantMix?id=ext-deezer-artist-42&Limit=500&api_key=fixture-key", "json", "")]
    [InlineData("Subsonic", "/rest/getSimilarSongs.view?u=fixture&p=fixture-password&v=1.16.1&c=fixture&f=xml&id=ext-deezer-artist-42&count=500", "xml", "similarSongs")]
    [InlineData("Subsonic", "/rest/getSimilarSongs2.view?u=fixture&p=fixture-password&v=1.16.1&c=fixture&f=json&id=ext-deezer-artist-42&count=500", "json", "similarSongs2")]
    public async Task ExternalArtistRadio_StartsWithTheArtistAndReadsAtMostFiveRelatedArtists(
        string backend, string path, string format, string element)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(service => service.GetArtistAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42"))
            .ReturnsAsync(RadioArtist("42"));
        gateway.Setup(service => service.GetArtistTracksAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42"))
            .ReturnsAsync([RadioSong("own", "Own track"), RadioSong("blocked", "Blocked track")]);
        gateway.Setup(service => service.GetSimilarArtistsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42", 5))
            .ReturnsAsync(Enumerable.Range(0, 7).Select(id => RadioArtist($"related-{id}")).ToList());
        foreach (var id in Enumerable.Range(0, 5))
        {
            var relatedId = $"related-{id}";
            gateway.Setup(service => service.GetArtistTracksAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", relatedId))
                .ReturnsAsync([RadioSong(relatedId, $"Related track {id}")]);
        }
        gateway.Setup(service => service.CanPlaySongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>()))
            .ReturnsAsync((ProtocolExecutionContext _, Song song) => song.ExternalId != "blocked");
        using var factory = RadioFactory(backend, gateway);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path);
        var ids = ReadRadioIds(await response.Content.ReadAsStringAsync(), backend, format, element);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ext-deezer-song-own", ids[0]);
        Assert.Equal(6, ids.Count);
        Assert.Equal(Enumerable.Range(0, 5).Select(id => $"ext-deezer-song-related-{id}").Order(), ids.Skip(1).Order());
        Assert.DoesNotContain("ext-deezer-song-blocked", ids);
        gateway.Verify(service => service.GetArtistAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42"), Times.Once);
        gateway.Verify(service => service.GetArtistTracksAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42"), Times.Once);
        gateway.Verify(service => service.GetSimilarArtistsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42", 5), Times.Once);
        foreach (var id in Enumerable.Range(0, 5))
        {
            var relatedId = $"related-{id}";
            gateway.Verify(service => service.GetArtistTracksAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", relatedId), Times.Once);
        }
        gateway.VerifyAll();
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("Jellyfin", "/Songs/ext-deezer-song-42/InstantMix?Limit=2&api_key=fixture-key", "json", "")]
    [InlineData("Subsonic", "/rest/getSimilarSongs.view?u=fixture&p=fixture-password&v=1.16.1&c=fixture&f=json&id=ext-deezer-song-42&count=2", "json", "similarSongs")]
    [InlineData("Subsonic", "/rest/getSimilarSongs2.view?u=fixture&p=fixture-password&v=1.16.1&c=fixture&f=xml&id=ext-deezer-song-42&count=2", "xml", "similarSongs2")]
    public async Task ExternalSongRadio_DropsUnplayableRowsWithoutCatalogOrSearchFiller(
        string backend, string path, string format, string element)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(service => service.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42"))
            .ReturnsAsync(RadioSong("42", "Seed"));
        gateway.Setup(service => service.GetSimilarSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42", 2))
            .ReturnsAsync([RadioSong("43", "Blocked recommendation")]);
        gateway.Setup(service => service.CanPlaySongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>()))
            .ReturnsAsync(false);
        using var factory = RadioFactory(backend, gateway);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path);
        var ids = ReadRadioIds(await response.Content.ReadAsStringAsync(), backend, format, element);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(ids);
        gateway.VerifyAll();
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("Jellyfin", "/Songs/native-song/InstantMix?Limit=2&customKeep=kept&api_key=fixture-key", "json", "")]
    [InlineData("Jellyfin", "/Artists/native-artist/InstantMix?Limit=2&customKeep=kept&api_key=fixture-key", "json", "")]
    [InlineData("Subsonic", "/rest/getSimilarSongs.view?u=fixture&p=fixture-password&v=1.16.1&c=fixture&f=json&id=native-song&count=2&customKeep=kept", "json", "similarSongs")]
    [InlineData("Subsonic", "/rest/getSimilarSongs2.view?u=fixture&p=fixture-password&v=1.16.1&c=fixture&f=xml&id=native-artist&count=2&customKeep=kept", "xml", "similarSongs2")]
    public async Task NativeRadio_RelaysTheBackendEnvelopeAndQueryWithoutProviderWork(
        string backend, string path, string format, string element)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        var observed = new List<ObservedRequest>();
        var nativeBody = backend == "Jellyfin"
            ? """{"Items":[{"Id":"native-2","Name":"Second","FutureField":"retained"},{"Id":"native-1","Name":"First"}],"TotalRecordCount":2,"StartIndex":0,"FutureEnvelope":"retained"}"""
            : format == "json"
                ? $$$$"""{"subsonic-response":{"status":"ok","version":"1.16.1","futureEnvelope":"retained","{{{{element}}}}":{"song":[{"id":"native-2","title":"Second","futureField":"retained"},{"id":"native-1","title":"First"}]}}}"""
                : $"<subsonic-response xmlns=\"http://subsonic.org/restapi\" status=\"ok\" version=\"1.16.1\" futureEnvelope=\"retained\"><{element}><song id=\"native-2\" title=\"Second\" futureField=\"retained\"/><song id=\"native-1\" title=\"First\"/></{element}></subsonic-response>";
        using var factory = new ProtocolFactory(backend, request =>
        {
            if (RadioAuthenticationResponse(request) is { } authentication) return authentication;
            observed.Add(Observe(request));
            Assert.Equal(new Uri("http://fixture" + path).AbsolutePath, request.RequestUri!.AbsolutePath);
            var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
            Assert.Equal("kept", query["customKeep"].ToString());
            Assert.Equal("2", query[backend == "Jellyfin" ? "Limit" : "count"].ToString());
            return format == "xml" ? Xml(nativeBody) : Json(200, nativeBody);
        }, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["native-2", "native-1"], ReadRadioIds(body, backend, format, element));
        if (backend == "Jellyfin")
        {
            using var document = JsonDocument.Parse(body);
            Assert.Equal("retained", document.RootElement.GetProperty("FutureEnvelope").GetString());
            Assert.Equal("retained", document.RootElement.GetProperty("Items")[0].GetProperty("FutureField").GetString());
        }
        else Assert.Equal(nativeBody, body);
        Assert.Single(observed);
        gateway.VerifyNoOtherCalls();
    }

    private static ProtocolFactory RadioFactory(string backend, Mock<IProtocolProviderGateway> gateway)
    {
        var index = new Mock<ILibraryIndexService>(MockBehavior.Strict);
        index.Setup(service => service.FindKnownRecordingTracksAsync(It.IsAny<ProtocolExecutionContext>(),
                It.IsAny<IReadOnlyList<LibraryRecordingLookup>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());
        var interaction = new Mock<IJellyfinInteractionProtocolAdapter>(MockBehavior.Strict);
        interaction.Setup(adapter => adapter.CanRunOptionalUserWork(It.IsAny<ProtocolExecutionContext?>())).Returns(true);
        interaction.Setup(adapter => adapter.ShapeInstantMix(It.IsAny<IReadOnlyList<Dictionary<string, object?>>>()))
            .Returns((IReadOnlyList<Dictionary<string, object?>> items) => new JellyfinInteractionProtocolAdapter().ShapeInstantMix(items));
        return new ProtocolFactory(backend,
            request => RadioAuthenticationResponse(request) ?? throw new InvalidOperationException($"Unexpected upstream request: {request.RequestUri}"),
            services =>
            {
                services.RemoveAll<IProtocolProviderGateway>();
                services.AddSingleton(gateway.Object);
                services.RemoveAll<ILibraryIndexService>();
                services.AddSingleton(index.Object);
                services.RemoveAll<IJellyfinInteractionProtocolAdapter>();
                services.AddSingleton(interaction.Object);
            }, new Dictionary<string, string?>
            {
                ["Playback:LastFmSimilarEnabled"] = "false",
                ["Playback:ListenBrainzSimilarEnabled"] = "false"
            });
    }

    private static HttpResponseMessage? RadioAuthenticationResponse(HttpRequestMessage request) => request.RequestUri!.AbsolutePath switch
    {
        "/Users/Me" => Json(200, """{"Id":"user-1"}"""),
        "/rest/ping.view" => Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}"""),
        _ => null
    };

    private static IReadOnlyList<string> ReadRadioIds(string body, string backend, string format, string element)
    {
        if (format == "xml")
            return XDocument.Parse(body).Descendants(XName.Get("song", "http://subsonic.org/restapi"))
                .Select(song => song.Attribute("id")!.Value).ToArray();
        using var document = JsonDocument.Parse(body);
        if (backend == "Jellyfin")
            return document.RootElement.GetProperty("Items").EnumerateArray().Select(item => item.GetProperty("Id").GetString()!).ToArray();
        var list = document.RootElement.GetProperty("subsonic-response").GetProperty(element);
        return list.TryGetProperty("song", out var songs)
            ? songs.EnumerateArray().Select(song => song.GetProperty("id").GetString()!).ToArray()
            : [];
    }

    private static Song RadioSong(string id, string title) => new()
    {
        Id = $"ext-deezer-song-{id}",
        ExternalProvider = "deezer",
        ExternalId = id,
        Title = title,
        Artist = "Fixture Artist",
        ArtistId = "ext-deezer-artist-42",
        IsLocal = false
    };

    private static Artist RadioArtist(string id) => new()
    {
        Id = $"ext-deezer-artist-{id}",
        ExternalProvider = "deezer",
        ExternalId = id,
        Name = $"Artist {id}"
    };
}

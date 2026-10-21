using System.Reflection;
using System.Text.Json;
using allstarr.Core.Providers.Deezer;
using allstarr.Core.Providers.Qobuz;
using allstarr.Core.Providers.Spotify;
using allstarr.Models.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed class ProviderSourceDescriptionTests
{
    [Theory]
    [InlineData("deezer")]
    [InlineData("qobuz")]
    public void SourceTrack_AlbumCreditUsesEmbeddedAlbumArtist(string provider)
    {
        using var document = JsonDocument.Parse(provider == "deezer" ? """
            {"id":42,"title":"Guest song","artist":{"id":3,"name":"Guest"},
             "album":{"id":7,"title":"Album","artist":{"id":2,"name":"Host"}}}
            """ : """
            {"id":42,"title":"Guest song","performer":{"id":3,"name":"Guest"},
             "album":{"id":"7","title":"Album","artist":{"id":2,"name":"Host"}}}
            """);
        var catalog = Catalog(provider);
        var song = Assert.IsType<Song>(Parse(catalog, provider == "deezer" ? "ParseDeezerTrack" : "ParseQobuzTrack", document.RootElement));
        var item = JsonSerializer.SerializeToElement(song);
        var artist = Assert.Single(item.GetProperty("AlbumArtists").EnumerateArray());
        Assert.Equal($"ext-{provider}-artist-2", artist.GetProperty("Id").GetString());
        Assert.Equal("Host", artist.GetProperty("Name").GetString());
        Assert.Equal($"ext-{provider}-artist-3", song.ArtistId);
    }

    [Theory]
    [InlineData("deezer")]
    [InlineData("qobuz")]
    public void SourceAlbum_PreservesAvailableLabelAndDescription(string provider)
    {
        using var document = JsonDocument.Parse(provider == "deezer" ? """
            {"id":7,"title":"Album","artist":{"id":2,"name":"Host"},"label":"Small label"}
            """ : """
            {"id":"7","title":"Album","artist":{"id":2,"name":"Host"},"label":{"name":"Small label"},"description":"Source album notes"}
            """);
        var album = Assert.IsType<Album>(Parse(Catalog(provider), provider == "deezer" ? "ParseDeezerAlbum" : "ParseQobuzAlbum", document.RootElement));
        var item = JsonSerializer.SerializeToElement(album);
        Assert.Equal("Small label", item.GetProperty("Label").GetString());
        if (provider == "qobuz") Assert.Equal("Source album notes", item.GetProperty("Notes").GetString());
    }

    [Fact]
    public void SourceArtist_PreservesAvailableBiography()
    {
        using var document = JsonDocument.Parse("""{"id":2,"name":"Host","biography":{"content":"Source biography"}}""");
        var artist = Assert.IsType<Artist>(Parse(Catalog("qobuz"), "ParseQobuzArtist", document.RootElement));
        Assert.Equal("Source biography", JsonSerializer.SerializeToElement(artist).GetProperty("Biography").GetString());
    }

    private static object Parse(object catalog, string method, JsonElement value)
    {
        var parser = catalog.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!;
        var arguments = parser.GetParameters().Select(parameter => parameter.Position == 0 ? (object)value : parameter.DefaultValue).ToArray();
        return parser.Invoke(catalog, arguments)!;
    }

    private static object Catalog(string provider)
    {
        var http = new HttpClient(new NoRequests());
        if (provider == "deezer") return new DeezerProvider(new DeezerHttpClient(http, 0));
        var factory = Mock.Of<IHttpClientFactory>(value => value.CreateClient(It.IsAny<string>()) == http);
        var bundles = new Mock<QobuzBundleService>(factory, NullLogger<QobuzBundleService>.Instance);
        return new QobuzProvider(http, bundles.Object, Mock.Of<IProviderAccountSecretAccessor>(), NullLogger<QobuzProvider>.Instance);
    }

    private sealed class NoRequests : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Unexpected provider request.");
    }
}

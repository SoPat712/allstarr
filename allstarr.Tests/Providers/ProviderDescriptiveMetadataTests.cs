using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Protocols;
using allstarr.Core.Providers;
using allstarr.Models.Domain;
using Moq;

namespace allstarr.Tests;

public sealed class ProviderDescriptiveMetadataTests
{
    [Fact]
    public void TrackRoundTrip_PreservesActualAlbumCreditsAndIdentifiers()
    {
        var song = JsonSerializer.Deserialize<Song>("""
            {
              "ExternalId":"track-1", "Title":"Guest song", "Artist":"Guest",
              "Artists":["Guest"], "ArtistIds":["guest-1"],
              "Album":"Collected songs", "AlbumId":"album-1", "AlbumArtist":"Ensemble",
              "AlbumArtists":[{"Name":"Ensemble","ExternalId":"ensemble-1","ExternalProvider":"deezer"}],
              "ProviderIds":{"MusicBrainz":"recording-1","CustomCatalog":"track-77"},
              "Label":"Small label", "Copyright":"2026 Small label", "Duration":183
            }
            """)!;
        var catalog = new Mock<ProviderCatalogMetadata>(MockBehavior.Strict, "deezer").Object;

        var metadata = catalog.MapTrack(song);
        var result = JsonSerializer.SerializeToElement(ProtocolProviderGateway.Map(metadata));

        Assert.Equal("Guest", result.GetProperty("Artist").GetString());
        Assert.Equal("Ensemble", result.GetProperty("AlbumArtist").GetString());
        var credit = Assert.Single(Property(result, "AlbumArtists").EnumerateArray());
        Assert.Equal("Ensemble", credit.GetProperty("Name").GetString());
        Assert.Equal("ext-deezer-artist-ensemble-1", credit.GetProperty("Id").GetString());
        Assert.Equal("ensemble-1", credit.GetProperty("ExternalId").GetString());
        Assert.Equal("deezer", credit.GetProperty("ExternalProvider").GetString());
        Assert.Equal("recording-1", Property(result, "ProviderIds").GetProperty("MusicBrainz").GetString());
        Assert.Equal("track-77", Property(result, "ProviderIds").GetProperty("CustomCatalog").GetString());
        Assert.Equal("Small label", result.GetProperty("Label").GetString());
        Assert.Equal(183, result.GetProperty("Duration").GetInt32());
    }

    [Fact]
    public async Task AlbumRoundTrip_PreservesDescriptionsAndInheritsCreditsOnlyWhenAbsent()
    {
        var album = JsonSerializer.Deserialize<Album>("""
            {
              "ExternalId":"album-1", "Title":"Collected songs", "Artist":"Ensemble", "ArtistId":"ensemble-1",
              "Artists":[
                {"Name":"Ensemble","ExternalId":"ensemble-1","ExternalProvider":"deezer"},
                {"Name":"Partner","Id":"ext-deezer-artist-partner-1"}
              ],
              "Label":"Small label", "Notes":"First paragraph.\nSecond paragraph.",
              "ProviderIds":{"MusicBrainz":"release-1"}, "LastFmUrl":"https://www.last.fm/music/Ensemble/Collected+songs",
              "Songs":[
                {"ExternalId":"guest-1","Title":"Guest song","Artist":"Guest","ArtistIds":["guest-artist"]},
                {"ExternalId":"solo-1","Title":"Solo song","Artist":"Soloist",
                 "AlbumArtists":[{"Name":"Soloist","ExternalId":"soloist-1","ExternalProvider":"deezer"}]}
              ]
            }
            """)!;
        var catalog = new Mock<ProviderCatalogMetadata>(MockBehavior.Strict, "deezer") { CallBase = true };
        catalog.Setup(item => item.GetAlbumAsync("deezer", "album-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(album);

        var metadata = (await catalog.Object.GetAlbumAsync(Context(), new ProviderAlbumLookupRequest(
            new("deezer", ProviderResourceKind.Album, "album-1")))).RequireValue();
        var result = JsonSerializer.SerializeToElement(ProtocolProviderGateway.Map(metadata));

        Assert.Equal("Small label", Property(result, "Label").GetString());
        Assert.Equal("First paragraph.\nSecond paragraph.", Property(result, "Notes").GetString());
        Assert.Equal("release-1", Property(result, "ProviderIds").GetProperty("MusicBrainz").GetString());
        Assert.Equal("https://www.last.fm/music/Ensemble/Collected+songs", Property(result, "LastFmUrl").GetString());
        Assert.Equal(new[] { "Ensemble", "Partner" }, Property(result, "Artists").EnumerateArray()
            .Select(item => item.GetProperty("Name").GetString()));
        var tracks = result.GetProperty("Songs");
        Assert.Equal("Guest", tracks[0].GetProperty("Artist").GetString());
        Assert.Equal(new[] { "Ensemble", "Partner" }, Property(tracks[0], "AlbumArtists").EnumerateArray()
            .Select(item => item.GetProperty("Name").GetString()));
        Assert.Equal("ext-deezer-artist-ensemble-1", Property(tracks[0], "AlbumArtists")[0].GetProperty("Id").GetString());
        Assert.Equal("Soloist", Assert.Single(Property(tracks[1], "AlbumArtists").EnumerateArray())
            .GetProperty("Name").GetString());
        catalog.Verify(item => item.GetAlbumAsync("deezer", "album-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ArtistRoundTrip_PreservesBiographyIdentifiersAndLink()
    {
        var artist = JsonSerializer.Deserialize<Artist>("""
            {
              "ExternalId":"artist-1", "Name":"Ensemble",
              "Biography":"Formed in 2001.\nA second paragraph.",
              "ProviderIds":{"MusicBrainz":"artist-guid","CustomCatalog":"artist-77"},
              "LastFmUrl":"https://www.last.fm/music/Ensemble"
            }
            """)!;
        var catalog = new Mock<ProviderCatalogMetadata>(MockBehavior.Strict, "deezer") { CallBase = true };
        catalog.Setup(item => item.GetArtistAsync("deezer", "artist-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(artist);

        var metadata = (await catalog.Object.GetArtistAsync(Context(), new ProviderArtistLookupRequest(
            new("deezer", ProviderResourceKind.Artist, "artist-1")))).RequireValue();
        var result = JsonSerializer.SerializeToElement(ProtocolProviderGateway.Map(metadata));

        Assert.Equal("Formed in 2001.\nA second paragraph.", Property(result, "Biography").GetString());
        Assert.Equal("artist-guid", Property(result, "ProviderIds").GetProperty("MusicBrainz").GetString());
        Assert.Equal("artist-77", Property(result, "ProviderIds").GetProperty("CustomCatalog").GetString());
        Assert.Equal("https://www.last.fm/music/Ensemble", Property(result, "LastFmUrl").GetString());
    }

    [Fact]
    public void MissingMetadata_RemainsEmptyWithoutInventedCreditsOrIdentifiers()
    {
        var track = ProtocolProviderGateway.Map(new ProviderTrackMetadata(
            new("deezer", ProviderResourceKind.Track, "track-1"), "Track", [new("Guest")]));
        var album = ProtocolProviderGateway.Map(new ProviderAlbumMetadata(
            new("deezer", ProviderResourceKind.Album, "album-1"), "Album", [new("Ensemble")]));
        var artist = ProtocolProviderGateway.Map(new ProviderArtistMetadata(
            new("deezer", ProviderResourceKind.Artist, "artist-1"), "Ensemble"));
        var trackJson = JsonSerializer.SerializeToElement(track);
        var albumJson = JsonSerializer.SerializeToElement(album);
        var artistJson = JsonSerializer.SerializeToElement(artist);

        Assert.Empty(Property(trackJson, "AlbumArtists").EnumerateArray());
        Assert.Empty(Property(trackJson, "ProviderIds").EnumerateObject());
        Assert.Empty(Property(albumJson, "ProviderIds").EnumerateObject());
        Assert.Equal(JsonValueKind.Null, Property(albumJson, "Label").ValueKind);
        Assert.Equal(JsonValueKind.Null, Property(albumJson, "Notes").ValueKind);
        Assert.Equal(JsonValueKind.Null, Property(albumJson, "LastFmUrl").ValueKind);
        Assert.Empty(Property(artistJson, "ProviderIds").EnumerateObject());
        Assert.Equal(JsonValueKind.Null, Property(artistJson, "Biography").ValueKind);
        Assert.Equal(JsonValueKind.Null, Property(artistJson, "LastFmUrl").ValueKind);
    }

    [Fact]
    public void TrackContract_RejectsAlbumCreditFromAnotherProvider()
    {
        var constructor = typeof(ProviderTrackMetadata).GetConstructors().Single();
        var parameters = constructor.GetParameters();
        var albumCredits = Array.FindIndex(parameters, parameter => parameter.Name == "albumArtists");
        Assert.True(albumCredits >= 0, "Track metadata must expose album artist credits.");
        var values = parameters.Select(parameter => parameter.HasDefaultValue ? parameter.DefaultValue : null).ToArray();
        values[0] = new ProviderExternalResourceId("deezer", ProviderResourceKind.Track, "track-1");
        values[1] = "Track";
        values[2] = new[] { new ProviderArtistCredit("Guest") };
        values[albumCredits] = new[] { new ProviderArtistCredit("Ensemble", new("qobuz", ProviderResourceKind.Artist, "artist-1")) };

        var exception = Assert.Throws<System.Reflection.TargetInvocationException>(() => constructor.Invoke(values));

        Assert.IsType<ArgumentException>(exception.InnerException);
    }

    private static JsonElement Property(JsonElement element, string name)
    {
        Assert.True(element.TryGetProperty(name, out var property), $"Missing metadata property: {name}");
        return property;
    }

    private static ProviderExecutionContext Context() => new(
        new ProviderActorContext(ProviderActorKind.User, Guid.CreateVersion7(),
            new ProviderBackendPrincipal("jellyfin", "fixture", "fixture-user")),
        "deezer", null,
        new ProviderExecutionPolicy(
            new ProviderQualityPolicy(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, allowTranscode: true),
            ProviderExplicitContentPolicy.Allow, allowFallback: true, allowSharedAccount: false,
            allowManagedDownloads: false, allowedProviderIds: ["deezer"]),
        operationId: "metadata-preservation", correlationId: "metadata-fixture",
        deadline: DateTimeOffset.UtcNow.AddMinutes(1), cancellationToken: default);
}

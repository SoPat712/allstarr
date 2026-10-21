using System.Text.Json;
using System.Xml.Linq;
using allstarr.Models.Domain;
using allstarr.Services.Jellyfin;
using allstarr.Services.Subsonic;
using Microsoft.AspNetCore.Http;

namespace allstarr.Tests;

public sealed class ProviderDescriptiveResponseTests
{
    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    public void FeishinSongCredits_KeepAlbumArtistIdentityAndContributorShapes(string format)
    {
        var song = GuestSong();
        var builder = new SubsonicResponseBuilder();
        if (format == "json")
        {
            var item = JsonSerializer.SerializeToElement(builder.ConvertSongToJson(song));
            var artist = Assert.Single(item.GetProperty("albumArtists").EnumerateArray());
            Assert.Equal("ext-deezer-artist-2", artist.GetProperty("id").GetString());
            Assert.Equal("Host", artist.GetProperty("name").GetString());
            var composer = Assert.Single(item.GetProperty("contributors").EnumerateArray(), value => value.GetProperty("role").GetString() == "composer");
            Assert.Equal("Writer", composer.GetProperty("artist").GetProperty("name").GetString());
            Assert.Equal("recording-id", item.GetProperty("musicBrainzId").GetString());
            Assert.Equal(29, item.GetProperty("releaseDate").GetProperty("day").GetInt32());
        }
        else
        {
            var item = builder.ConvertSongToXml(song, "http://subsonic.org/restapi");
            var artist = Assert.Single(item.Elements(), value => value.Name.LocalName == "albumArtists");
            Assert.Equal("ext-deezer-artist-2", artist.Attribute("id")?.Value);
            Assert.Equal("Host", artist.Attribute("name")?.Value);
            var composer = Assert.Single(item.Elements().Where(value => value.Name.LocalName == "contributors"), value => value.Attribute("role")?.Value == "composer");
            Assert.Equal("Writer", composer.Elements().Single().Attribute("name")?.Value);
            Assert.Equal("recording-id", item.Attribute("musicBrainzId")?.Value);
            Assert.Equal("29", item.Elements().Single(value => value.Name.LocalName == "releaseDate").Attribute("day")?.Value);
        }
    }

    [Theory]
    [InlineData("ChildCount,Tags,Studios,SortName,ProviderIds")]
    [InlineData("Genres,DateCreated,MediaSources,ParentId,Tags,SortName,ProviderIds,People")]
    public void JellyfinClients_GuestSongNavigatesToActualAlbumArtist(string fields)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = "/Items";
        http.Request.QueryString = new("?Fields=" + fields);
        var builder = new JellyfinResponseBuilder(httpContexts: new HttpContextAccessor { HttpContext = http });
        var item = JsonSerializer.SerializeToElement(builder.ConvertSongToJellyfinItem(GuestSong()));
        var albumArtist = Assert.Single(item.GetProperty("AlbumArtists").EnumerateArray());
        Assert.Equal("ext-deezer-artist-2", albumArtist.GetProperty("Id").GetString());
        Assert.Equal("Host [D]", albumArtist.GetProperty("Name").GetString());
        Assert.Equal("ext-deezer-artist-3", Assert.Single(item.GetProperty("ArtistItems").EnumerateArray()).GetProperty("Id").GetString());
        Assert.Equal("recording-id", item.GetProperty("ProviderIds").GetProperty("MusicBrainzTrack").GetString());
        if (fields.Contains("People", StringComparison.Ordinal))
            Assert.Contains(item.GetProperty("People").EnumerateArray(), person => person.GetProperty("Name").GetString() == "Writer" && person.GetProperty("Type").GetString() == "Composer");
    }

    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    public void FeishinAlbum_ProjectsLabelsReleaseAndIdentifiers(string format)
    {
        var album = JsonSerializer.Deserialize<Album>("""{"Id":"ext-deezer-album-7","Title":"Album","Artist":"Host","ArtistId":"ext-deezer-artist-2","Label":"Fixture label","ReleaseDate":"2024-02","ProviderIds":{"MusicBrainzAlbum":"release-id"}}""")!;
        var builder = new SubsonicResponseBuilder();
        if (format == "json")
        {
            var item = JsonSerializer.SerializeToElement(builder.ConvertAlbumToJson(album));
            Assert.Equal("Fixture label", Assert.Single(item.GetProperty("recordLabels").EnumerateArray()).GetProperty("name").GetString());
            Assert.Equal("release-id", item.GetProperty("musicBrainzId").GetString());
            Assert.Equal(2, item.GetProperty("releaseDate").GetProperty("month").GetInt32());
            Assert.False(item.GetProperty("releaseDate").TryGetProperty("day", out _));
        }
        else
        {
            var item = builder.ConvertAlbumToXml(album, XNamespace.None);
            Assert.Equal("Fixture label", item.Element("recordLabels")?.Attribute("name")?.Value);
            Assert.Equal("release-id", item.Attribute("musicBrainzId")?.Value);
            Assert.Equal("2", item.Element("releaseDate")?.Attribute("month")?.Value);
            Assert.Null(item.Element("releaseDate")?.Attribute("day"));
        }
    }

    [Fact]
    public void JellyfinAlbumAndArtist_ExposeSourceDescriptionsStudiosAndIds()
    {
        var album = JsonSerializer.Deserialize<Album>("""{"Id":"ext-deezer-album-7","ExternalProvider":"deezer","Label":"Fixture label","Notes":"Album notes","ProviderIds":{"MusicBrainzAlbum":"release-id"}}""")!;
        var artist = JsonSerializer.Deserialize<Artist>("""{"Id":"ext-deezer-artist-2","ExternalProvider":"deezer","Biography":"Artist biography","ProviderIds":{"MusicBrainzArtist":"artist-id"}}""")!;
        var http = new DefaultHttpContext();
        http.Request.Path = "/Items";
        http.Request.QueryString = new("?Fields=Studios,Overview,ProviderIds");
        var builder = new JellyfinResponseBuilder(httpContexts: new HttpContextAccessor { HttpContext = http });
        var albumItem = JsonSerializer.SerializeToElement(builder.ConvertAlbumToJellyfinItem(album));
        Assert.Equal("Album notes", albumItem.GetProperty("Overview").GetString());
        var studio = Assert.Single(albumItem.GetProperty("Studios").EnumerateArray());
        Assert.Equal("Fixture label", studio.GetProperty("Name").GetString());
        Assert.False(studio.TryGetProperty("Id", out _));
        Assert.Equal("release-id", albumItem.GetProperty("ProviderIds").GetProperty("MusicBrainzAlbum").GetString());
        var artistItem = JsonSerializer.SerializeToElement(builder.ConvertArtistToJellyfinItem(artist));
        Assert.Equal("Artist biography", artistItem.GetProperty("Overview").GetString());
        Assert.Equal("artist-id", artistItem.GetProperty("ProviderIds").GetProperty("MusicBrainzArtist").GetString());
    }

    [Fact]
    public void UnknownAlbumArtist_DoesNotBorrowTheGuestIdentity()
    {
        var song = new Song { Id = "ext-deezer-song-42", Artist = "Guest", ArtistId = "ext-deezer-artist-3", AlbumArtist = "Host" };
        var item = JsonSerializer.SerializeToElement(new JellyfinResponseBuilder().ConvertSongToJellyfinItem(song));
        Assert.Empty(item.GetProperty("AlbumArtists").EnumerateArray());
    }

    private static Song GuestSong() => JsonSerializer.Deserialize<Song>("""
        {"Id":"ext-deezer-song-42","ExternalProvider":"deezer","ExternalId":"42","Title":"Guest song",
         "Artist":"Guest","ArtistId":"ext-deezer-artist-3","AlbumArtist":"Host",
         "AlbumArtists":[{"Id":"ext-deezer-artist-2","Name":"Host"}],"Label":"Fixture label","Composer":"Writer",
         "Contributors":["Guest"],"ProviderIds":{"MusicBrainzTrack":"recording-id"},"ReleaseDate":"2024-02-29"}
        """)!;
}

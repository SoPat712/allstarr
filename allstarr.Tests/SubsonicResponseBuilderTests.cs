using Microsoft.AspNetCore.Mvc;
using allstarr.Models.Domain;
using allstarr.Services.Subsonic;
using System.Text.Json;
using System.Xml.Linq;

namespace allstarr.Tests;

public class SubsonicResponseBuilderTests
{
    private readonly SubsonicResponseBuilder _builder;

    public SubsonicResponseBuilderTests()
    {
        _builder = new SubsonicResponseBuilder();
    }

    [Fact]
    public void CreateResponse_JsonFormat_ReturnsJsonWithOkStatus()
    {
        // Act
        var result = _builder.CreateResponse("json", "testElement", new { });

        // Assert
        var jsonResult = Assert.IsType<JsonResult>(result);
        Assert.NotNull(jsonResult.Value);
        
        // Serialize and deserialize to check structure
        var json = JsonSerializer.Serialize(jsonResult.Value);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("ok", doc.RootElement.GetProperty("subsonic-response").GetProperty("status").GetString());
        Assert.Equal("1.16.1", doc.RootElement.GetProperty("subsonic-response").GetProperty("version").GetString());
    }

    [Fact]
    public void CreateResponse_XmlFormat_ReturnsXmlWithOkStatus()
    {
        // Act
        var result = _builder.CreateResponse("xml", "testElement", new { });

        // Assert
        var contentResult = Assert.IsType<ContentResult>(result);
        Assert.Equal("application/xml", contentResult.ContentType);
        
        var doc = XDocument.Parse(contentResult.Content!);
        var root = doc.Root!;
        Assert.Equal("subsonic-response", root.Name.LocalName);
        Assert.Equal("ok", root.Attribute("status")?.Value);
        Assert.Equal("1.16.1", root.Attribute("version")?.Value);
    }

    [Fact]
    public void CreateError_JsonFormat_ReturnsJsonWithError()
    {
        // Act
        var result = _builder.CreateError("json", 70, "Test error message");

        // Assert
        var jsonResult = Assert.IsType<JsonResult>(result);
        var json = JsonSerializer.Serialize(jsonResult.Value);
        var doc = JsonDocument.Parse(json);
        var response = doc.RootElement.GetProperty("subsonic-response");
        
        Assert.Equal("failed", response.GetProperty("status").GetString());
        Assert.Equal(70, response.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal("Test error message", response.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public void CreateError_XmlFormat_ReturnsXmlWithError()
    {
        // Act
        var result = _builder.CreateError("xml", 70, "Test error message");

        // Assert
        var contentResult = Assert.IsType<ContentResult>(result);
        Assert.Equal("application/xml", contentResult.ContentType);
        
        var doc = XDocument.Parse(contentResult.Content!);
        var root = doc.Root!;
        Assert.Equal("failed", root.Attribute("status")?.Value);
        
        var ns = root.GetDefaultNamespace();
        var errorElement = root.Element(ns + "error");
        Assert.NotNull(errorElement);
        Assert.Equal("70", errorElement.Attribute("code")?.Value);
        Assert.Equal("Test error message", errorElement.Attribute("message")?.Value);
    }

    [Fact]
    public void CreateSongResponse_JsonFormat_ReturnsSongData()
    {
        // Arrange
        var song = new Song
        {
            Id = "song123",
            Title = "Test Song",
            Artist = "Test Artist",
            Album = "Test Album",
            Duration = 180,
            Track = 5,
            Year = 2023,
            Genre = "Rock",
            LocalPath = "/music/test.mp3"
        };

        // Act
        var result = _builder.CreateSongResponse("json", song);

        // Assert
        var jsonResult = Assert.IsType<JsonResult>(result);
        var json = JsonSerializer.Serialize(jsonResult.Value);
        var doc = JsonDocument.Parse(json);
        var songData = doc.RootElement.GetProperty("subsonic-response").GetProperty("song");
        
        Assert.Equal("song123", songData.GetProperty("id").GetString());
        Assert.Equal("Test Song", songData.GetProperty("title").GetString());
        Assert.Equal("Test Artist", songData.GetProperty("artist").GetString());
        Assert.Equal("Test Album", songData.GetProperty("album").GetString());
    }

    [Fact]
    public void CreateSongResponse_XmlFormat_ReturnsSongData()
    {
        // Arrange
        var song = new Song
        {
            Id = "song123",
            Title = "Test Song",
            Artist = "Test Artist",
            Album = "Test Album",
            Duration = 180
        };

        // Act
        var result = _builder.CreateSongResponse("xml", song);

        // Assert
        var contentResult = Assert.IsType<ContentResult>(result);
        Assert.Equal("application/xml", contentResult.ContentType);
        
        var doc = XDocument.Parse(contentResult.Content!);
        var ns = doc.Root!.GetDefaultNamespace();
        var songElement = doc.Root!.Element(ns + "song");
        Assert.NotNull(songElement);
        Assert.Equal("song123", songElement.Attribute("id")?.Value);
        Assert.Equal("Test Song", songElement.Attribute("title")?.Value);
    }

    [Fact]
    public void CreateAlbumResponse_JsonFormat_ReturnsAlbumWithSongs()
    {
        // Arrange
        var album = new Album
        {
            Id = "album123",
            Title = "Test Album",
            Artist = "Test Artist",
            Year = 2023,
            Songs = new List<Song>
            {
                new Song { Id = "song1", Title = "Song 1", Duration = 180 },
                new Song { Id = "song2", Title = "Song 2", Duration = 200 }
            }
        };

        // Act
        var result = _builder.CreateAlbumResponse("json", album);

        // Assert
        var jsonResult = Assert.IsType<JsonResult>(result);
        var json = JsonSerializer.Serialize(jsonResult.Value);
        var doc = JsonDocument.Parse(json);
        var albumData = doc.RootElement.GetProperty("subsonic-response").GetProperty("album");
        
        Assert.Equal("album123", albumData.GetProperty("id").GetString());
        Assert.Equal("Test Album", albumData.GetProperty("name").GetString());
        Assert.Equal(2, albumData.GetProperty("songCount").GetInt32());
        Assert.Equal(380, albumData.GetProperty("duration").GetInt32());
    }

    [Fact]
    public void CreateAlbumResponse_XmlFormat_ReturnsAlbumWithSongs()
    {
        // Arrange
        var album = new Album
        {
            Id = "album123",
            Title = "Test Album",
            Artist = "Test Artist",
            SongCount = 2,
            Songs = new List<Song>
            {
                new Song { Id = "song1", Title = "Song 1" },
                new Song { Id = "song2", Title = "Song 2" }
            }
        };

        // Act
        var result = _builder.CreateAlbumResponse("xml", album);

        // Assert
        var contentResult = Assert.IsType<ContentResult>(result);
        Assert.Equal("application/xml", contentResult.ContentType);
        
        var doc = XDocument.Parse(contentResult.Content!);
        var ns = doc.Root!.GetDefaultNamespace();
        var albumElement = doc.Root!.Element(ns + "album");
        Assert.NotNull(albumElement);
        Assert.Equal("album123", albumElement.Attribute("id")?.Value);
        Assert.Equal("2", albumElement.Attribute("songCount")?.Value);
    }

    [Fact]
    public void CreateArtistResponse_JsonFormat_ReturnsArtistData()
    {
        // Arrange
        var artist = new Artist
        {
            Id = "artist123",
            Name = "Test Artist"
        };
        var albums = new List<Album>
        {
            new Album { Id = "album1", Title = "Album 1" },
            new Album { Id = "album2", Title = "Album 2" }
        };

        // Act
        var result = _builder.CreateArtistResponse("json", artist, albums);

        // Assert
        var jsonResult = Assert.IsType<JsonResult>(result);
        var json = JsonSerializer.Serialize(jsonResult.Value);
        var doc = JsonDocument.Parse(json);
        var artistData = doc.RootElement.GetProperty("subsonic-response").GetProperty("artist");
        
        Assert.Equal("artist123", artistData.GetProperty("id").GetString());
        Assert.Equal("Test Artist", artistData.GetProperty("name").GetString());
        Assert.Equal(2, artistData.GetProperty("albumCount").GetInt32());
    }

    [Fact]
    public void CreateArtistResponse_XmlFormat_ReturnsArtistData()
    {
        // Arrange
        var artist = new Artist
        {
            Id = "artist123",
            Name = "Test Artist"
        };
        var albums = new List<Album>
        {
            new Album { Id = "album1", Title = "Album 1" },
            new Album { Id = "album2", Title = "Album 2" }
        };

        // Act
        var result = _builder.CreateArtistResponse("xml", artist, albums);

        // Assert
        var contentResult = Assert.IsType<ContentResult>(result);
        Assert.Equal("application/xml", contentResult.ContentType);
        
        var doc = XDocument.Parse(contentResult.Content!);
        var ns = doc.Root!.GetDefaultNamespace();
        var artistElement = doc.Root!.Element(ns + "artist");
        Assert.NotNull(artistElement);
        Assert.Equal("artist123", artistElement.Attribute("id")?.Value);
        Assert.Equal("Test Artist", artistElement.Attribute("name")?.Value);
        Assert.Equal("2", artistElement.Attribute("albumCount")?.Value);
    }

    [Fact]
    public void CreateSongResponse_SongWithNullValues_HandlesGracefully()
    {
        // Arrange
        var song = new Song
        {
            Id = "song123",
            Title = "Test Song"
            // Other fields are null
        };

        // Act
        var result = _builder.CreateSongResponse("json", song);

        // Assert
        var jsonResult = Assert.IsType<JsonResult>(result);
        var json = JsonSerializer.Serialize(jsonResult.Value);
        var doc = JsonDocument.Parse(json);
        var songData = doc.RootElement.GetProperty("subsonic-response").GetProperty("song");
        
        Assert.Equal("song123", songData.GetProperty("id").GetString());
        Assert.Equal("Test Song", songData.GetProperty("title").GetString());
    }

    [Fact]
    public void CreateAlbumResponse_EmptySongList_ReturnsZeroCounts()
    {
        // Arrange
        var album = new Album
        {
            Id = "album123",
            Title = "Empty Album",
            Artist = "Test Artist",
            Songs = new List<Song>()
        };

        // Act
        var result = _builder.CreateAlbumResponse("json", album);

        // Assert
        var jsonResult = Assert.IsType<JsonResult>(result);
        var json = JsonSerializer.Serialize(jsonResult.Value);
        var doc = JsonDocument.Parse(json);
        var albumData = doc.RootElement.GetProperty("subsonic-response").GetProperty("album");
        
        Assert.Equal(0, albumData.GetProperty("songCount").GetInt32());
        Assert.Equal(0, albumData.GetProperty("duration").GetInt32());
    }
}

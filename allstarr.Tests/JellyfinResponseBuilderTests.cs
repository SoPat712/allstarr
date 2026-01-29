using Microsoft.AspNetCore.Mvc;
using allstarr.Models.Domain;
using allstarr.Models.Subsonic;
using allstarr.Services.Jellyfin;

namespace allstarr.Tests;

public class JellyfinResponseBuilderTests
{
    private readonly JellyfinResponseBuilder _builder;

    public JellyfinResponseBuilderTests()
    {
        _builder = new JellyfinResponseBuilder();
    }

    [Fact]
    public void ConvertSongToJellyfinItem_SetsCorrectFields()
    {
        // Arrange
        var song = new Song
        {
            Id = "song-123",
            Title = "Test Track",
            Artist = "Test Artist",
            Album = "Test Album",
            AlbumId = "album-456",
            ArtistId = "artist-789",
            Duration = 245,
            Track = 3,
            DiscNumber = 1,
            Year = 2023,
            Genre = "Rock",
            IsLocal = true
        };

        // Act
        var result = _builder.ConvertSongToJellyfinItem(song);

        // Assert
        Assert.Equal("song-123", result["Id"]);
        Assert.Equal("Test Track", result["Name"]);
        Assert.Equal("Audio", result["Type"]);
        Assert.Equal("Test Album", result["Album"]);
        Assert.Equal("album-456", result["AlbumId"]);
        Assert.Equal(3, result["IndexNumber"]);
        Assert.Equal(1, result["ParentIndexNumber"]);
        Assert.Equal(2023, result["ProductionYear"]);
        Assert.Equal(245 * TimeSpan.TicksPerSecond, result["RunTimeTicks"]);
    }

    [Fact]
    public void ConvertSongToJellyfinItem_ExternalSong_IncludesProviderIds()
    {
        // Arrange
        var song = new Song
        {
            Id = "ext-deezer-song-12345",
            Title = "External Track",
            Artist = "External Artist",
            IsLocal = false,
            ExternalProvider = "deezer",
            ExternalId = "12345",
            Isrc = "USRC12345678"
        };

        // Act
        var result = _builder.ConvertSongToJellyfinItem(song);

        // Assert
        Assert.True(result.ContainsKey("ProviderIds"));
        var providerIds = result["ProviderIds"] as Dictionary<string, string>;
        Assert.NotNull(providerIds);
        Assert.Equal("12345", providerIds["deezer"]);
        Assert.Equal("USRC12345678", providerIds["ISRC"]);
    }

    [Fact]
    public void ConvertAlbumToJellyfinItem_SetsCorrectFields()
    {
        // Arrange
        var album = new Album
        {
            Id = "album-456",
            Title = "Greatest Hits",
            Artist = "Famous Band",
            ArtistId = "artist-123",
            Year = 2020,
            SongCount = 12,
            Genre = "Pop",
            IsLocal = true
        };

        // Act
        var result = _builder.ConvertAlbumToJellyfinItem(album);

        // Assert
        Assert.Equal("album-456", result["Id"]);
        Assert.Equal("Greatest Hits", result["Name"]);
        Assert.Equal("MusicAlbum", result["Type"]);
        Assert.Equal(true, result["IsFolder"]);
        Assert.Equal("Famous Band", result["AlbumArtist"]);
        Assert.Equal(2020, result["ProductionYear"]);
        Assert.Equal(12, result["ChildCount"]);
    }

    [Fact]
    public void ConvertArtistToJellyfinItem_SetsCorrectFields()
    {
        // Arrange
        var artist = new Artist
        {
            Id = "artist-789",
            Name = "The Rockers",
            AlbumCount = 5,
            IsLocal = true
        };

        // Act
        var result = _builder.ConvertArtistToJellyfinItem(artist);

        // Assert
        Assert.Equal("artist-789", result["Id"]);
        Assert.Equal("The Rockers", result["Name"]);
        Assert.Equal("MusicArtist", result["Type"]);
        Assert.Equal(true, result["IsFolder"]);
        Assert.Equal(5, result["AlbumCount"]);
    }

    [Fact]
    public void ConvertPlaylistToAlbumItem_SetsPlaylistType()
    {
        // Arrange
        var playlist = new ExternalPlaylist
        {
            Id = "ext-playlist-deezer-999",
            ExternalId = "999",
            Name = "Summer Vibes",
            Provider = "deezer",
            CuratorName = "DJ Cool",
            TrackCount = 50,
            Duration = 3600,
            CreatedDate = new DateTime(2023, 6, 15)
        };

        // Act
        var result = _builder.ConvertPlaylistToAlbumItem(playlist);

        // Assert
        Assert.Equal("ext-playlist-deezer-999", result["Id"]);
        Assert.Equal("Summer Vibes", result["Name"]);
        Assert.Equal("Playlist", result["Type"]);
        Assert.Equal("DJ Cool", result["AlbumArtist"]);
        Assert.Equal(50, result["ChildCount"]);
        Assert.Equal(2023, result["ProductionYear"]);
    }

    [Fact]
    public void ConvertPlaylistToAlbumItem_NoCurator_UsesProvider()
    {
        // Arrange
        var playlist = new ExternalPlaylist
        {
            Id = "ext-playlist-deezer-888",
            ExternalId = "888",
            Name = "Top Hits",
            Provider = "deezer",
            CuratorName = null,
            TrackCount = 30
        };

        // Act
        var result = _builder.ConvertPlaylistToAlbumItem(playlist);

        // Assert
        Assert.Equal("deezer", result["AlbumArtist"]);
    }

    [Fact]
    public void CreateItemsResponse_ReturnsPaginatedResult()
    {
        // Arrange
        var songs = new List<Song>
        {
            new() { Id = "1", Title = "Song One", Artist = "Artist", Duration = 200 },
            new() { Id = "2", Title = "Song Two", Artist = "Artist", Duration = 180 }
        };

        // Act
        var result = _builder.CreateItemsResponse(songs);

        // Assert
        var jsonResult = Assert.IsType<JsonResult>(result);
        Assert.NotNull(jsonResult.Value);
    }

    [Fact]
    public void CreateSearchHintsResponse_IncludesAllTypes()
    {
        // Arrange
        var songs = new List<Song> { new() { Id = "s1", Title = "Track", Artist = "A" } };
        var albums = new List<Album> { new() { Id = "a1", Title = "Album", Artist = "A" } };
        var artists = new List<Artist> { new() { Id = "ar1", Name = "Artist" } };

        // Act
        var result = _builder.CreateSearchHintsResponse(songs, albums, artists);

        // Assert
        var jsonResult = Assert.IsType<JsonResult>(result);
        Assert.NotNull(jsonResult.Value);
    }

    [Fact]
    public void CreateError_Returns404ForNotFound()
    {
        // Act
        var result = _builder.CreateError(404, "Item not found");

        // Assert
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(404, objectResult.StatusCode);
    }

    [Fact]
    public void CreateAlbumResponse_IncludesChildrenForSongs()
    {
        // Arrange
        var album = new Album
        {
            Id = "album-1",
            Title = "Full Album",
            Artist = "Artist",
            Songs = new List<Song>
            {
                new() { Id = "t1", Title = "Track 1", Artist = "Artist", Track = 1 },
                new() { Id = "t2", Title = "Track 2", Artist = "Artist", Track = 2 }
            }
        };

        // Act
        var result = _builder.CreateAlbumResponse(album);

        // Assert
        var jsonResult = Assert.IsType<JsonResult>(result);
        Assert.NotNull(jsonResult.Value);
    }

    [Fact]
    public void CreateArtistResponse_IncludesAlbumsList()
    {
        // Arrange
        var artist = new Artist { Id = "art-1", Name = "Test Artist" };
        var albums = new List<Album>
        {
            new() { Id = "alb-1", Title = "First Album", Artist = "Test Artist" },
            new() { Id = "alb-2", Title = "Second Album", Artist = "Test Artist" }
        };

        // Act
        var result = _builder.CreateArtistResponse(artist, albums);

        // Assert
        var jsonResult = Assert.IsType<JsonResult>(result);
        Assert.NotNull(jsonResult.Value);
    }

    [Fact]
    public void CreatePlaylistAsAlbumResponse_CalculatesTotalDuration()
    {
        // Arrange
        var playlist = new ExternalPlaylist
        {
            Id = "pl-1",
            Name = "My Playlist",
            Provider = "deezer",
            ExternalId = "123"
        };
        var tracks = new List<Song>
        {
            new() { Id = "t1", Title = "Song 1", Duration = 180 },
            new() { Id = "t2", Title = "Song 2", Duration = 240 },
            new() { Id = "t3", Title = "Song 3", Duration = 200 }
        };

        // Act
        var result = _builder.CreatePlaylistAsAlbumResponse(playlist, tracks);

        // Assert
        var jsonResult = Assert.IsType<JsonResult>(result);
        Assert.NotNull(jsonResult.Value);
    }
}

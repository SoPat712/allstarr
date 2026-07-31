using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using allstarr.Models.Domain;
using allstarr.Models.Subsonic;
using allstarr.Models.Settings;
using allstarr.Services.Jellyfin;
using Microsoft.Extensions.Options;

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
        Assert.NotNull(result["AudioInfo"]);
        Assert.Equal(false, result["CanDelete"]);
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
        Assert.True(Assert.IsType<bool>(result["HasLyrics"]));
    }

    [Fact]
    public void ConvertSongToJellyfinItem_UsesTheAdvertisedProxyServerIdentity()
    {
        var builder = new JellyfinResponseBuilder(Options.Create(new JellyfinSettings
        {
            DeviceId = "proxy-server-id"
        }));

        var result = builder.ConvertSongToJellyfinItem(new Song
        {
            Id = "ext-applemusic-song-42",
            Title = "Artwork Track",
            Artist = "Artwork Artist",
            IsLocal = false,
            ExternalProvider = "applemusic",
            ExternalId = "42"
        });

        Assert.Equal("proxy-server-id", result["ServerId"]);
        var imageTags = Assert.IsType<Dictionary<string, string>>(result["ImageTags"]);
        Assert.Equal("ext-applemusic-song-42-art-v2", imageTags["Primary"]);
    }

    [Fact]
    public void ConvertSongToJellyfinItem_ExternalRelationshipsRemainRoutableForAlbumArtwork()
    {
        var song = new Song
        {
            Id = "ext-apple-musickit-song-i.song",
            Title = "Library Song",
            Artist = "Library Artist",
            Artists = ["Library Artist"],
            ArtistId = "ext-apple-musickit-artist-i.artist",
            ArtistIds = ["ext-apple-musickit-artist-i.artist"],
            Album = "Library Album",
            AlbumId = "ext-apple-musickit-album-i.album",
            IsLocal = false,
            ExternalProvider = "apple-musickit",
            ExternalId = "i.song"
        };

        var result = _builder.ConvertSongToJellyfinItem(song);

        Assert.Equal("ext-apple-musickit-song-i.song", result["Id"]);
        Assert.Equal("ext-apple-musickit-album-i.album", result["AlbumId"]);
        Assert.Equal("ext-apple-musickit-album-i.album", result["AlbumPrimaryImageTag"]);
        Assert.Equal("ext-apple-musickit-album-i.album", result["ParentLogoImageTag"]);
        var imageTags = Assert.IsType<Dictionary<string, string>>(result["ImageTags"]);
        Assert.Equal("ext-apple-musickit-song-i.song-art-v2", imageTags["Primary"]);
    }

    [Fact]
    public void ConvertSongToJellyfinItem_ExternalExplicitSong_AppendsStreamingAndExplicitLabels()
    {
        var song = new Song
        {
            Id = "ext-squidwtf-song-12345",
            Title = "Sunflower",
            Artist = "Artist",
            IsLocal = false,
            ExternalProvider = "squidwtf",
            ExternalId = "12345",
            ExplicitContentLyrics = 1
        };

        var result = _builder.ConvertSongToJellyfinItem(song);

        Assert.Equal("Sunflower [S] [E]", result["Name"]);
    }

    [Fact]
    public void ConvertSongToJellyfinItem_ExternalCleanSong_AppendsOnlyStreamingLabel()
    {
        var song = new Song
        {
            Id = "ext-squidwtf-song-12345",
            Title = "Sunflower",
            Artist = "Artist",
            IsLocal = false,
            ExternalProvider = "squidwtf",
            ExternalId = "12345",
            ExplicitContentLyrics = 0
        };

        var result = _builder.ConvertSongToJellyfinItem(song);

        Assert.Equal("Sunflower [S]", result["Name"]);
    }

    [Theory]
    [InlineData("deezer", "[D]")]
    [InlineData("qobuz", "[Q]")]
    [InlineData("apple-download", "[AM]")]
    [InlineData("applemusic", "[AM]")]
    [InlineData("apple-music", "[AM]")]
    [InlineData("apple-musickit", "[AM]")]
    [InlineData("spotify", "[SP]")]
    [InlineData("tidal", "[T]")]
    [InlineData("squidwtf", "[S]")]
    public void ConvertSongToJellyfinItem_ExternalSong_UsesProviderSourceLabel(string provider, string label)
    {
        var song = new Song
        {
            Id = $"ext-{provider}-song-12345",
            Title = "External Track",
            Artist = "External Artist",
            Artists = new List<string> { "External Artist" },
            Album = "External Album",
            IsLocal = false,
            ExternalProvider = provider,
            ExternalId = "12345"
        };

        var result = _builder.ConvertSongToJellyfinItem(song);

        Assert.Equal($"External Track {label}", result["Name"]);
        Assert.Equal($"External Album {label}", result["Album"]);
        var artists = Assert.IsType<string[]>(result["Artists"]);
        Assert.Equal(new[] { $"External Artist {label}" }, artists);
    }

    [Fact]
    public void ConvertSongToJellyfinItem_DeezerPlaylistMatch_KeepsDisplayArtistWithoutInventingId()
    {
        var matchedSong = new Song
        {
            Id = "ext-deezer-song-12345",
            Title = "Matched Track",
            Artist = "Matched Artist",
            Album = "Matched Album",
            IsLocal = false,
            ExternalProvider = "deezer",
            ExternalId = "12345"
        };

        var result = _builder.ConvertSongToJellyfinItem(matchedSong);

        Assert.Equal("Matched Track [D]", result["Name"]);
        Assert.Equal("Matched Album [D]", result["Album"]);
        var artists = Assert.IsType<string[]>(result["Artists"]);
        Assert.Equal(["Matched Artist [D]"], artists);
        var artistItems = Assert.IsType<Dictionary<string, object?>[]>(result["ArtistItems"]);
        Assert.Empty(artistItems);
        Assert.Empty(Assert.IsType<Dictionary<string, object?>[]>(result["AlbumArtists"]));
    }

    [Fact]
    public void SynthesizedRelationships_OmitMismatchedOrMissingIds()
    {
        var song = _builder.ConvertSongToJellyfinItem(new Song
        {
            Id = "ext-deezer-song-1",
            Title = "Track",
            Artist = "Primary",
            Artists = ["Primary", "Feature"],
            ArtistIds = ["ext-deezer-artist-1"],
            IsLocal = false,
            ExternalProvider = "deezer"
        });
        var album = _builder.ConvertAlbumToJellyfinItem(new Album
        {
            Id = "ext-deezer-album-1",
            Title = "Album",
            Artist = "Primary",
            IsLocal = false,
            ExternalProvider = "deezer"
        });

        Assert.Equal(["Primary [D]", "Feature [D]"], Assert.IsType<string[]>(song["Artists"]));
        Assert.Empty(Assert.IsType<Dictionary<string, object?>[]>(song["ArtistItems"]));
        Assert.Empty(Assert.IsType<Dictionary<string, object?>[]>(song["AlbumArtists"]));
        Assert.Equal(["Primary"], Assert.IsType<string[]>(album["Artists"]));
        Assert.Empty(Assert.IsType<Dictionary<string, object?>[]>(album["ArtistItems"]));
        Assert.Empty(Assert.IsType<Dictionary<string, object?>[]>(album["AlbumArtists"]));
    }

    [Theory]
    [InlineData("deezer")]
    [InlineData("qobuz")]
    [InlineData("squidwtf")]
    [InlineData("Deezer")]
    [InlineData("Qobuz")]
    [InlineData("SquidWTF")]
    public void ConvertSongToJellyfinItem_ExternalStreamingProviders_DisableTranscoding(string provider)
    {
        // Arrange
        var song = new Song
        {
            Id = $"ext-{provider}-song-123",
            Title = "External Track",
            Artist = "External Artist",
            IsLocal = false,
            ExternalProvider = provider,
            ExternalId = "123"
        };

        // Act
        var result = _builder.ConvertSongToJellyfinItem(song);

        // Assert
        var mediaSources = Assert.IsAssignableFrom<object[]>(result["MediaSources"]);
        var mediaSource = Assert.IsType<Dictionary<string, object?>>(mediaSources[0]);
        Assert.False(Assert.IsType<bool>(mediaSource["SupportsTranscoding"]));
    }

    [Fact]
    public void ConvertSongToJellyfinItem_OtherExternalProviders_KeepTranscodingEnabled()
    {
        // Arrange
        var song = new Song
        {
            Id = "ext-spotify-song-123",
            Title = "External Track",
            Artist = "External Artist",
            IsLocal = false,
            ExternalProvider = "spotify",
            ExternalId = "123"
        };

        // Act
        var result = _builder.ConvertSongToJellyfinItem(song);

        // Assert
        var mediaSources = Assert.IsAssignableFrom<object[]>(result["MediaSources"]);
        var mediaSource = Assert.IsType<Dictionary<string, object?>>(mediaSources[0]);
        Assert.True(Assert.IsType<bool>(mediaSource["SupportsTranscoding"]));
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
        Assert.Equal("Greatest Hits", result["SortName"]);
        Assert.NotNull(result["DateCreated"]);
        Assert.NotNull(result["BasicSyncInfo"]);
    }

    [Theory]
    [InlineData("deezer", "[D]")]
    [InlineData("qobuz", "[Q]")]
    [InlineData("squidwtf", "[S]")]
    public void ConvertAlbumToJellyfinItem_ExternalAlbum_UsesProviderSourceLabel(string provider, string label)
    {
        var album = new Album
        {
            Id = $"ext-{provider}-album-456",
            Title = "External Album",
            Artist = "External Artist",
            IsLocal = false,
            ExternalProvider = provider,
            ExternalId = "456"
        };

        var result = _builder.ConvertAlbumToJellyfinItem(album);

        Assert.Equal($"External Album {label}", result["Name"]);
        Assert.Equal($"External Album {label}", result["SortName"]);
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
        Assert.Equal("The Rockers", result["SortName"]);
        Assert.Equal(1.0, result["PrimaryImageAspectRatio"]);
        Assert.NotNull(result["BasicSyncInfo"]);
    }

    [Theory]
    [InlineData("deezer", "[D]")]
    [InlineData("qobuz", "[Q]")]
    [InlineData("squidwtf", "[S]")]
    public void ConvertArtistToJellyfinItem_ExternalArtist_UsesProviderSourceLabel(string provider, string label)
    {
        var artist = new Artist
        {
            Id = $"ext-{provider}-artist-789",
            Name = "External Artist",
            IsLocal = false,
            ExternalProvider = provider,
            ExternalId = "789"
        };

        var result = _builder.ConvertArtistToJellyfinItem(artist);

        Assert.Equal($"External Artist {label}", result["Name"]);
        Assert.Equal($"External Artist {label}", result["SortName"]);
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
        Assert.Equal("Summer Vibes [D/P]", result["Name"]);
        Assert.Equal("MusicAlbum", result["Type"]);
        Assert.Equal("DJ Cool", result["AlbumArtist"]);
        Assert.Equal(50, result["ChildCount"]);
        Assert.Equal(2023, result["ProductionYear"]);
        Assert.Equal("Summer Vibes [D/P]", result["SortName"]);
        Assert.NotNull(result["DateCreated"]);
        Assert.NotNull(result["BasicSyncInfo"]);
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
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(jsonResult.Value));
        Assert.All(document.RootElement.GetProperty("SearchHints").EnumerateArray(), hint =>
        {
            Assert.False(string.IsNullOrWhiteSpace(hint.GetProperty("Id").GetString()));
            Assert.Equal(hint.GetProperty("Id").GetString(), hint.GetProperty("ItemId").GetString());
        });
    }

    [Fact]
    public void CreateSearchHintsResponse_LabelsExternalResults()
    {
        var result = _builder.CreateSearchHintsResponse(
            [new Song
            {
                Id = "ext-apple-download-song-1",
                Title = "Track",
                Artist = "Artist",
                Artists = ["Artist"],
                Album = "Album",
                ExternalProvider = "apple-download",
                ExplicitContentLyrics = 1
            }],
            [new Album
            {
                Id = "ext-deezer-album-1",
                Title = "Album",
                Artist = "Artist",
                ExternalProvider = "deezer"
            }],
            [new Artist
            {
                Id = "ext-qobuz-artist-1",
                Name = "Artist",
                ExternalProvider = "qobuz"
            }]);

        var jsonResult = Assert.IsType<JsonResult>(result);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(jsonResult.Value));
        var hints = document.RootElement.GetProperty("SearchHints").EnumerateArray().ToArray();
        Assert.Equal("Artist [Q]", hints[0].GetProperty("Name").GetString());
        Assert.Equal("Album [D]", hints[1].GetProperty("Name").GetString());
        Assert.Equal("Track [AM] [E]", hints[2].GetProperty("Name").GetString());
        Assert.Equal("Album [AM]", hints[2].GetProperty("Album").GetString());
        Assert.Equal("Artist [AM]", hints[2].GetProperty("Artists")[0].GetString());
    }

    [Fact]
    public void CreateSearchHintsResponse_PreservesNativeFieldsAndNormalizesBothIds()
    {
        using var native = JsonDocument.Parse(
            """
            {
              "SearchHints": [
                {
                  "ItemId": "native-1",
                  "Name": "Native",
                  "Type": "Audio",
                  "AlbumId": "album-1",
                  "PrimaryImageTag": "image-1",
                  "IndexNumber": 7
                }
              ]
            }
            """);

        var result = _builder.CreateSearchHintsResponse([], [], [], native);

        var jsonResult = Assert.IsType<JsonResult>(result);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(jsonResult.Value));
        var hint = Assert.Single(document.RootElement.GetProperty("SearchHints").EnumerateArray());
        Assert.Equal("native-1", hint.GetProperty("Id").GetString());
        Assert.Equal("native-1", hint.GetProperty("ItemId").GetString());
        Assert.Equal("album-1", hint.GetProperty("AlbumId").GetString());
        Assert.Equal("image-1", hint.GetProperty("PrimaryImageTag").GetString());
        Assert.Equal(7, hint.GetProperty("IndexNumber").GetInt32());
    }

    [Fact]
    public void CreateSearchHintsResponse_AppliesLimitAfterMergingNativeAndExternalHints()
    {
        using var native = JsonDocument.Parse(
            """{"SearchHints":[{"Id":"native-1","Name":"Native","Type":"Audio"}]}""");

        var result = _builder.CreateSearchHintsResponse(
            [new Song { Id = "song-1", Title = "Song", Artist = "Artist" }],
            [new Album { Id = "album-1", Title = "Album", Artist = "Artist" }],
            [new Artist { Id = "artist-1", Name = "Artist" }],
            native,
            2);

        var jsonResult = Assert.IsType<JsonResult>(result);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(jsonResult.Value));
        Assert.Equal(2, document.RootElement.GetProperty("SearchHints").GetArrayLength());
        Assert.Equal(2, document.RootElement.GetProperty("TotalRecordCount").GetInt32());
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
            Id = "ext-deezer-playlist-1",
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

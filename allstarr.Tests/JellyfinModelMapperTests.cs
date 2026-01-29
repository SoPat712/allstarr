using Microsoft.Extensions.Logging;
using Moq;
using allstarr.Models.Domain;
using allstarr.Models.Search;
using allstarr.Models.Subsonic;
using allstarr.Services.Jellyfin;
using System.Text.Json;

namespace allstarr.Tests;

public class JellyfinModelMapperTests
{
    private readonly JellyfinModelMapper _mapper;
    private readonly JellyfinResponseBuilder _responseBuilder;

    public JellyfinModelMapperTests()
    {
        _responseBuilder = new JellyfinResponseBuilder();
        var mockLogger = new Mock<ILogger<JellyfinModelMapper>>();
        _mapper = new JellyfinModelMapper(_responseBuilder, mockLogger.Object);
    }

    [Fact]
    public void ParseItemsResponse_AudioItems_ReturnsSongs()
    {
        // Arrange
        var json = @"{
            ""Items"": [
                {
                    ""Id"": ""song-abc"",
                    ""Name"": ""Test Song"",
                    ""Type"": ""Audio"",
                    ""Album"": ""Test Album"",
                    ""AlbumId"": ""album-123"",
                    ""RunTimeTicks"": 2450000000,
                    ""IndexNumber"": 5,
                    ""ParentIndexNumber"": 1,
                    ""ProductionYear"": 2022,
                    ""Artists"": [""Test Artist""],
                    ""Genres"": [""Rock""]
                }
            ],
            ""TotalRecordCount"": 1
        }";
        var doc = JsonDocument.Parse(json);

        // Act
        var (songs, albums, artists) = _mapper.ParseItemsResponse(doc);

        // Assert
        Assert.Single(songs);
        Assert.Empty(albums);
        Assert.Empty(artists);

        var song = songs[0];
        Assert.Equal("song-abc", song.Id);
        Assert.Equal("Test Song", song.Title);
        Assert.Equal("Test Album", song.Album);
        Assert.Equal("Test Artist", song.Artist);
        Assert.Equal(245, song.Duration); // 2450000000 ticks = 245 seconds
        Assert.Equal(5, song.Track);
        Assert.Equal(1, song.DiscNumber);
        Assert.Equal(2022, song.Year);
        Assert.Equal("Rock", song.Genre);
    }

    [Fact]
    public void ParseItemsResponse_AlbumItems_ReturnsAlbums()
    {
        // Arrange
        var json = @"{
            ""Items"": [
                {
                    ""Id"": ""album-xyz"",
                    ""Name"": ""Greatest Hits"",
                    ""Type"": ""MusicAlbum"",
                    ""AlbumArtist"": ""Famous Band"",
                    ""ProductionYear"": 2020,
                    ""ChildCount"": 14,
                    ""Genres"": [""Pop""],
                    ""AlbumArtists"": [{""Id"": ""artist-1"", ""Name"": ""Famous Band""}]
                }
            ]
        }";
        var doc = JsonDocument.Parse(json);

        // Act
        var (songs, albums, artists) = _mapper.ParseItemsResponse(doc);

        // Assert
        Assert.Empty(songs);
        Assert.Single(albums);
        Assert.Empty(artists);

        var album = albums[0];
        Assert.Equal("album-xyz", album.Id);
        Assert.Equal("Greatest Hits", album.Title);
        Assert.Equal("Famous Band", album.Artist);
        Assert.Equal(2020, album.Year);
        Assert.Equal(14, album.SongCount);
        Assert.Equal("Pop", album.Genre);
    }

    [Fact]
    public void ParseItemsResponse_ArtistItems_ReturnsArtists()
    {
        // Arrange
        var json = @"{
            ""Items"": [
                {
                    ""Id"": ""artist-999"",
                    ""Name"": ""The Rockers"",
                    ""Type"": ""MusicArtist"",
                    ""AlbumCount"": 7
                }
            ]
        }";
        var doc = JsonDocument.Parse(json);

        // Act
        var (songs, albums, artists) = _mapper.ParseItemsResponse(doc);

        // Assert
        Assert.Empty(songs);
        Assert.Empty(albums);
        Assert.Single(artists);

        var artist = artists[0];
        Assert.Equal("artist-999", artist.Id);
        Assert.Equal("The Rockers", artist.Name);
        Assert.Equal(7, artist.AlbumCount);
    }

    [Fact]
    public void ParseItemsResponse_MixedTypes_SortsCorrectly()
    {
        // Arrange
        var json = @"{
            ""Items"": [
                {""Id"": ""1"", ""Name"": ""Song"", ""Type"": ""Audio""},
                {""Id"": ""2"", ""Name"": ""Album"", ""Type"": ""MusicAlbum""},
                {""Id"": ""3"", ""Name"": ""Artist"", ""Type"": ""MusicArtist""},
                {""Id"": ""4"", ""Name"": ""Another Song"", ""Type"": ""Audio""}
            ]
        }";
        var doc = JsonDocument.Parse(json);

        // Act
        var (songs, albums, artists) = _mapper.ParseItemsResponse(doc);

        // Assert
        Assert.Equal(2, songs.Count);
        Assert.Single(albums);
        Assert.Single(artists);
    }

    [Fact]
    public void ParseItemsResponse_NullResponse_ReturnsEmptyLists()
    {
        // Act
        var (songs, albums, artists) = _mapper.ParseItemsResponse(null);

        // Assert
        Assert.Empty(songs);
        Assert.Empty(albums);
        Assert.Empty(artists);
    }

    [Fact]
    public void ParseItemsResponse_EmptyItems_ReturnsEmptyLists()
    {
        // Arrange
        var json = @"{""Items"": [], ""TotalRecordCount"": 0}";
        var doc = JsonDocument.Parse(json);

        // Act
        var (songs, albums, artists) = _mapper.ParseItemsResponse(doc);

        // Assert
        Assert.Empty(songs);
        Assert.Empty(albums);
        Assert.Empty(artists);
    }

    [Fact]
    public void ParseSong_ExtractsArtistFromAlbumArtist_WhenNoArtistsArray()
    {
        // Arrange
        var json = @"{
            ""Id"": ""s1"",
            ""Name"": ""Track"",
            ""AlbumArtist"": ""Fallback Artist""
        }";
        var element = JsonDocument.Parse(json).RootElement;

        // Act
        var song = _mapper.ParseSong(element);

        // Assert
        Assert.Equal("Fallback Artist", song.Artist);
    }

    [Fact]
    public void ParseSong_ExtractsArtistId_FromArtistItems()
    {
        // Arrange
        var json = @"{
            ""Id"": ""s1"",
            ""Name"": ""Track"",
            ""Artists"": [""Main Artist""],
            ""ArtistItems"": [{""Id"": ""art-id-123"", ""Name"": ""Main Artist""}]
        }";
        var element = JsonDocument.Parse(json).RootElement;

        // Act
        var song = _mapper.ParseSong(element);

        // Assert
        Assert.Equal("art-id-123", song.ArtistId);
        Assert.Equal("Main Artist", song.Artist);
    }

    [Fact]
    public void ParseAlbum_ExtractsArtistId_FromAlbumArtists()
    {
        // Arrange
        var json = @"{
            ""Id"": ""alb-1"",
            ""Name"": ""The Album"",
            ""AlbumArtist"": ""Band Name"",
            ""AlbumArtists"": [{""Id"": ""band-id"", ""Name"": ""Band Name""}]
        }";
        var element = JsonDocument.Parse(json).RootElement;

        // Act
        var album = _mapper.ParseAlbum(element);

        // Assert
        Assert.Equal("band-id", album.ArtistId);
    }

    [Fact]
    public void MergeSearchResults_DeduplicatesArtistsByName()
    {
        // Arrange
        var localArtists = new List<Artist>
        {
            new() { Id = "local-1", Name = "The Beatles", IsLocal = true }
        };

        var externalResult = new SearchResult
        {
            Songs = new List<Song>(),
            Albums = new List<Album>(),
            Artists = new List<Artist>
            {
                new() { Id = "ext-deezer-artist-1", Name = "The Beatles", IsLocal = false },
                new() { Id = "ext-deezer-artist-2", Name = "Pink Floyd", IsLocal = false }
            }
        };

        var playlists = new List<ExternalPlaylist>();

        // Act
        var (songs, albums, artists) = _mapper.MergeSearchResults(
            new List<Song>(), new List<Album>(), localArtists, externalResult, playlists);

        // Assert - Beatles should not be duplicated, Pink Floyd should be added
        Assert.Equal(2, artists.Count);
        Assert.Contains(artists, a => a["Id"]!.ToString() == "local-1");
        Assert.Contains(artists, a => a["Id"]!.ToString() == "ext-deezer-artist-2");
    }

    [Fact]
    public void MergeSearchResults_IncludesPlaylistsAsAlbums()
    {
        // Arrange
        var playlists = new List<ExternalPlaylist>
        {
            new() { Id = "pl-1", Name = "Summer Mix", Provider = "deezer", ExternalId = "123" }
        };

        var externalResult = new SearchResult
        {
            Songs = new List<Song>(),
            Albums = new List<Album>(),
            Artists = new List<Artist>()
        };

        // Act
        var (songs, albums, artists) = _mapper.MergeSearchResults(
            new List<Song>(), new List<Album>(), new List<Artist>(), externalResult, playlists);

        // Assert
        Assert.Single(albums);
        Assert.Equal("pl-1", albums[0]["Id"]);
    }

    [Fact]
    public void ParseAlbumWithTracks_CombinesAlbumAndTracks()
    {
        // Arrange
        var albumJson = @"{
            ""Id"": ""album-1"",
            ""Name"": ""Test Album"",
            ""Type"": ""MusicAlbum"",
            ""AlbumArtist"": ""Test Artist""
        }";
        var tracksJson = @"{
            ""Items"": [
                {""Id"": ""t1"", ""Name"": ""Track 1"", ""Type"": ""Audio""},
                {""Id"": ""t2"", ""Name"": ""Track 2"", ""Type"": ""Audio""}
            ]
        }";

        var albumDoc = JsonDocument.Parse(albumJson);
        var tracksDoc = JsonDocument.Parse(tracksJson);

        // Act
        var album = _mapper.ParseAlbumWithTracks(albumDoc, tracksDoc);

        // Assert
        Assert.NotNull(album);
        Assert.Equal("album-1", album.Id);
        Assert.Equal(2, album.Songs.Count);
    }

    [Fact]
    public void ParseAlbumWithTracks_NullAlbum_ReturnsNull()
    {
        // Act
        var album = _mapper.ParseAlbumWithTracks(null, null);

        // Assert
        Assert.Null(album);
    }

    [Fact]
    public void ParseArtistWithAlbums_SetsAlbumCount()
    {
        // Arrange
        var artistJson = @"{
            ""Id"": ""art-1"",
            ""Name"": ""Test Artist"",
            ""Type"": ""MusicArtist""
        }";
        var albumsJson = @"{
            ""Items"": [
                {""Id"": ""a1"", ""Name"": ""Album 1""},
                {""Id"": ""a2"", ""Name"": ""Album 2""},
                {""Id"": ""a3"", ""Name"": ""Album 3""}
            ]
        }";

        var artistDoc = JsonDocument.Parse(artistJson);
        var albumsDoc = JsonDocument.Parse(albumsJson);

        // Act
        var artist = _mapper.ParseArtistWithAlbums(artistDoc, albumsDoc);

        // Assert
        Assert.NotNull(artist);
        Assert.Equal("art-1", artist.Id);
        Assert.Equal(3, artist.AlbumCount);
    }

    [Fact]
    public void ParseSearchHintsResponse_HandlesSearchHintsFormat()
    {
        // Arrange
        var json = @"{
            ""SearchHints"": [
                {""Id"": ""s1"", ""Name"": ""Song"", ""Type"": ""Audio"", ""Album"": ""Album"", ""AlbumArtist"": ""Artist""},
                {""Id"": ""a1"", ""Name"": ""Album"", ""Type"": ""MusicAlbum"", ""AlbumArtist"": ""Artist""},
                {""Id"": ""ar1"", ""Name"": ""Artist"", ""Type"": ""MusicArtist""}
            ],
            ""TotalRecordCount"": 3
        }";
        var doc = JsonDocument.Parse(json);

        // Act
        var (songs, albums, artists) = _mapper.ParseSearchHintsResponse(doc);

        // Assert
        Assert.Single(songs);
        Assert.Single(albums);
        Assert.Single(artists);
    }

    [Fact]
    public void ParseSearchHintsResponse_NullResponse_ReturnsEmptyLists()
    {
        // Act
        var (songs, albums, artists) = _mapper.ParseSearchHintsResponse(null);

        // Assert
        Assert.Empty(songs);
        Assert.Empty(albums);
        Assert.Empty(artists);
    }
}

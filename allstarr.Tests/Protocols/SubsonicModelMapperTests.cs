using Microsoft.Extensions.Logging;
using Moq;
using allstarr.Models.Domain;
using allstarr.Models.Search;
using allstarr.Models.Subsonic;
using allstarr.Services.Subsonic;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace allstarr.Tests;

public class SubsonicModelMapperTests
{
    private readonly SubsonicModelMapper _mapper;
    private readonly Mock<ILogger<SubsonicModelMapper>> _mockLogger;
    private readonly SubsonicResponseBuilder _responseBuilder;

    public SubsonicModelMapperTests()
    {
        _responseBuilder = new SubsonicResponseBuilder();
        _mockLogger = new Mock<ILogger<SubsonicModelMapper>>();
        _mapper = new SubsonicModelMapper(_responseBuilder, _mockLogger.Object);
    }

    [Fact]
    public void ParseSearchResponse_JsonWithSongs_ParsesCorrectly()
    {
        var jsonResponse = @"{
            ""subsonic-response"": {
                ""status"": ""ok"",
                ""version"": ""1.16.1"",
                ""searchResult3"": {
                    ""song"": [
                        {
                            ""id"": ""song1"",
                            ""title"": ""Test Song"",
                            ""artist"": ""Test Artist"",
                            ""album"": ""Test Album""
                        }
                    ]
                }
            }
        }";
        var responseBody = Encoding.UTF8.GetBytes(jsonResponse);

        var (songs, albums, artists) = _mapper.ParseSearchResponse(responseBody, "application/json");

        Assert.Single(songs);
        Assert.Empty(albums);
        Assert.Empty(artists);
    }

    [Fact]
    public void ParseSearchResponse_XmlWithSongs_ParsesCorrectly()
    {
        var xmlResponse = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<subsonic-response xmlns=""http://subsonic.org/restapi"" status=""ok"" version=""1.16.1"">
    <searchResult3>
        <song id=""song1"" title=""Test Song"" artist=""Test Artist"" album=""Test Album"" />
    </searchResult3>
</subsonic-response>";
        var responseBody = Encoding.UTF8.GetBytes(xmlResponse);

        var (songs, albums, artists) = _mapper.ParseSearchResponse(responseBody, "application/xml");

        Assert.Single(songs);
        Assert.Empty(albums);
        Assert.Empty(artists);
    }

    [Fact]
    public void ParseSearchResponse_JsonWithAllTypes_ParsesAllCorrectly()
    {
        var jsonResponse = @"{
            ""subsonic-response"": {
                ""status"": ""ok"",
                ""version"": ""1.16.1"",
                ""searchResult3"": {
                    ""song"": [
                        {""id"": ""song1"", ""title"": ""Song 1""}
                    ],
                    ""album"": [
                        {""id"": ""album1"", ""name"": ""Album 1""}
                    ],
                    ""artist"": [
                        {""id"": ""artist1"", ""name"": ""Artist 1""}
                    ]
                }
            }
        }";
        var responseBody = Encoding.UTF8.GetBytes(jsonResponse);

        var (songs, albums, artists) = _mapper.ParseSearchResponse(responseBody, "application/json");

        Assert.Single(songs);
        Assert.Single(albums);
        Assert.Single(artists);
    }

    [Fact]
    public void ParseSearchResponse_XmlWithAllTypes_ParsesAllCorrectly()
    {
        var xmlResponse = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<subsonic-response xmlns=""http://subsonic.org/restapi"" status=""ok"" version=""1.16.1"">
    <searchResult3>
        <song id=""song1"" title=""Song 1"" />
        <album id=""album1"" name=""Album 1"" />
        <artist id=""artist1"" name=""Artist 1"" />
    </searchResult3>
</subsonic-response>";
        var responseBody = Encoding.UTF8.GetBytes(xmlResponse);

        var (songs, albums, artists) = _mapper.ParseSearchResponse(responseBody, "application/xml");

        Assert.Single(songs);
        Assert.Single(albums);
        Assert.Single(artists);
    }

    [Fact]
    public void ParseSearchResponse_InvalidJson_ReturnsEmpty()
    {
        var invalidJson = "{invalid json}";
        var responseBody = Encoding.UTF8.GetBytes(invalidJson);

        var (songs, albums, artists) = _mapper.ParseSearchResponse(responseBody, "application/json");

        Assert.Empty(songs);
        Assert.Empty(albums);
        Assert.Empty(artists);
    }

    [Fact]
    public void ParseSearchResponse_EmptySearchResult_ReturnsEmpty()
    {
        var jsonResponse = @"{
            ""subsonic-response"": {
                ""status"": ""ok"",
                ""version"": ""1.16.1"",
                ""searchResult3"": {}
            }
        }";
        var responseBody = Encoding.UTF8.GetBytes(jsonResponse);

        var (songs, albums, artists) = _mapper.ParseSearchResponse(responseBody, "application/json");

        Assert.Empty(songs);
        Assert.Empty(albums);
        Assert.Empty(artists);
    }

    [Fact]
    public void MergeSearchResults_Json_MergesSongsCorrectly()
    {
        var localSongs = new List<object>
        {
            new Dictionary<string, object> { ["id"] = "local1", ["title"] = "Local Song" }
        };
        var externalResult = new SearchResult
        {
            Songs = new List<Song>
            {
                new Song { Id = "ext1", Title = "External Song" }
            },
            Albums = new List<Album>(),
            Artists = new List<Artist>()
        };

        var (mergedSongs, mergedAlbums, mergedArtists) = _mapper.MergeSearchResults(
            localSongs, new List<object>(), new List<object>(), externalResult, new List<ExternalPlaylist>(), true);

        Assert.Equal(2, mergedSongs.Count);
    }

    [Fact]
    public void MergeSearchResults_Json_CaseInsensitiveDeduplication()
    {
        var localArtists = new List<object>
        {
            new Dictionary<string, object> { ["id"] = "local1", ["name"] = "Test Artist" }
        };
        var externalResult = new SearchResult
        {
            Songs = new List<Song>(),
            Albums = new List<Album>(),
            Artists = new List<Artist>
            {
                new Artist { Id = "ext1", Name = "test artist" } // Different case - should still be filtered
            }
        };

        var (mergedSongs, mergedAlbums, mergedArtists) = _mapper.MergeSearchResults(
            new List<object>(), new List<object>(), localArtists, externalResult, new List<ExternalPlaylist>(), true);

        Assert.Single(mergedArtists); // Only the local artist
    }

    [Fact]
    public void MergeSearchResults_Xml_MergesSongsCorrectly()
    {
        var ns = XNamespace.Get("http://subsonic.org/restapi");
        var localSongs = new List<object>
        {
            new XElement("song", new XAttribute("id", "local1"), new XAttribute("title", "Local Song"))
        };
        var externalResult = new SearchResult
        {
            Songs = new List<Song>
            {
                new Song { Id = "ext1", Title = "External Song" }
            },
            Albums = new List<Album>(),
            Artists = new List<Artist>()
        };

        var (mergedSongs, mergedAlbums, mergedArtists) = _mapper.MergeSearchResults(
            localSongs, new List<object>(), new List<object>(), externalResult, new List<ExternalPlaylist>(), false);

        Assert.Equal(2, mergedSongs.Count);
    }

    [Fact]
    public void MergeSearchResults_Xml_DeduplicatesArtists()
    {
        var localArtists = new List<object>
        {
            new XElement("artist", new XAttribute("id", "local1"), new XAttribute("name", "Test Artist"))
        };
        var externalResult = new SearchResult
        {
            Songs = new List<Song>(),
            Albums = new List<Album>(),
            Artists = new List<Artist>
            {
                new Artist { Id = "ext1", Name = "Test Artist" }, // Same name - should be filtered
                new Artist { Id = "ext2", Name = "Different Artist" } // Different name - should be included
            }
        };

        var (mergedSongs, mergedAlbums, mergedArtists) = _mapper.MergeSearchResults(
            new List<object>(), new List<object>(), localArtists, externalResult, new List<ExternalPlaylist>(), false);

        Assert.Equal(2, mergedArtists.Count); // 1 local + 1 external (duplicate filtered)
    }

    [Fact]
    public void MergeSearchResults_EmptyLocalResults_ReturnsOnlyExternal()
    {
        var externalResult = new SearchResult
        {
            Songs = new List<Song> { new Song { Id = "ext1" } },
            Albums = new List<Album> { new Album { Id = "ext2" } },
            Artists = new List<Artist> { new Artist { Id = "ext3", Name = "Artist" } }
        };

        var (mergedSongs, mergedAlbums, mergedArtists) = _mapper.MergeSearchResults(
            new List<object>(), new List<object>(), new List<object>(), externalResult, new List<ExternalPlaylist>(), true);

        Assert.Single(mergedSongs);
        Assert.Single(mergedAlbums);
        Assert.Single(mergedArtists);
    }

    [Fact]
    public void MergeSearchResults_EmptyExternalResults_ReturnsOnlyLocal()
    {
        var localSongs = new List<object> { new Dictionary<string, object> { ["id"] = "local1" } };
        var localAlbums = new List<object> { new Dictionary<string, object> { ["id"] = "local2" } };
        var localArtists = new List<object> { new Dictionary<string, object> { ["id"] = "local3", ["name"] = "Local" } };
        var externalResult = new SearchResult
        {
            Songs = new List<Song>(),
            Albums = new List<Album>(),
            Artists = new List<Artist>()
        };

        var (mergedSongs, mergedAlbums, mergedArtists) = _mapper.MergeSearchResults(
            localSongs, localAlbums, localArtists, externalResult, new List<ExternalPlaylist>(), true);

        Assert.Single(mergedSongs);
        Assert.Single(mergedAlbums);
        Assert.Single(mergedArtists);
    }

    [Fact]
    public void MergeSearchResults_PlaylistOmitsUnknownDurationInBothFormats()
    {
        var playlist = new ExternalPlaylist
        {
            Id = "ext-deezer-playlist-1",
            Name = "Fixture",
            Provider = "deezer",
            ExternalId = "1",
            TrackCount = 3
        };
        var empty = new SearchResult();

        var (_, jsonAlbums, _) = _mapper.MergeSearchResults(
            [], [], [], empty, [playlist], true);
        Assert.False(Assert.IsType<Dictionary<string, object>>(Assert.Single(jsonAlbums))
            .ContainsKey("duration"));

        var (_, xmlAlbums, _) = _mapper.MergeSearchResults(
            [], [], [], empty, [playlist], false);
        Assert.Null(Assert.IsType<XElement>(Assert.Single(xmlAlbums)).Attribute("duration"));
    }
}

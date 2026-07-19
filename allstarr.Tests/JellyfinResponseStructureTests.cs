using System.Text.Json;
using Xunit;
using allstarr.Models.Domain;
using allstarr.Services.Jellyfin;

namespace allstarr.Tests;

/// <summary>
/// Integration tests to verify Jellyfin response structure matches real API responses.
/// </summary>
public class JellyfinResponseStructureTests
{
    private readonly JellyfinResponseBuilder _builder;

    public JellyfinResponseStructureTests()
    {
        _builder = new JellyfinResponseBuilder();
    }

    [Fact]
    public void Track_Response_Should_Have_All_Required_Fields()
    {
        // Arrange
        var song = new Song
        {
            Id = "test-id",
            Title = "Test Song",
            Artist = "Test Artist",
            ArtistId = "artist-id",
            Album = "Test Album",
            AlbumId = "album-id",
            Duration = 180,
            Year = 2024,
            Track = 1,
            Genre = "Pop",
            IsLocal = false,
            ExternalProvider = "Deezer",
            ExternalId = "123456"
        };

        // Act
        var result = _builder.ConvertSongToJellyfinItem(song);

        // Assert - Required top-level fields
        Assert.NotNull(result["Name"]);
        Assert.NotNull(result["ServerId"]);
        Assert.NotNull(result["Id"]);
        Assert.NotNull(result["Type"]);
        Assert.Equal("Audio", result["Type"]);
        Assert.NotNull(result["MediaType"]);
        Assert.Equal("Audio", result["MediaType"]);

        // Assert - Metadata fields
        Assert.NotNull(result["Container"]);
        Assert.Equal("flac", result["Container"]);
        Assert.NotNull(result["HasLyrics"]);
        Assert.True((bool)result["HasLyrics"]!);

        // Assert - Genres (must be array, never null)
        Assert.NotNull(result["Genres"]);
        Assert.IsType<string[]>(result["Genres"]);
        Assert.NotNull(result["GenreItems"]);
        Assert.IsAssignableFrom<System.Collections.IEnumerable>(result["GenreItems"]);

        // Assert - UserData
        Assert.NotNull(result["UserData"]);
        var userData = result["UserData"] as Dictionary<string, object>;
        Assert.NotNull(userData);
        Assert.Contains("ItemId", userData.Keys);
        Assert.Contains("Key", userData.Keys);

        // Assert - Image fields
        Assert.NotNull(result["ImageTags"]);
        Assert.NotNull(result["BackdropImageTags"]);
        Assert.NotNull(result["ImageBlurHashes"]);

        // Assert - Location
        Assert.NotNull(result["LocationType"]);
        Assert.Equal("FileSystem", result["LocationType"]);

        // Assert - Parent references
        Assert.NotNull(result["ParentLogoItemId"]);
        Assert.NotNull(result["ParentBackdropItemId"]);
        Assert.NotNull(result["ParentBackdropImageTags"]);
    }

    [Fact]
    public void Track_MediaSources_Should_Have_Complete_Structure()
    {
        // Arrange
        var song = new Song
        {
            Id = "test-id",
            Title = "Test Song",
            Artist = "Test Artist",
            Album = "Test Album",
            Duration = 180,
            IsLocal = false,
            ExternalProvider = "Deezer",
            ExternalId = "123456"
        };

        // Act
        var result = _builder.ConvertSongToJellyfinItem(song);

        // Assert - MediaSources exists
        Assert.NotNull(result["MediaSources"]);
        var mediaSources = result["MediaSources"] as object[];
        Assert.NotNull(mediaSources);
        Assert.Single(mediaSources);

        var mediaSource = mediaSources[0] as Dictionary<string, object?>;
        Assert.NotNull(mediaSource);

        // Assert - Required MediaSource fields
        Assert.Contains("Protocol", mediaSource.Keys);
        Assert.Contains("Id", mediaSource.Keys);
        Assert.Contains("Path", mediaSource.Keys);
        Assert.Contains("Type", mediaSource.Keys);
        Assert.Contains("Container", mediaSource.Keys);
        Assert.Contains("Bitrate", mediaSource.Keys);
        Assert.Contains("ETag", mediaSource.Keys);
        Assert.Contains("RunTimeTicks", mediaSource.Keys);

        // Assert - Boolean flags
        Assert.Contains("IsRemote", mediaSource.Keys);
        Assert.Contains("IsInfiniteStream", mediaSource.Keys);
        Assert.Contains("RequiresOpening", mediaSource.Keys);
        Assert.Contains("RequiresClosing", mediaSource.Keys);
        Assert.Contains("RequiresLooping", mediaSource.Keys);
        Assert.Contains("SupportsProbing", mediaSource.Keys);
        Assert.Contains("SupportsTranscoding", mediaSource.Keys);
        Assert.Contains("SupportsDirectStream", mediaSource.Keys);
        Assert.Contains("SupportsDirectPlay", mediaSource.Keys);
        Assert.Contains("ReadAtNativeFramerate", mediaSource.Keys);
        Assert.Contains("IgnoreDts", mediaSource.Keys);
        Assert.Contains("IgnoreIndex", mediaSource.Keys);
        Assert.Contains("GenPtsInput", mediaSource.Keys);
        Assert.Contains("UseMostCompatibleTranscodingProfile", mediaSource.Keys);
        Assert.Contains("HasSegments", mediaSource.Keys);

        // Assert - Arrays (must not be null)
        Assert.Contains("MediaStreams", mediaSource.Keys);
        Assert.NotNull(mediaSource["MediaStreams"]);
        Assert.Contains("MediaAttachments", mediaSource.Keys);
        Assert.NotNull(mediaSource["MediaAttachments"]);
        Assert.Contains("Formats", mediaSource.Keys);
        Assert.NotNull(mediaSource["Formats"]);
        Assert.Contains("RequiredHttpHeaders", mediaSource.Keys);
        Assert.NotNull(mediaSource["RequiredHttpHeaders"]);

        // Assert - Other fields
        Assert.Contains("TranscodingSubProtocol", mediaSource.Keys);
        Assert.Contains("DefaultAudioStreamIndex", mediaSource.Keys);
    }

    [Fact]
    public void Track_MediaStreams_Should_Have_Complete_Audio_Stream()
    {
        // Arrange
        var song = new Song
        {
            Id = "test-id",
            Title = "Test Song",
            Artist = "Test Artist",
            IsLocal = false,
            ExternalProvider = "Deezer"
        };

        // Act
        var result = _builder.ConvertSongToJellyfinItem(song);
        var mediaSources = result["MediaSources"] as object[];
        var mediaSource = mediaSources![0] as Dictionary<string, object?>;
        var mediaStreams = mediaSource!["MediaStreams"] as object[];

        // Assert
        Assert.NotNull(mediaStreams);
        Assert.Single(mediaStreams);

        var audioStream = mediaStreams[0] as Dictionary<string, object?>;
        Assert.NotNull(audioStream);

        // Assert - Required audio stream fields
        Assert.Contains("Codec", audioStream.Keys);
        Assert.Equal("flac", audioStream["Codec"]);
        Assert.Contains("Type", audioStream.Keys);
        Assert.Equal("Audio", audioStream["Type"]);
        Assert.Contains("BitRate", audioStream.Keys);
        Assert.Contains("Channels", audioStream.Keys);
        Assert.Contains("SampleRate", audioStream.Keys);
        Assert.Contains("BitDepth", audioStream.Keys);
        Assert.Contains("ChannelLayout", audioStream.Keys);
        Assert.Contains("TimeBase", audioStream.Keys);
        Assert.Contains("DisplayTitle", audioStream.Keys);

        // Assert - Video-related fields (required even for audio)
        Assert.Contains("VideoRange", audioStream.Keys);
        Assert.Contains("VideoRangeType", audioStream.Keys);
        Assert.Contains("AudioSpatialFormat", audioStream.Keys);

        // Assert - Localization
        Assert.Contains("LocalizedDefault", audioStream.Keys);
        Assert.Contains("LocalizedExternal", audioStream.Keys);

        // Assert - Boolean flags
        Assert.Contains("IsInterlaced", audioStream.Keys);
        Assert.Contains("IsAVC", audioStream.Keys);
        Assert.Contains("IsDefault", audioStream.Keys);
        Assert.Contains("IsForced", audioStream.Keys);
        Assert.Contains("IsHearingImpaired", audioStream.Keys);
        Assert.Contains("IsExternal", audioStream.Keys);
        Assert.Contains("IsTextSubtitleStream", audioStream.Keys);
        Assert.Contains("SupportsExternalStream", audioStream.Keys);

        // Assert - Index and Level
        Assert.Contains("Index", audioStream.Keys);
        Assert.Contains("Level", audioStream.Keys);
    }

    [Fact]
    public void Album_Response_Should_Have_All_Required_Fields()
    {
        // Arrange
        var album = new Album
        {
            Id = "album-id",
            Title = "Test Album",
            Artist = "Test Artist",
            Year = 2024,
            Genre = "Rock",
            IsLocal = false,
            ExternalProvider = "Deezer"
        };

        // Act
        var result = _builder.ConvertAlbumToJellyfinItem(album);

        // Assert
        Assert.NotNull(result["Name"]);
        Assert.NotNull(result["ServerId"]);
        Assert.NotNull(result["Id"]);
        Assert.NotNull(result["Type"]);
        Assert.Equal("MusicAlbum", result["Type"]);
        Assert.True((bool)result["IsFolder"]!);
        Assert.NotNull(result["MediaType"]);
        Assert.Equal("Unknown", result["MediaType"]);

        // Assert - Genres
        Assert.NotNull(result["Genres"]);
        Assert.IsType<string[]>(result["Genres"]);
        Assert.NotNull(result["GenreItems"]);

        // Assert - Artists
        Assert.NotNull(result["Artists"]);
        Assert.NotNull(result["ArtistItems"]);
        Assert.NotNull(result["AlbumArtist"]);
        Assert.NotNull(result["AlbumArtists"]);

        // Assert - Parent references
        Assert.NotNull(result["ParentLogoItemId"]);
        Assert.NotNull(result["ParentBackdropItemId"]);
        Assert.NotNull(result["ParentLogoImageTag"]);
    }

    [Fact]
    public void Artist_Response_Should_Have_All_Required_Fields()
    {
        // Arrange
        var artist = new Artist
        {
            Id = "artist-id",
            Name = "Test Artist",
            AlbumCount = 5,
            IsLocal = false,
            ExternalProvider = "Deezer"
        };

        // Act
        var result = _builder.ConvertArtistToJellyfinItem(artist);

        // Assert
        Assert.NotNull(result["Name"]);
        Assert.NotNull(result["ServerId"]);
        Assert.NotNull(result["Id"]);
        Assert.NotNull(result["Type"]);
        Assert.Equal("MusicArtist", result["Type"]);
        Assert.True((bool)result["IsFolder"]!);
        Assert.NotNull(result["MediaType"]);
        Assert.Equal("Unknown", result["MediaType"]);

        // Assert - Genres (empty array for artists)
        Assert.NotNull(result["Genres"]);
        Assert.IsType<string[]>(result["Genres"]);
        Assert.NotNull(result["GenreItems"]);

        // Assert - Album count
        Assert.NotNull(result["AlbumCount"]);
        Assert.Equal(5, result["AlbumCount"]);

        // Assert - RunTimeTicks
        Assert.NotNull(result["RunTimeTicks"]);
        Assert.Equal(0, result["RunTimeTicks"]);
    }

    [Fact]
    public void All_Entities_Should_Have_UserData_With_ItemId()
    {
        // Arrange
        var song = new Song { Id = "song-id", Title = "Test", Artist = "Test" };
        var album = new Album { Id = "album-id", Title = "Test", Artist = "Test" };
        var artist = new Artist { Id = "artist-id", Name = "Test" };

        // Act
        var songResult = _builder.ConvertSongToJellyfinItem(song);
        var albumResult = _builder.ConvertAlbumToJellyfinItem(album);
        var artistResult = _builder.ConvertArtistToJellyfinItem(artist);

        // Assert
        var songUserData = songResult["UserData"] as Dictionary<string, object>;
        Assert.NotNull(songUserData);
        Assert.Contains("ItemId", songUserData.Keys);
        Assert.Equal("song-id", songUserData["ItemId"]);

        var albumUserData = albumResult["UserData"] as Dictionary<string, object>;
        Assert.NotNull(albumUserData);
        Assert.Contains("ItemId", albumUserData.Keys);
        Assert.Equal("album-id", albumUserData["ItemId"]);

        var artistUserData = artistResult["UserData"] as Dictionary<string, object>;
        Assert.NotNull(artistUserData);
        Assert.Contains("ItemId", artistUserData.Keys);
        Assert.Equal("artist-id", artistUserData["ItemId"]);
    }
}

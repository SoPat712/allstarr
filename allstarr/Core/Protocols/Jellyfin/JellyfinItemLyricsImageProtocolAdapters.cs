using System.Text.Json;
using System.Text.RegularExpressions;
using allstarr.Models.Domain;
using allstarr.Models.Lyrics;
using allstarr.Services.Common;
using allstarr.Services.Jellyfin;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Core.Protocols.Jellyfin;

public interface IJellyfinItemProtocolAdapter
{
    IActionResult ShapeSong(Song song);
    IActionResult ShapeAlbum(Album album);
    IActionResult ShapeArtist(Artist artist, List<Album> albums);
    IActionResult ShapeNotFound(string itemType);
}

public sealed class JellyfinItemProtocolAdapter(JellyfinResponseBuilder responseBuilder)
    : IJellyfinItemProtocolAdapter
{
    public IActionResult ShapeSong(Song song) => responseBuilder.CreateSongResponse(song);

    public IActionResult ShapeAlbum(Album album) => responseBuilder.CreateAlbumResponse(album);

    public IActionResult ShapeArtist(Artist artist, List<Album> albums) =>
        responseBuilder.CreateArtistResponse(artist, albums);

    public IActionResult ShapeNotFound(string itemType) =>
        responseBuilder.CreateError(StatusCodes.Status404NotFound, $"{itemType} not found");
}

public interface IJellyfinImageProtocolAdapter
{
    JellyfinImageProtocolResponse Shape(byte[] imageBytes, string contentType, IHeaderDictionary requestHeaders);
}

public sealed record JellyfinImageProtocolResponse(
    int StatusCode,
    string ContentType,
    byte[]? Body,
    string ETag);

public sealed class JellyfinImageProtocolAdapter : IJellyfinImageProtocolAdapter
{
    public JellyfinImageProtocolResponse Shape(
        byte[] imageBytes,
        string contentType,
        IHeaderDictionary requestHeaders)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);

        var etag = ImageConditionalRequestHelper.ComputeStrongETag(imageBytes);
        var statusCode = ImageConditionalRequestHelper.MatchesIfNoneMatch(requestHeaders, etag)
            ? StatusCodes.Status304NotModified
            : StatusCodes.Status200OK;

        return new JellyfinImageProtocolResponse(
            statusCode,
            contentType,
            statusCode == StatusCodes.Status304NotModified ? null : imageBytes,
            etag);
    }
}

public interface IJellyfinLyricsProtocolAdapter
{
    JellyfinProtocolResponse Shape(LyricsInfo lyrics);
}

public sealed class JellyfinLyricsProtocolAdapter : IJellyfinLyricsProtocolAdapter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = null,
        DictionaryKeyPolicy = null
    };

    public JellyfinProtocolResponse Shape(LyricsInfo lyrics)
    {
        ArgumentNullException.ThrowIfNull(lyrics);

        var isSynced = !string.IsNullOrEmpty(lyrics.SyncedLyrics);
        var lines = isSynced
            ? ParseSyncedLines(lyrics.SyncedLyrics!)
            : ParsePlainLines(lyrics.PlainLyrics);

        var response = new JellyfinLyricsResponse(
            new JellyfinLyricsMetadata(
                lyrics.ArtistName,
                lyrics.AlbumName,
                lyrics.TrackName,
                lyrics.Duration,
                isSynced),
            lines);

        return new JellyfinProtocolResponse(
            StatusCodes.Status200OK,
            "application/json",
            JsonSerializer.Serialize(response, SerializerOptions));
    }

    private static IReadOnlyList<Dictionary<string, object>> ParseSyncedLines(string lyrics)
    {
        var result = new List<Dictionary<string, object>>();
        foreach (var line in lyrics.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var match = Regex.Match(line, @"^\[(\d+):(\d+)\.(\d+)\]\s*(.*)$");
            if (!match.Success)
            {
                continue;
            }

            var minutes = int.Parse(match.Groups[1].Value);
            var seconds = int.Parse(match.Groups[2].Value);
            var centiseconds = int.Parse(match.Groups[3].Value);
            var totalMilliseconds = (minutes * 60 + seconds) * 1000 + centiseconds * 10;
            result.Add(new Dictionary<string, object>
            {
                ["Text"] = match.Groups[4].Value,
                ["Start"] = totalMilliseconds * 10_000L
            });
        }

        return result;
    }

    private static IReadOnlyList<Dictionary<string, object>> ParsePlainLines(string? lyrics)
    {
        var result = (lyrics ?? string.Empty)
            .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => new Dictionary<string, object> { ["Text"] = line.Trim() })
            .ToList();

        if (result.Count == 0)
        {
            result.Add(new Dictionary<string, object> { ["Text"] = string.Empty });
        }

        return result;
    }

    private sealed record JellyfinLyricsResponse(
        JellyfinLyricsMetadata Metadata,
        IReadOnlyList<Dictionary<string, object>> Lyrics);

    private sealed record JellyfinLyricsMetadata(
        string Artist,
        string Album,
        string Title,
        int Length,
        bool IsSynced);
}

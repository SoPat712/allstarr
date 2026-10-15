using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using allstarr.Core.Music;
using allstarr.Models.Domain;
using allstarr.Models.Lyrics;
using allstarr.Models.Subsonic;
using allstarr.Services;
using allstarr.Services.Local;
using allstarr.Services.Subsonic;

namespace allstarr.Core.Protocols.Subsonic;

public interface ISubsonicLyricsLookup
{
    Task<SubsonicStructuredLyrics?> FindAsync(
        ProtocolExecutionContext protocol,
        string provider,
        string externalId,
        CancellationToken cancellationToken);
}

public sealed partial class SubsonicLyricsLookup : ISubsonicLyricsLookup
{
    private readonly IProtocolProviderGateway _providerGateway;
    private readonly IProtocolLyricsResolver _lyrics;

    public SubsonicLyricsLookup(
        IProtocolProviderGateway providerGateway,
        IProtocolLyricsResolver lyrics)
    {
        _providerGateway = providerGateway;
        _lyrics = lyrics;
    }

    public async Task<SubsonicStructuredLyrics?> FindAsync(
        ProtocolExecutionContext protocol,
        string provider,
        string externalId,
        CancellationToken cancellationToken)
    {
        var found = await MusicLyrics.FindForProviderSongAsync(
            _providerGateway, _lyrics, protocol, provider, externalId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return found == null ? null : SubsonicStructuredLyricsMapper.Map(found.Song, found.Lyrics);
    }
}

public static partial class SubsonicStructuredLyricsMapper
{
    public static SubsonicStructuredLyrics? Map(Song song, LyricsInfo? lyrics)
    {
        ArgumentNullException.ThrowIfNull(song);
        if (!string.IsNullOrWhiteSpace(lyrics?.SyncedLyrics))
        {
            var lines = ParseSynced(lyrics.SyncedLyrics);
            if (lines.Count > 0)
            {
                return CreateLyrics(song, true, lines);
            }
        }

        if (string.IsNullOrWhiteSpace(lyrics?.PlainLyrics))
        {
            return null;
        }

        var linesPlain = lyrics.PlainLyrics
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(line => new SubsonicLyricLine(0, line.TrimEnd()))
            .ToList();
        return linesPlain.Count == 0
            ? null
            : CreateLyrics(song, false, linesPlain);
    }

    private static SubsonicStructuredLyrics CreateLyrics(
        Song song,
        bool synced,
        IReadOnlyList<SubsonicLyricLine> lines) => new(
        song.Artist,
        song.Title,
        "xxx",
        0,
        synced,
        lines);

    private static List<SubsonicLyricLine> ParseSynced(string value) =>
        MusicLyrics.ParseSyncedLines(value).Select(line => new SubsonicLyricLine(line.StartMilliseconds, line.Text)).ToList();
}

public sealed class SubsonicLyricsProtocolAdapter
{
    private readonly ILocalLibraryService _localLibraryService;
    private readonly ISubsonicLyricsLookup _lyricsLookup;
    private readonly SubsonicProxyService _proxyService;
    private readonly SubsonicResponseBuilder _responseBuilder;
    private readonly IProtocolLyricsResolver _lyrics;

    public SubsonicLyricsProtocolAdapter(
        ILocalLibraryService localLibraryService,
        ISubsonicLyricsLookup lyricsLookup,
        SubsonicProxyService proxyService,
        SubsonicResponseBuilder responseBuilder,
        IProtocolLyricsResolver lyrics)
    {
        _localLibraryService = localLibraryService;
        _lyricsLookup = lyricsLookup;
        _proxyService = proxyService;
        _responseBuilder = responseBuilder;
        _lyrics = lyrics;
    }

    public async Task<IActionResult> GetLyricsBySongIdAsync(
        SubsonicRequestParameters parameters,
        ProtocolExecutionContext protocol,
        CancellationToken cancellationToken)
    {
        var id = parameters.GetValueOrDefault("id", string.Empty);
        var format = parameters.GetValueOrDefault("f", "xml");
        if (string.IsNullOrWhiteSpace(id))
        {
            return _responseBuilder.CreateError(format, 10, "Missing id parameter");
        }

        var (isExternal, provider, externalId) = _localLibraryService.ParseSongId(id);
        if (!isExternal)
        {
            var metadata = await _proxyService.RelayRawAsync("rest/getSong",
                parameters.SetValue("f", "json"), cancellationToken);
            if ((int)metadata.StatusCode is < 200 or >= 300)
                return new SubsonicRelayResult(metadata.Body, metadata.ContentType ?? $"application/{format}", (int)metadata.StatusCode);
            var song = ParseNativeSong(metadata.Body, id);
            if (song == null) return _responseBuilder.CreateError(format, 70, "Song not found");
            SubsonicProxyResponse? backendLyrics = null;
            var selected = await _lyrics.FindAsync(protocol, MusicLyrics.ForLookup(song), id,
                new ProtocolLyricsSources(Backend: async token =>
                {
                    var response = await _proxyService.RelayRawAsync("rest/getLyricsBySongId", parameters, token);
                    backendLyrics = response;
                    if ((int)response.StatusCode is < 200 or >= 300) return null;
                    return BackendLyrics.ParseSubsonic(response.Body,
                        response.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) ?? format == "json");
                }));
            if ((selected == null || selected.Source == "backend") && backendLyrics != null)
                return new SubsonicRelayResult(backendLyrics.Body,
                    backendLyrics.ContentType ?? $"application/{format}", (int)backendLyrics.StatusCode);
            return _responseBuilder.CreateLyricsBySongIdResponse(format, SubsonicStructuredLyricsMapper.Map(song, selected));
        }

        var lyrics = await _lyricsLookup.FindAsync(protocol, provider!, externalId!, cancellationToken);
        return _responseBuilder.CreateLyricsBySongIdResponse(format, lyrics);
    }

    private static Song? ParseNativeSong(byte[] body, string expectedId)
    {
        if (body.Length > 2 * 1024 * 1024) return null;
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("subsonic-response", out var response) ||
                response.ValueKind != JsonValueKind.Object || Text(response, "status") != "ok" ||
                !response.TryGetProperty("song", out var song) || song.ValueKind != JsonValueKind.Object ||
                Text(song, "id") != expectedId || song.TryGetProperty("isVideo", out var video) && video.ValueKind == JsonValueKind.True ||
                song.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() != "music")
                return null;
            int? duration = song.TryGetProperty("duration", out var length) && length.TryGetInt32(out var seconds) && seconds >= 0
                ? seconds : null;
            return new Song
            {
                Id = expectedId,
                IsLocal = true,
                Title = Text(song, "title"),
                Artist = Text(song, "artist"),
                Album = Text(song, "album"),
                Duration = duration
            };
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException) { return null; }

        static string Text(JsonElement item, string property) => item.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    }

    private sealed class SubsonicRelayResult(
        byte[] body,
        string contentType,
        int statusCode) : IActionResult
    {
        public async Task ExecuteResultAsync(ActionContext context)
        {
            var response = context.HttpContext.Response;
            response.StatusCode = statusCode;
            response.ContentType = contentType;
            response.ContentLength = body.Length;
            await response.Body.WriteAsync(body, context.HttpContext.RequestAborted);
        }
    }
}

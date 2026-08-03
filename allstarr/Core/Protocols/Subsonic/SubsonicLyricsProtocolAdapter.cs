using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
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
    private readonly IMusicMetadataService _metadataService;
    private readonly IProtocolProviderGateway _providerGateway;
    private readonly IProtocolLyricsResolver _lyrics;

    public SubsonicLyricsLookup(
        IMusicMetadataService metadataService,
        IProtocolProviderGateway providerGateway,
        IProtocolLyricsResolver lyrics)
    {
        _metadataService = metadataService;
        _providerGateway = providerGateway;
        _lyrics = lyrics;
    }

    public async Task<SubsonicStructuredLyrics?> FindAsync(
        ProtocolExecutionContext protocol,
        string provider,
        string externalId,
        CancellationToken cancellationToken)
    {
        var song = await _providerGateway.GetSongAsync(protocol, provider, externalId) ??
                   await _metadataService.GetSongAsync(provider, externalId, cancellationToken);
        if (song == null)
        {
            return null;
        }

        var lyrics = await _lyrics.FindAsync(
            protocol, song, $"{provider}:{externalId}", provider, externalId, song.SpotifyId);
        cancellationToken.ThrowIfCancellationRequested();

        return SubsonicStructuredLyricsMapper.Map(song, lyrics);
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

    private static List<SubsonicLyricLine> ParseSynced(string value)
    {
        var lines = new List<SubsonicLyricLine>();
        foreach (var rawLine in value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var matches = TimestampRegex().Matches(rawLine);
            if (matches.Count == 0)
            {
                continue;
            }

            var text = TimestampRegex().Replace(rawLine, string.Empty).Trim();
            foreach (Match match in matches)
            {
                var minutes = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                var seconds = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                var milliseconds = match.Groups[3].Success
                    ? int.Parse(match.Groups[3].Value.PadRight(3, '0')[..3], CultureInfo.InvariantCulture)
                    : 0;
                lines.Add(new SubsonicLyricLine(
                    (((long)minutes * 60) + seconds) * 1000 + milliseconds,
                    text));
            }
        }

        lines.Sort((left, right) => left.StartMilliseconds.CompareTo(right.StartMilliseconds));
        return lines;
    }

    [GeneratedRegex(@"\[(\d{1,3}):([0-5]?\d)(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled)]
    private static partial Regex TimestampRegex();
}

public sealed class SubsonicLyricsProtocolAdapter
{
    private readonly ILocalLibraryService _localLibraryService;
    private readonly ISubsonicLyricsLookup _lyricsLookup;
    private readonly SubsonicProxyService _proxyService;
    private readonly SubsonicResponseBuilder _responseBuilder;

    public SubsonicLyricsProtocolAdapter(
        ILocalLibraryService localLibraryService,
        ISubsonicLyricsLookup lyricsLookup,
        SubsonicProxyService proxyService,
        SubsonicResponseBuilder responseBuilder)
    {
        _localLibraryService = localLibraryService;
        _lyricsLookup = lyricsLookup;
        _proxyService = proxyService;
        _responseBuilder = responseBuilder;
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
            var result = await _proxyService.RelayRawAsync(
                "rest/getLyricsBySongId",
                parameters,
                cancellationToken);
            return new SubsonicRelayResult(
                result.Body,
                result.ContentType ?? $"application/{format}",
                (int)result.StatusCode);
        }

        var lyrics = await _lyricsLookup.FindAsync(protocol, provider!, externalId!, cancellationToken);
        return _responseBuilder.CreateLyricsBySongIdResponse(format, lyrics);
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

using System.Text.RegularExpressions;
using TagLib;
using allstarr.Models.Domain;
using allstarr.Models.Lyrics;
using allstarr.Models.Settings;
using allstarr.Models.Spotify;
using allstarr.Services.Common;
using Microsoft.Extensions.Options;

namespace allstarr.Services.Lyrics;

public class KeptLyricsSidecarService : IKeptLyricsSidecarService
{
    private static readonly Regex ProviderSuffixRegex = new(
        @"\[(?<provider>[A-Za-z0-9_-]+)-(?<externalId>[^\]]+)\]$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly LyricsOrchestrator _lyricsOrchestrator;
    private readonly RedisCacheService _cache;
    private readonly SpotifyImportSettings _spotifySettings;
    private readonly OdesliService _odesliService;
    private readonly ILogger<KeptLyricsSidecarService> _logger;

    public KeptLyricsSidecarService(
        LyricsOrchestrator lyricsOrchestrator,
        RedisCacheService cache,
        IOptions<SpotifyImportSettings> spotifySettings,
        OdesliService odesliService,
        ILogger<KeptLyricsSidecarService> logger)
    {
        _lyricsOrchestrator = lyricsOrchestrator;
        _cache = cache;
        _spotifySettings = spotifySettings.Value;
        _odesliService = odesliService;
        _logger = logger;
    }

    public string GetSidecarPath(string audioFilePath)
    {
        return Path.ChangeExtension(audioFilePath, ".lrc");
    }

    public async Task<string?> EnsureSidecarAsync(
        string audioFilePath,
        Song? song = null,
        string? externalProvider = null,
        string? externalId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(audioFilePath) || !System.IO.File.Exists(audioFilePath))
        {
            return null;
        }

        var sidecarPath = GetSidecarPath(audioFilePath);
        if (System.IO.File.Exists(sidecarPath))
        {
            return sidecarPath;
        }

        try
        {
            var inferredExternalRef = ParseExternalReferenceFromPath(audioFilePath);
            externalProvider ??= inferredExternalRef.Provider;
            externalId ??= inferredExternalRef.ExternalId;

            var metadata = ReadAudioMetadata(audioFilePath);
            var artistNames = ResolveArtists(song, metadata);
            var title = FirstNonEmpty(
                StripTrackDecorators(song?.Title),
                StripTrackDecorators(metadata.Title),
                GetFallbackTitleFromPath(audioFilePath));
            var album = FirstNonEmpty(
                StripTrackDecorators(song?.Album),
                StripTrackDecorators(metadata.Album));
            var durationSeconds = song?.Duration ?? metadata.DurationSeconds;

            if (string.IsNullOrWhiteSpace(title) || artistNames.Count == 0)
            {
                _logger.LogDebug("Skipping lyrics sidecar generation for {Path}: missing title or artist metadata", audioFilePath);
                return null;
            }

            var spotifyTrackId = FirstNonEmpty(song?.SpotifyId);
            if (string.IsNullOrWhiteSpace(spotifyTrackId) &&
                !string.IsNullOrWhiteSpace(externalProvider) &&
                !string.IsNullOrWhiteSpace(externalId))
            {
                spotifyTrackId = await ResolveSpotifyTrackIdAsync(externalProvider, externalId, cancellationToken);
            }

            var lyrics = await _lyricsOrchestrator.GetLyricsAsync(
                trackName: title,
                artistNames: artistNames.ToArray(),
                albumName: album,
                durationSeconds: durationSeconds,
                spotifyTrackId: spotifyTrackId);

            if (lyrics == null)
            {
                return null;
            }

            var lrcContent = BuildLrcContent(
                lyrics,
                title,
                artistNames,
                album,
                durationSeconds);

            if (string.IsNullOrWhiteSpace(lrcContent))
            {
                return null;
            }

            await System.IO.File.WriteAllTextAsync(sidecarPath, lrcContent, cancellationToken);
            _logger.LogInformation("Saved lyrics sidecar: {SidecarPath}", sidecarPath);
            return sidecarPath;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to create lyrics sidecar for {Path}", audioFilePath);
            return null;
        }
    }

    private async Task<string?> ResolveSpotifyTrackIdAsync(
        string externalProvider,
        string externalId,
        CancellationToken cancellationToken)
    {
        var spotifyId = await FindSpotifyIdFromMatchedTracksAsync(externalProvider, externalId);
        if (!string.IsNullOrWhiteSpace(spotifyId))
        {
            return spotifyId;
        }

        return externalProvider.ToLowerInvariant() switch
        {
            "squidwtf" => await _odesliService.ConvertTidalToSpotifyIdAsync(externalId, cancellationToken),
            "deezer" => await _odesliService.ConvertUrlToSpotifyIdAsync($"https://www.deezer.com/track/{externalId}", cancellationToken),
            "qobuz" => await _odesliService.ConvertUrlToSpotifyIdAsync($"https://www.qobuz.com/us-en/album/-/-/{externalId}", cancellationToken),
            _ => null
        };
    }

    private async Task<string?> FindSpotifyIdFromMatchedTracksAsync(string externalProvider, string externalId)
    {
        if (_spotifySettings.Playlists == null || _spotifySettings.Playlists.Count == 0)
        {
            return null;
        }

        foreach (var playlist in _spotifySettings.Playlists)
        {
            var cacheKey = CacheKeyBuilder.BuildSpotifyMatchedTracksKey(playlist.Name);
            var matchedTracks = await _cache.GetAsync<List<MatchedTrack>>(cacheKey);

            var match = matchedTracks?.FirstOrDefault(track =>
                track.MatchedSong != null &&
                string.Equals(track.MatchedSong.ExternalProvider, externalProvider, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(track.MatchedSong.ExternalId, externalId, StringComparison.Ordinal));

            if (match != null && !string.IsNullOrWhiteSpace(match.SpotifyId))
            {
                return match.SpotifyId;
            }
        }

        return null;
    }

    private static (string? Provider, string? ExternalId) ParseExternalReferenceFromPath(string audioFilePath)
    {
        var baseName = Path.GetFileNameWithoutExtension(audioFilePath);
        var match = ProviderSuffixRegex.Match(baseName);
        if (!match.Success)
        {
            return (null, null);
        }

        return (
            match.Groups["provider"].Value,
            match.Groups["externalId"].Value);
    }

    private static AudioMetadata ReadAudioMetadata(string audioFilePath)
    {
        try
        {
            using var tagFile = TagLib.File.Create(audioFilePath);
            return new AudioMetadata
            {
                Title = tagFile.Tag.Title,
                Album = tagFile.Tag.Album,
                Artists = tagFile.Tag.Performers?.Where(value => !string.IsNullOrWhiteSpace(value)).ToList() ?? new List<string>(),
                DurationSeconds = (int)Math.Round(tagFile.Properties.Duration.TotalSeconds)
            };
        }
        catch
        {
            return new AudioMetadata();
        }
    }

    private static List<string> ResolveArtists(Song? song, AudioMetadata metadata)
    {
        var artists = new List<string>();

        if (song?.Artists != null && song.Artists.Count > 0)
        {
            artists.AddRange(song.Artists.Where(value => !string.IsNullOrWhiteSpace(value)));
        }
        else if (!string.IsNullOrWhiteSpace(song?.Artist))
        {
            artists.Add(song.Artist);
        }

        if (artists.Count == 0 && metadata.Artists.Count > 0)
        {
            artists.AddRange(metadata.Artists);
        }

        return artists
            .Select(StripTrackDecorators)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string BuildLrcContent(
        LyricsInfo lyrics,
        string fallbackTitle,
        IReadOnlyList<string> fallbackArtists,
        string? fallbackAlbum,
        int fallbackDurationSeconds)
    {
        var title = FirstNonEmpty(lyrics.TrackName, fallbackTitle);
        var artist = FirstNonEmpty(lyrics.ArtistName, string.Join(", ", fallbackArtists));
        var album = FirstNonEmpty(lyrics.AlbumName, fallbackAlbum);
        var durationSeconds = lyrics.Duration > 0 ? lyrics.Duration : fallbackDurationSeconds;

        var body = FirstNonEmpty(
            NormalizeLineEndings(lyrics.SyncedLyrics),
            NormalizeLineEndings(lyrics.PlainLyrics));

        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        var headerLines = new List<string>();
        if (!string.IsNullOrWhiteSpace(artist))
        {
            headerLines.Add($"[ar:{artist}]");
        }

        if (!string.IsNullOrWhiteSpace(album))
        {
            headerLines.Add($"[al:{album}]");
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            headerLines.Add($"[ti:{title}]");
        }

        if (durationSeconds > 0)
        {
            var duration = TimeSpan.FromSeconds(durationSeconds);
            headerLines.Add($"[length:{(int)duration.TotalMinutes}:{duration.Seconds:D2}]");
        }

        return headerLines.Count == 0
            ? body
            : $"{string.Join('\n', headerLines)}\n\n{body}";
    }

    private static string? GetFallbackTitleFromPath(string audioFilePath)
    {
        var baseName = Path.GetFileNameWithoutExtension(audioFilePath);
        baseName = ProviderSuffixRegex.Replace(baseName, string.Empty).Trim();
        baseName = Regex.Replace(baseName, @"^\d+\s*-\s*", string.Empty);
        return baseName.Trim();
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    private static string NormalizeLineEndings(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
    }

    private static string StripTrackDecorators(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value
            .Replace(" [S]", "", StringComparison.Ordinal)
            .Replace(" [E]", "", StringComparison.Ordinal)
            .Trim();
    }

    private sealed class AudioMetadata
    {
        public string? Title { get; init; }
        public string? Album { get; init; }
        public List<string> Artists { get; init; } = new();
        public int DurationSeconds { get; init; }
    }
}

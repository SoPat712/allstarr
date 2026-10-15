using allstarr.Core.Music;
using allstarr.Core.Protocols;
using System.Text.RegularExpressions;
using TagLib;
using allstarr.Models.Domain;
using allstarr.Models.Lyrics;
using allstarr.Services.Common;

namespace allstarr.Services.Lyrics;

public interface IKeptLyricsSidecarService
{
    string GetSidecarPath(string audioFilePath);

    Task<string?> EnsureSidecarAsync(
        string audioFilePath,
        Song? song = null,
        string? externalProvider = null,
        string? externalId = null,
        CancellationToken cancellationToken = default,
        ProtocolExecutionContext? protocol = null);
}

public class KeptLyricsSidecarService : IKeptLyricsSidecarService
{
    private static readonly Regex ProviderSuffixRegex = new(
        @"\[(?<provider>[A-Za-z0-9_-]+)-(?<externalId>[^\]]+)\]$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IProtocolLyricsResolver _lyrics;
    private readonly ILogger<KeptLyricsSidecarService> _logger;

    public KeptLyricsSidecarService(
        IProtocolLyricsResolver lyrics,
        ILogger<KeptLyricsSidecarService> logger)
    {
        _lyrics = lyrics;
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
        CancellationToken cancellationToken = default,
        ProtocolExecutionContext? protocol = null)
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
                _logger.LogDebug("Skipping lyrics sidecar generation without title and artist metadata");
                return null;
            }

            if (protocol?.Actor == null) return null;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, protocol.CancellationToken);
            cancellation.Token.ThrowIfCancellationRequested();
            var context = protocol.CancellationToken == cancellationToken ? protocol : new ProtocolExecutionContext(
                protocol.Protocol, protocol.BackendInstanceId, protocol.VerifiedBackendPrincipalId, protocol.Principal,
                protocol.CorrelationId, protocol.Deadline, cancellation.Token, protocol.Client)
            { Policy = protocol.Policy, RequestedItemId = protocol.RequestedItemId };
            var lookup = MusicLyrics.ForLookup(new Song
            {
                Title = title,
                Artist = artistNames[0],
                Artists = artistNames,
                Album = album,
                Duration = Math.Clamp(durationSeconds, 0, 86_400),
                SpotifyId = song?.SpotifyId
            });
            var resourceKey = externalProvider != null && externalId != null ? $"{externalProvider}:{externalId}" :
                Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(audioFilePath)));
            var lyrics = await _lyrics.FindAsync(context, lookup, resourceKey,
                externalProvider, externalId, lookup.SpotifyId);

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

            context.CancellationToken.ThrowIfCancellationRequested();
            var temporary = Path.Combine(Path.GetDirectoryName(sidecarPath)!, $".lyrics-{Guid.NewGuid():N}.tmp");
            try
            {
                await System.IO.File.WriteAllTextAsync(temporary, lrcContent, context.CancellationToken);
                context.CancellationToken.ThrowIfCancellationRequested();
                System.IO.File.Move(temporary, sidecarPath);
            }
            finally { System.IO.File.Delete(temporary); }
            _logger.LogInformation("Saved a lyrics sidecar");
            return sidecarPath;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || protocol?.CancellationToken.IsCancellationRequested == true) { throw; }
        catch (Exception)
        {
            _logger.LogDebug("Could not create a lyrics sidecar");
            return null;
        }
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

    private static string StripTrackDecorators(string? value) => MusicLyrics.StripPresentationLabels(value);

    private sealed class AudioMetadata
    {
        public string? Title { get; init; }
        public string? Album { get; init; }
        public List<string> Artists { get; init; } = new();
        public int DurationSeconds { get; init; }
    }
}

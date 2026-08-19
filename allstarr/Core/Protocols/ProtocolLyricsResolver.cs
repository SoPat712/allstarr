using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Models.Domain;
using allstarr.Models.Lyrics;
using allstarr.Services.Common;

namespace allstarr.Core.Protocols;

public interface IProtocolLyricsResolver
{
    Task<LyricsInfo?> FindAsync(
        ProtocolExecutionContext protocol,
        Song song,
        string resourceKey,
        string? sourceProvider = null,
        string? sourceExternalId = null,
        string? spotifyTrackId = null);
}

public sealed class ProtocolLyricsResolver(
    IProtocolProviderGateway providers,
    ILogger<ProtocolLyricsResolver> logger,
    OdesliService? odesli = null) : IProtocolLyricsResolver
{
    public async Task<LyricsInfo?> FindAsync(
        ProtocolExecutionContext protocol,
        Song song,
        string resourceKey,
        string? sourceProvider = null,
        string? sourceExternalId = null,
        string? spotifyTrackId = null)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        ArgumentNullException.ThrowIfNull(song);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceKey);
        var artists = song.Artists.Count > 0 ? song.Artists : [song.Artist];

        var order = providers.GetProviderOrder(ProviderCapabilityKind.Lyrics);
        if (!string.IsNullOrWhiteSpace(sourceProvider) &&
            order.Contains(sourceProvider, StringComparer.OrdinalIgnoreCase))
            order = [sourceProvider, .. order];
        var sourceUrl = !string.IsNullOrWhiteSpace(sourceProvider) && !string.IsNullOrWhiteSpace(sourceExternalId)
            ? OdesliService.BuildTrackUrl(sourceProvider, sourceExternalId)
            : null;

        foreach (var providerId in order.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var externalId = ResolveExternalId(
                    providerId, resourceKey, sourceProvider, sourceExternalId, spotifyTrackId ?? song.SpotifyId);
                if (externalId == null && sourceUrl != null && odesli != null)
                    externalId = await odesli.TranslateTrackUrlAsync(
                        sourceUrl, providerId, protocol.CancellationToken);
                if (externalId == null) continue;
                var result = await providers.GetLyricsAsync(
                    protocol,
                    providerId,
                    externalId,
                    ProviderLyricsFormat.LineTimed,
                    song.Title,
                    artists,
                    song.Album,
                    song.Duration);
                if (string.IsNullOrWhiteSpace(result?.Content)) continue;
                return new LyricsInfo
                {
                    TrackName = song.Title,
                    ArtistName = string.Join(", ", artists),
                    AlbumName = song.Album,
                    Duration = song.Duration ?? 0,
                    PlainLyrics = result.Format == ProviderLyricsFormat.PlainText ? result.Content : null,
                    SyncedLyrics = result.Format == ProviderLyricsFormat.PlainText ? null : result.Content,
                    Source = result.Source,
                    Revision = result.Revision
                };
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Lyrics source {Provider} failed; trying the next configured source", providerId);
            }
        }

        return null;
    }

    private static string? ResolveExternalId(
        string providerId,
        string resourceKey,
        string? sourceProvider,
        string? sourceExternalId,
        string? spotifyTrackId)
    {
        providerId = providerId.Trim().ToLowerInvariant();
        if (providerId == "spotify") return string.IsNullOrWhiteSpace(spotifyTrackId) ? null : spotifyTrackId;
        if (providerId == "lrclib")
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(resourceKey))).ToLowerInvariant();
        if (providerId.Equals(sourceProvider, StringComparison.OrdinalIgnoreCase)) return sourceExternalId;
        var sourceIsApple = sourceProvider is not null &&
                            (sourceProvider.Equals("applemusic", StringComparison.OrdinalIgnoreCase) ||
                             sourceProvider.Equals("apple-download", StringComparison.OrdinalIgnoreCase) ||
                             sourceProvider.Equals("spotiflac-apple-music", StringComparison.OrdinalIgnoreCase));
        return sourceIsApple && providerId is "apple-download" or "spotiflac-apple-music"
            ? sourceExternalId
            : null;
    }
}

using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Services.Common;

namespace allstarr.Core.Releases;

public sealed class ReleaseArtworkReader(IProtocolProviderGateway gateway, IMediaAssetResolver media,
    IHttpClientFactory clients, ILogger logger)
{
    private const int MaximumBytes = 8 * 1024 * 1024;

    public Task<ResolvedMediaAsset?> ReadAsync(Guid owner, ProtocolExecutionContext protocol, string providerId, string albumId, CancellationToken token) =>
        media.ResolveAsync(new(owner, null, providerId, "album", albumId, Width: 384),
            cancel => FetchAsync(protocol, providerId, albumId, cancel), MaximumBytes, token);

    private async Task<MediaAssetSource?> FetchAsync(ProtocolExecutionContext protocol, string providerId, string albumId, CancellationToken token)
    {
        try
        {
            // Album details already kept for this listener answer without asking the source again.
            var url = Cover(await gateway.GetStoredAlbumAsync(protocol, providerId, albumId), providerId) ??
                Cover(await gateway.GetAlbumAsync(protocol, providerId, albumId), providerId);
            if (!OutboundRequestGuard.TryCreateSafeHttpUri(url, out var uri, out _) || uri == null) return null;
            using var response = await clients.CreateClient().GetAsync(uri, token);
            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (!response.IsSuccessStatusCode || contentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true ||
                response.Content.Headers.ContentLength > MaximumBytes) return null;
            await response.Content.LoadIntoBufferAsync(MaximumBytes, token);
            return new(await response.Content.ReadAsByteArrayAsync(token), contentType);
        }
        catch (Exception exception) when (!token.IsCancellationRequested)
        {
            // A source that is turned off or failing for this listener leaves the placeholder.
            logger.LogDebug(exception, "Release artwork lookup failed for provider {ProviderId}", providerId);
            return null;
        }
    }

    // Albums without art carry the source's logo instead; a release keeps its placeholder rather than show that.
    private static string? Cover(Album? album, string providerId) =>
        album == null || ProviderArtworkFallback.IsFallback(providerId, album.CoverArtUrl, "album") ? null : album.CoverArtUrl;
}

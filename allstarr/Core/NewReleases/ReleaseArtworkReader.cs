using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Services.Common;

namespace allstarr.Core.Releases;

public sealed class ReleaseArtworkReader(IProtocolProviderGateway gateway, IMediaAssetResolver media,
    IHttpClientFactory clients, ILogger logger, IApplicationCache? sourceCache = null)
{
    private const int MaximumBytes = 8 * 1024 * 1024;

    public async Task<ResolvedMediaAsset?> ReadAsync(Guid owner, ProtocolExecutionContext protocol, string providerId, string albumId,
        CancellationToken token, string? releaseId = null, IApplicationCache? cache = null)
    {
        if (releaseId == null)
            return await media.ResolveAsync(new(owner, null, providerId, "album", albumId, Width: 384),
                cancel => FetchAsync(protocol, providerId, albumId, cancel), MaximumBytes, token);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token, protocol.CancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        var scoped = new ProtocolExecutionContext(protocol.Protocol, protocol.BackendInstanceId,
            protocol.VerifiedBackendPrincipalId, protocol.Principal, protocol.CorrelationId,
            protocol.Deadline < deadline ? protocol.Deadline : deadline, budget.Token, protocol.Client)
        {
            Policy = protocol.Policy,
            IsReadOnly = protocol.IsReadOnly,
            UseStoredBackendCredentials = protocol.UseStoredBackendCredentials,
            BackendCredentialReferenceId = protocol.BackendCredentialReferenceId
        };
        try
        {
            budget.Token.ThrowIfCancellationRequested();
            var pending = ReadReleaseAsync(owner, scoped, providerId, albumId, releaseId, budget.Token, cache ?? sourceCache);
            return await pending.WaitAsync(budget.Token);
        }
        catch (OperationCanceledException exception) when (!token.IsCancellationRequested && !protocol.CancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Release artwork lookup timed out.", exception);
        }
    }

    private async Task<ResolvedMediaAsset?> ReadReleaseAsync(Guid owner, ProtocolExecutionContext protocol,
        string providerId, string albumId, string releaseId, CancellationToken token, IApplicationCache? cache)
    {
        try
        {
            var sourceKey = "artwork:image:v1:release-source:" + Hash(string.Join('\0', owner.ToString("N"), providerId, albumId, releaseId));
            var remembered = cache == null ? null : await cache.GetStringAsync(sourceKey).WaitAsync(token);
            var url = Cover(await gateway.GetStoredAlbumAsync(protocol, providerId, albumId).WaitAsync(token), providerId) ?? remembered;
            if (url == null)
            {
                var stored = await media.ResolveAsync(new(owner, null, providerId, "album", albumId, Width: 384),
                    _ => Task.FromResult<MediaAssetSource?>(null), MaximumBytes, token);
                if (stored != null) return stored;
                url = Cover(await gateway.GetAlbumAsync(protocol, providerId, albumId).WaitAsync(token), providerId);
            }
            if (!OutboundRequestGuard.TryCreateSafeHttpUri(url, out var uri, out _) || uri == null) return null;
            if (cache != null && url != remembered)
                await cache.SetStringAsync(sourceKey, url!, ApplicationCachePolicyRegistry.Resolve(ApplicationCacheCategory.Artwork).FreshFor);
            return await media.ResolveAsync(new(owner, null, providerId, "release", releaseId, Hash(url!), Width: 384),
                cancel => FetchAsync(uri, cancel), MaximumBytes, token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException && exception is not TimeoutException && !token.IsCancellationRequested)
        {
            logger.LogDebug(exception, "Release artwork lookup failed for provider {ProviderId}", providerId);
            return null;
        }
    }

    private async Task<MediaAssetSource?> FetchAsync(ProtocolExecutionContext protocol, string providerId, string albumId, CancellationToken token)
    {
        try
        {
            // Album details already kept for this listener answer without asking the source again.
            var url = Cover(await gateway.GetStoredAlbumAsync(protocol, providerId, albumId).WaitAsync(token), providerId) ??
                Cover(await gateway.GetAlbumAsync(protocol, providerId, albumId).WaitAsync(token), providerId);
            if (!OutboundRequestGuard.TryCreateSafeHttpUri(url, out var uri, out _) || uri == null) return null;
            return await FetchAsync(uri, token);
        }
        catch (Exception exception) when (!token.IsCancellationRequested)
        {
            // A source that is turned off or failing for this listener leaves the placeholder.
            logger.LogDebug(exception, "Release artwork lookup failed for provider {ProviderId}", providerId);
            return null;
        }
    }

    private async Task<MediaAssetSource?> FetchAsync(Uri uri, CancellationToken token)
    {
        using var response = await clients.CreateClient().GetAsync(uri, token);
        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (!response.IsSuccessStatusCode || contentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true ||
            response.Content.Headers.ContentLength > MaximumBytes) return null;
        await response.Content.LoadIntoBufferAsync(MaximumBytes, token);
        return new(await response.Content.ReadAsByteArrayAsync(token), contentType);
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    // Albums without art carry the source's logo instead; a release keeps its placeholder rather than show that.
    private static string? Cover(Album? album, string providerId) =>
        album == null || ProviderArtworkFallback.IsFallback(providerId, album.CoverArtUrl, "album") ? null : album.CoverArtUrl;
}

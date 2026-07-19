using allstarr.Core.Identity;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using Microsoft.Extensions.Options;

namespace allstarr.Services.Spotify;

/// <summary>
/// Runs background playlist matching through the same user/account-aware provider
/// gateway used by Jellyfin search. This keeps encrypted shared and personal
/// provider accounts usable without copying credentials back into deployment config.
/// </summary>
public sealed class PlaylistPlayableSearchService(
    IProtocolProviderGateway gateway,
    BackendIdentityResolver identities,
    IdentityOptions identityOptions,
    IOptions<JellyfinSettings> jellyfinSettings,
    ILogger<PlaylistPlayableSearchService> logger)
{
    private readonly SemaphoreSlim _principalLock = new(1, 1);
    private AllstarrPrincipal? _principal;

    public async Task<IReadOnlyList<Song>?> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        var principal = await ResolvePrincipalAsync(cancellationToken);
        if (principal == null)
        {
            return null;
        }

        var context = new ProtocolExecutionContext(
            ProtocolKind.Jellyfin,
            principal.BackendInstanceId,
            principal.BackendPrincipalId,
            principal,
            $"playlist-match-{Guid.NewGuid():N}",
            DateTimeOffset.UtcNow.AddSeconds(30),
            cancellationToken);
        var result = await gateway.SearchAsync(context, query, limit, 1, 1);
        return result.Songs
            .Where(ExternalTrackPlaybackPolicy.CanUseForPlayback)
            .Take(limit)
            .ToList();
    }

    private async Task<AllstarrPrincipal?> ResolvePrincipalAsync(CancellationToken cancellationToken)
    {
        if (_principal != null)
        {
            return _principal;
        }

        var principalId = jellyfinSettings.Value.UserId;
        if (string.IsNullOrWhiteSpace(principalId))
        {
            return null;
        }

        await _principalLock.WaitAsync(cancellationToken);
        try
        {
            if (_principal != null)
            {
                return _principal;
            }

            _principal = await identities.ResolveAsync(new BackendIdentityDescriptor(
                "jellyfin",
                principalId,
                BackendInstanceId: identityOptions.BackendInstanceId), cancellationToken);
            if (_principal == null)
            {
                logger.LogWarning(
                    "Playlist matching could not resolve the Jellyfin user; falling back to deployment-level providers");
            }
            return _principal;
        }
        finally
        {
            _principalLock.Release();
        }
    }
}

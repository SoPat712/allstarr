using allstarr.Core.Capabilities;
using allstarr.Core.Playlists;
using allstarr.Core.Routing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Controllers;

public sealed partial class PlaylistLinksController
{
    private bool CanDiscoverPlaylists(string providerId) => providerRegistry.TryGet(providerId, out var source) &&
        source!.Capabilities.Any(item => item.Capability == ProviderCapabilityKind.Playlist && item.SupportsPlaylistDiscovery);

    [HttpPost("resolve-url")]
    public Task<IActionResult> ResolveUrl([FromBody] ResolvePlaylistUrlRequest request, CancellationToken cancellationToken) =>
        Execute(async session =>
        {
            if (string.IsNullOrWhiteSpace(request.Url))
                return BadRequest(new { error = "No source can read this link.", reasonCode = "no-source" });
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var accounts = await db.ProviderAccounts.AsNoTracking().Where(item => item.Enabled &&
                    (item.OwnerUserId == null || item.OwnerUserId == session.AllstarrUserId))
                .ToListAsync(cancellationToken);
            var actor = new ProviderActorContext(ProviderActorKind.User, session.AllstarrUserId,
                new ProviderBackendPrincipal(session.BackendType.ToLowerInvariant(), session.BackendInstanceId, session.UserId));
            var resolved = await new PlaylistUrlResolver(providerRegistry, providerRouter, effectivePolicies)
                .ResolveAsync(request.Url, actor, accounts, HttpContext.TraceIdentifier, cancellationToken);
            if (resolved.Resource == null)
                return BadRequest(new { error = resolved.Error, reasonCode = resolved.ErrorCode });
            if (resolved.Resource.ResourceKind == ProviderResourceKind.Album)
                return BadRequest(new { error = "Albums are not supported yet.", reasonCode = "album-not-supported" });
            if (resolved.Resource.ResourceKind != ProviderResourceKind.Playlist)
                return BadRequest(new { error = "Use a playlist or album link.", reasonCode = "playlist-required" });
            var descriptor = resolved.Source!.Capabilities.FirstOrDefault(item =>
                item.Capability == ProviderCapabilityKind.Playlist && item.HasUsableImplementation);
            if (descriptor == null)
                return BadRequest(new { error = "No source can read this link.", reasonCode = "playlist-read-unavailable" });
            var previewRequest = new ProviderRouteRequest(ProviderCapabilityKind.Playlist, actor,
                resolved.Context!.Policy, "playlist-url-preview", HttpContext.TraceIdentifier, clock.UtcNow.AddMinutes(2),
                [resolved.Source.Id], resolved.ProviderAccountId is { } accountId
                    ? [new(resolved.Source.Id, requestedAccountId: accountId)] : [],
                cancellationToken: cancellationToken, purpose: ProviderRoutePurpose.ManualSelection);
            var candidate = (await providerRouter.PlanAsync<IProviderPlaylistCapability>(previewRequest)).Candidates.FirstOrDefault();
            if (candidate == null)
                return Conflict(new { error = $"{resolved.Source.DisplayName} cannot currently read this link.", reasonCode = "source-unavailable" });
            var previewContext = candidate.Context;
            if (previewContext.Account == null && descriptor.SupportsPublicRead)
                previewContext = new(new ProviderActorContext(ProviderActorKind.PublicRead, null), resolved.Source.Id, null,
                    previewContext.Policy, previewContext.OperationId, previewContext.CorrelationId, previewContext.Deadline, cancellationToken);
            var outcome = await candidate.Implementation.GetPlaylistTracksAsync(previewContext,
                new(resolved.Resource, new ProviderPageRequest(1)));
            if (!outcome.IsSuccess)
                return DiscoveryFailure(outcome.Error!, resolved.Source.Id, resolved.ProviderAccountId);
            var playlist = outcome.RequireValue().Playlist;
            Response.Headers.CacheControl = "no-store";
            return Ok(new
            {
                source = new { id = resolved.Source.Id, displayName = resolved.Source.DisplayName },
                kind = "playlist",
                id = resolved.Resource.Value,
                providerAccountId = candidate.Context.Account?.AccountId,
                title = playlist.Name,
                owner = playlist.Owner.DisplayName ?? playlist.Owner.ProviderUserId,
                trackCount = playlist.TrackCount,
                artworkUrl = playlist.Artwork == null ? null : SourceArtworkUrl(candidate.Context.Account?.AccountId,
                    resolved.Source.Id, resolved.Resource.Value) + $"?revision={Uri.EscapeDataString(playlist.SourceRevision)}"
            });
        });
}

public sealed record ResolvePlaylistUrlRequest(string Url);

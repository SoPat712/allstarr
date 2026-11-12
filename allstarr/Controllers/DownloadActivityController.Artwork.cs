using allstarr.Core.Artwork;
using allstarr.Core.Playback;
using allstarr.Core.Downloads;
using allstarr.Core.Capabilities;
using allstarr.Services.Admin;
using allstarr.Core.Protocols;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Controllers;

public partial class DownloadActivityController
{
    private async Task<MediaAssetSource?> ReadDownloadArtworkAsync(AdminAuthSession session, string itemId, CancellationToken token)
    {
        var identity = ExternalPlaybackMetadataResolver.ParseTrackIdentity(itemId);
        if (identity == null || session.AllstarrUserId is not { } owner) return null;
        ProtocolExecutionContext? protocol;
        try
        {
            protocol = _protocolContexts != null
                ? await _protocolContexts.CreateAsync(session, HttpContext.TraceIdentifier, token)
                : (await _libraryAccess.ResolveUserAsync(owner, token)).Context;
        }
        catch (UnauthorizedAccessException) { return null; }
        if (protocol == null) return null;
        var paths = new List<string>();
        if (_keptFiles != null && protocol.Actor is { } actor)
        {
            var kept = await _keptFiles.FindBySourceAsync(actor,
                new(identity.Value.Provider, ProviderResourceKind.Track, identity.Value.ExternalId), token);
            if (kept != null) paths.Add(kept.Path);
        }
        if (_contextFactory != null)
        {
            await using var db = await _contextFactory.CreateDbContextAsync(token);
            paths.AddRange(await db.DownloadedSongMappings.AsNoTracking()
                .Where(item => item.ProviderId == identity.Value.Provider && item.ExternalId == identity.Value.ExternalId &&
                    (item.ProviderAccountId == null || db.ProviderAccounts.Any(account => account.Id == item.ProviderAccountId &&
                        (account.OwnerUserId == null || account.OwnerUserId == owner))))
                .OrderByDescending(item => item.DownloadedAt)
                .Select(item => item.LocalPath).ToArrayAsync(token));
        }
        if (_providerGateway == null || _httpClients == null) return null;
        return await new DownloadArtworkReader(_providerGateway, _mediaAssets, _httpClients, _logger, _metadataResolvers)
            .ReadAsync(owner, protocol, identity.Value.Provider, identity.Value.ExternalId, paths, token);
    }
}

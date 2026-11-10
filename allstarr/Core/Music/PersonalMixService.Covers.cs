using System.Text.Json;
using allstarr.Core.Playlists;
using allstarr.Core.Capabilities;
using allstarr.Services.Common;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Music;

public sealed partial class PersonalMixService
{
    private void WarmCovers(Guid owner, IReadOnlyList<PersonalMix> changed) => _ = Task.Run(async () =>
    {
        try
        {
            var document = new PersonalMixDocument { Mixes = changed.ToDictionary(mix => mix.Id.ToString()) };
            var visible = await VisibleAsync(owner, document, CancellationToken.None);
            var inputs = new List<MixCoverInput>();
            foreach (var mix in visible)
                inputs.Add(MixCoverInput.From(mix.Kind == "external" ? await GetCoverAsync(owner, mix.Id) : mix));
            await coverImages!.WarmAsync(inputs, owner);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger?.LogDebug(exception, "Mix cover warm-up deferred");
        }
    });

    public async Task<PersonalMix> GetCoverAsync(Guid owner, Guid id, CancellationToken token = default)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var user = await UserAsync(db, owner, token);
        var row = await RowAsync(db, user, token);
        var document = row == null ? new PersonalMixDocument { AppsOptInVersion = 3 }
            : JsonSerializer.Deserialize<PersonalMixDocument>(row.PayloadJson)!;
        document = await LoadAsync(db, user, token, publishedOnly: true, saved: row, document: document);
        var selected = document.Mixes.SingleOrDefault(item => item.Value.Id == id);
        if (selected.Key != null)
            return (await VisibleAsync(owner, new() { Mixes = new() { [selected.Key] = selected.Value } }, token)).Single();
        return (await ServiceMixesAsync(user, token, id, coverOnly: true)).SingleOrDefault()
            ?? throw new KeyNotFoundException();
    }

    private static string ServiceArtworkUrl(Guid? account, string provider, string playlist, string revision) =>
        (account.HasValue ? $"/api/admin/playlist-sources/{account}/playlists/{Uri.EscapeDataString(playlist)}/artwork"
            : $"/api/admin/playlist-sources/public/{Uri.EscapeDataString(provider)}/playlists/{Uri.EscapeDataString(playlist)}/artwork") +
        $"?revision={Uri.EscapeDataString(revision)}";

    internal async Task<ResolvedMediaAsset?> LoadServiceArtworkAsync(MixCoverInput input, Guid owner,
        IMediaAssetResolver assets, CancellationToken token)
    {
        if (registry == null || input.Kind != "external" || input.ServiceArtwork == null || input.SourcePlaylistId.Length == 0) return null;
        await using var db = await factory.CreateDbContextAsync(token);
        var user = await UserAsync(db, owner, token);
        var context = await ContextAsync(user, token);
        if (context.Policy?.IsEnabled(input.Source) == false ||
            !registry.TryGetCapability<IProviderPlaylistCapability>(input.Source, ProviderCapabilityKind.Playlist, out var capability)) return null;
        var account = await db.ProviderAccounts.AsNoTracking().Where(item => item.OwnerUserId == owner && item.Enabled &&
                item.ProviderId == input.Source && (input.ProviderAccountId == null || item.Id == input.ProviderAccountId))
            .OrderBy(item => item.CreatedAt).ThenBy(item => item.Id).FirstOrDefaultAsync(token);
        if (account == null) return null;
        var query = input.ServiceArtwork.IndexOf('?');
        var revision = query < 0 ? null : QueryHelpers.ParseQuery(input.ServiceArtwork[query..])["revision"].ToString();
        var reference = new ProviderArtworkReference(new(input.Source, ProviderResourceKind.Playlist, input.SourcePlaylistId), revision: revision);
        return await assets.ResolveAsync(new(owner, account.Id, input.Source, "playlist", input.SourcePlaylistId, revision, Width: 512),
            async cancellation =>
            {
                var outcome = await capability!.ResolveArtworkAsync(FeedContext(context, account, cancellation), new(reference, maximumBytes: 4 * 1024 * 1024));
                return outcome.IsSuccess ? new MediaAssetSource(outcome.RequireValue().Bytes, outcome.RequireValue().ContentType) : null;
            }, 4 * 1024 * 1024, token);
    }

    // Image loaders may omit their player token. Resolve only already published artwork
    // from its stored owner; never load/migrate a listener document on this public path.
    public async Task<MixCoverInput?> PublishedCoverAsync(Guid id, CancellationToken token = default)
    {
        if (!(bool)(await settings.GetAsync(PublishKey, token)).Value) return null;
        await using var db = await factory.CreateDbContextAsync(token);
        var link = await db.PlaylistLinks.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, token);
        if (link != null)
        {
            if (!link.Enabled || link.TargetProtocol != "jellyfin" || !IsExternalLink(link) ||
                !await db.Users.AsNoTracking().AnyAsync(user => user.Id == link.OwnerUserId && user.Enabled && !user.IsSynthetic &&
                    user.BackendType == link.TargetProtocol && user.BackendInstanceId == link.TargetBackendInstanceId, token) ||
                !await db.ProviderAccounts.AsNoTracking().AnyAsync(account =>
                    account.Id == link.ProviderAccountId && account.OwnerUserId == link.OwnerUserId &&
                    account.ProviderId == link.SourceProviderId && account.Enabled, token)) return null;
            var snapshot = await db.PlaylistSourceSnapshots.AsNoTracking().Where(item =>
                    item.PlaylistLinkId == id && item.OwnerUserId == link.OwnerUserId && item.PublishedAt.HasValue)
                .OrderByDescending(item => item.SnapshotVersion).FirstOrDefaultAsync(token);
            if (snapshot == null) return null;
            var count = await db.PlaylistSourceEntries.CountAsync(item => item.PlaylistSourceSnapshotId == snapshot.Id, token);
            return new(link.Id.ToString(), snapshot.SnapshotVersion, "external", Source: link.SourceProviderId,
                SourcePlaylistId: link.SourcePlaylistId, Name: PlaylistVirtualizationService.DisplayName(link.SourceProviderId, snapshot.Name),
                Count: count);
        }

        // Personal mixes use IDs inside each owner's versioned document, rather than
        // playlist_links. Select the latest document before checking Added or the ID.
        var rows = await db.ExternalMetadataSnapshots.AsNoTracking().Where(row =>
                row.ProviderId == "allstarr" && row.ResourceKind == ResourceKind && row.Protocol == "jellyfin" &&
                db.Users.Any(user => user.Id == row.OwnerUserId && user.Enabled && !user.IsSynthetic &&
                    user.BackendType == row.Protocol && user.BackendInstanceId == row.BackendInstanceId &&
                    user.BackendPrincipalId == row.BackendPrincipalId))
            .GroupBy(row => row.OwnerUserId).Select(group => group.OrderByDescending(row => row.SnapshotVersion).First())
            .ToArrayAsync(token);
        foreach (var row in rows)
        {
            var document = JsonSerializer.Deserialize<PersonalMixDocument>(row.PayloadJson);
            var mix = document?.Mixes.Values.SingleOrDefault(item => item.Id == id);
            if (mix is { Added: true, Generation: > 0 })
                return MixCoverInput.From(mix);
        }
        return null;
    }
}

using allstarr.Core.Playlists;
using allstarr.Core.Storage;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace allstarr.Core.Protocols.Jellyfin;

public sealed record JellyfinPlaylistMutationRoute(bool Writable, string? TargetPlaylistId);

public interface IJellyfinPlaylistMutationResolver
{
    Task<JellyfinPlaylistMutationRoute?> ResolveAsync(
        ProtocolExecutionContext context,
        string protocolId,
        CancellationToken cancellationToken = default);
}

public sealed class JellyfinPlaylistMutationResolver(
    IDbContextFactory<AllstarrDbContext> contextFactory) : IJellyfinPlaylistMutationResolver
{
    public async Task<JellyfinPlaylistMutationRoute?> ResolveAsync(
        ProtocolExecutionContext context,
        string protocolId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Protocol != ProtocolKind.Jellyfin ||
            !PlaylistVirtualizationService.TryParseProtocolId(protocolId, out var linkId) ||
            context.Actor?.EffectiveUserId is not { } userId)
        {
            return null;
        }

        var actor = context.Actor;
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var link = await db.PlaylistLinks.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == linkId &&
            item.TenantId == actor.TenantId &&
            item.OwnerUserId == userId &&
            item.TargetBackendInstanceId == context.BackendInstanceId &&
            item.TargetProtocol == "jellyfin" &&
            item.Enabled &&
            (context.LibraryScopeId == null || item.LibraryScopeId == context.LibraryScopeId),
            cancellationToken);
        if (link == null) return null;

        var writable = link.Mode != PlaylistLinkMode.Virtual &&
                       !string.IsNullOrWhiteSpace(link.TargetPlaylistId);
        return new JellyfinPlaylistMutationRoute(
            writable,
            writable ? link.TargetPlaylistId!.Trim() : null);
    }
}

public sealed class JellyfinVirtualPlaylistProtocolAdapter(
    IPlaylistVirtualizationService playlists,
    IJellyfinPlaylistMutationResolver mutationResolver,
    IOptions<JellyfinSettings>? settings = null)
{
    private readonly string serverId = string.IsNullOrWhiteSpace(settings?.Value.DeviceId)
        ? "allstarrrr-proxy"
        : settings.Value.DeviceId;

    public bool IsVirtualPlaylistId(string? value) =>
        PlaylistVirtualizationService.TryParseProtocolId(value, out _);

    public Task<JellyfinPlaylistMutationRoute?> ResolveMutationAsync(
        ProtocolExecutionContext context,
        string id,
        CancellationToken cancellationToken) =>
        mutationResolver.ResolveAsync(context, id, cancellationToken);

    public async Task<IReadOnlyList<Dictionary<string, object?>>> ListItemsAsync(
        ProtocolExecutionContext context,
        CancellationToken cancellationToken)
    {
        var visible = await playlists.ListAsync(context, cancellationToken);
        return visible.Select(ToItem).ToArray();
    }

    public async Task<IActionResult?> ReadItemAsync(
        ProtocolExecutionContext context, string id, CancellationToken cancellationToken)
    {
        var playlist = await playlists.ReadAsync(context, id, cancellationToken);
        if (playlist == null) return null;
        return new JsonResult(ToItem(playlist));
    }

    public async Task<IActionResult?> ReadDefinitionAsync(
        ProtocolExecutionContext context, string id, CancellationToken cancellationToken)
    {
        var playlist = await playlists.ReadAsync(context, id, cancellationToken);
        return playlist == null
            ? null
            : new JsonResult(new
            {
                OpenAccess = false,
                Shares = Array.Empty<object>(),
                ItemIds = playlist.Tracks.Select(track => track.BackendItemId).ToArray()
            });
    }

    public async Task<string?> GetImageSourceIdAsync(
        ProtocolExecutionContext? context, string id, CancellationToken cancellationToken)
    {
        if (context != null)
        {
            var playlist = await playlists.ReadAsync(context, id, cancellationToken);
            return playlist?.ArtworkReferenceKey == null
                ? null
                : PlaylistIdHelper.CreatePlaylistId(playlist.SourceProviderId, playlist.SourcePlaylistId);
        }

        var source = await playlists.ResolvePublicArtworkSourceAsync(id, cancellationToken);
        return source == null
            ? null
            : PlaylistIdHelper.CreatePlaylistId(source.ProviderId, source.PlaylistId);
    }

    private Dictionary<string, object?> ToItem(VirtualPlaylistReadModel playlist) =>
        new()
        {
            ["Name"] = playlist.Name,
            ["Overview"] = playlist.Description,
            ["ServerId"] = serverId,
            ["Id"] = playlist.ProtocolId,
            ["IsFolder"] = true,
            ["Type"] = "Playlist",
            ["MediaType"] = "Audio",
            ["ChildCount"] = playlist.Tracks.Count,
            ["RunTimeTicks"] = playlist.Tracks.Sum(track => track.DurationMilliseconds) * TimeSpan.TicksPerMillisecond,
            ["UserData"] = UserData(playlist.ProtocolId),
            ["ImageTags"] = playlist.ArtworkReferenceKey == null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string> { ["Primary"] = playlist.ArtworkReferenceKey },
            ["ProviderIds"] = new Dictionary<string, string>
            {
                [playlist.SourceProviderId] = playlist.SourceRevision
            }
        };

    public async Task<IActionResult?> ReadItemsAsync(
        ProtocolExecutionContext context, string id, CancellationToken cancellationToken)
    {
        var playlist = await playlists.ReadAsync(context, id, cancellationToken);
        if (playlist == null) return null;
        var items = playlist.Tracks.Select(track => new Dictionary<string, object?>
        {
            ["Name"] = track.Title,
            ["ServerId"] = serverId,
            ["Id"] = track.BackendItemId,
            ["PlaylistItemId"] = $"{playlist.ProtocolId}-{track.SourcePosition}",
            ["ParentId"] = playlist.ProtocolId,
            ["AlbumId"] = playlist.ProtocolId,
            ["Album"] = track.Album,
            ["AlbumArtist"] = track.AlbumArtist,
            ["Artists"] = new[] { track.Artist },
            ["RunTimeTicks"] = track.DurationMilliseconds * TimeSpan.TicksPerMillisecond,
            ["IndexNumber"] = track.SourcePosition + 1,
            ["IsFolder"] = false,
            ["Type"] = "Audio",
            ["MediaType"] = "Audio",
            ["LocationType"] = "FileSystem",
            ["UserData"] = UserData(track.BackendItemId),
            ["ImageTags"] = track.CoverArtReference == null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string> { ["Primary"] = track.CoverArtReference }
        }).ToList();
        return new JsonResult(new { Items = items, TotalRecordCount = items.Count, StartIndex = 0 });
    }

    private static Dictionary<string, object> UserData(string id) => new()
    {
        ["ItemId"] = id,
        ["Key"] = id,
        ["PlaybackPositionTicks"] = 0L,
        ["PlayCount"] = 0,
        ["IsFavorite"] = false,
        ["Played"] = false
    };
}

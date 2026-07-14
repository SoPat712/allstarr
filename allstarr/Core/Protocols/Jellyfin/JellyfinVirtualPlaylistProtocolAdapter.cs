using allstarr.Core.Playlists;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Core.Protocols.Jellyfin;

public sealed class JellyfinVirtualPlaylistProtocolAdapter(IPlaylistVirtualizationService playlists)
{
    public bool IsVirtualPlaylistId(string? value) =>
        PlaylistVirtualizationService.TryParseProtocolId(value, out _);

    public async Task<IActionResult?> ReadItemAsync(
        ProtocolExecutionContext context, string id, CancellationToken cancellationToken)
    {
        var playlist = await playlists.ReadAsync(context, id, cancellationToken);
        if (playlist == null) return null;
        return new JsonResult(new Dictionary<string, object?>
        {
            ["Name"] = playlist.Name,
            ["Overview"] = playlist.Description,
            ["ServerId"] = "allstarr",
            ["Id"] = playlist.ProtocolId,
            ["IsFolder"] = true,
            ["Type"] = "Playlist",
            ["MediaType"] = "Audio",
            ["ChildCount"] = playlist.Tracks.Count,
            ["RunTimeTicks"] = playlist.Tracks.Sum(track => track.DurationMilliseconds) * TimeSpan.TicksPerMillisecond,
            ["ImageTags"] = playlist.ArtworkReferenceKey == null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string> { ["Primary"] = playlist.ArtworkReferenceKey },
            ["ProviderIds"] = new Dictionary<string, string>
            {
                [playlist.SourceProviderId] = playlist.SourceRevision
            }
        });
    }

    public async Task<IActionResult?> ReadItemsAsync(
        ProtocolExecutionContext context, string id, CancellationToken cancellationToken)
    {
        var playlist = await playlists.ReadAsync(context, id, cancellationToken);
        if (playlist == null) return null;
        var items = playlist.Tracks.Select(track => new Dictionary<string, object?>
        {
            ["Name"] = track.Title,
            ["ServerId"] = "allstarr",
            ["Id"] = track.BackendItemId,
            ["PlaylistItemId"] = $"{playlist.ProtocolId}-{track.SourcePosition}",
            ["ParentId"] = playlist.ProtocolId,
            ["AlbumId"] = playlist.ProtocolId,
            ["Album"] = track.Album,
            ["AlbumArtist"] = track.AlbumArtist,
            ["Artists"] = new[] { track.Artist },
            ["ArtistItems"] = new[] { new Dictionary<string, object?> { ["Name"] = track.Artist } },
            ["RunTimeTicks"] = track.DurationMilliseconds * TimeSpan.TicksPerMillisecond,
            ["IndexNumber"] = track.SourcePosition + 1,
            ["IsFolder"] = false,
            ["Type"] = "Audio",
            ["MediaType"] = "Audio",
            ["LocationType"] = "FileSystem",
            ["ImageTags"] = track.CoverArtReference == null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string> { ["Primary"] = track.CoverArtReference }
        }).ToList();
        return new JsonResult(new { Items = items, TotalRecordCount = items.Count, StartIndex = 0 });
    }
}

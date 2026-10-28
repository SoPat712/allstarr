using System.Globalization;
using System.Text.Json.Nodes;
using allstarr.Core.Matching;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

public partial class JellyfinController
{
    private async Task<IActionResult?> TryEditPlaylistForkAsync(string path)
    {
        if (HttpMethods.IsGet(Request.Method) || HttpMethods.IsHead(Request.Method)) return null;
        var segments = path.Trim('/').Split('/');
        if (segments.Length < 2 || !(segments[0].Equals("Playlists", StringComparison.OrdinalIgnoreCase) ||
                segments[0].Equals("Items", StringComparison.OrdinalIgnoreCase))) return null;
        var protocol = HttpContext.RequireProtocolExecutionContext();
        var token = HttpContext.RequestAborted;
        using var lease = await WritablePlaylists.LockAsync(token);
        var fork = await WritablePlaylists.ReadForkAsync(protocol, segments[1], token);
        var link = fork == null ? await WritablePlaylists.FindImportedPlaylistAsync(protocol, segments[1], token) : null;
        if (fork == null && link == null) return null;
        if (protocol.Actor == null || protocol.IsReadOnly) return StatusCode(StatusCodes.Status403Forbidden);
        var items = segments.Length == 3 && segments[2].Equals("Items", StringComparison.OrdinalIgnoreCase);
        var move = segments.Length == 6 && segments[2].Equals("Items", StringComparison.OrdinalIgnoreCase) &&
                   segments[4].Equals("Move", StringComparison.OrdinalIgnoreCase) && HttpMethods.IsPost(Request.Method);
        var replace = segments.Length == 2 && segments[0].Equals("Playlists", StringComparison.OrdinalIgnoreCase) && HttpMethods.IsPost(Request.Method);
        if (!(items && (HttpMethods.IsPost(Request.Method) || HttpMethods.IsDelete(Request.Method))) && !move && !replace)
            return Conflict(new { error = "Imported playlist metadata and sharing are read-only." });
        JsonObject? dto = null;
        if (replace)
        {
            Request.EnableBuffering();
            using var reader = new StreamReader(Request.Body, leaveOpen: true);
            var body = await reader.ReadToEndAsync(token);
            Request.Body.Position = 0;
            try { dto = string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body) as JsonObject; }
            catch (System.Text.Json.JsonException) { return BadRequest(); }
            if (dto?["Ids"] is not JsonArray) return Conflict(new { error = "Imported playlist metadata is read-only." });
        }
        var original = fork?.Playlist ?? await _virtualPlaylistProtocolAdapter.ReadOriginalAsync(protocol,
            PlaylistVirtualizationService.CreateProtocolId(link!.Id), token);
        if (original == null) return NotFound();
        var current = original.Tracks.ToList();
        try
        {
            if (move)
            {
                var index = current.FindIndex(track => ForkOccurrence(original, track) == segments[3]);
                if (index < 0 || !int.TryParse(segments[5], NumberStyles.None, CultureInfo.InvariantCulture, out var destination) ||
                    destination < 0 || destination >= current.Count) return BadRequest();
                var track = current[index];
                current.RemoveAt(index);
                current.Insert(destination, track);
            }
            else if (items && HttpMethods.IsDelete(Request.Method))
            {
                var entries = Request.Query["EntryIds"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
                if (entries.Count == 0 || current.Count(track => entries.Contains(ForkOccurrence(original, track))) != entries.Count)
                    return BadRequest(new { error = "A playlist occurrence is unavailable." });
                current.RemoveAll(track => entries.Contains(ForkOccurrence(original, track)));
            }
            else
            {
                var ids = replace ? dto!["Ids"]!.AsArray().Select(item => item?.GetValue<string>() ?? throw new ArgumentException("Invalid song identifier.")).ToArray() :
                    Request.Query["Ids"].SelectMany(value => (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)).ToArray();
                if (ids.Any(string.IsNullOrWhiteSpace)) return BadRequest();
                if (!replace && ids.Length == 0) return NoContent();
                if (fork == null && replace && ids.SequenceEqual(current.Select(track => track.BackendItemId)))
                    return Conflict(new { error = "Imported playlist metadata is read-only." });
                var added = await WritablePlaylists.ResolveAsync(protocol, ids, token);
                var nativeIds = added.Where(member => !member.IsProvider).Select(member => member.ItemId).Distinct().ToArray();
                if (nativeIds.Length > 0)
                {
                    var query = new Dictionary<string, string>
                    {
                        ["Ids"] = string.Join(',', nativeIds),
                        ["UserId"] = protocol.VerifiedBackendPrincipalId,
                        ["Limit"] = nativeIds.Length.ToString(CultureInfo.InvariantCulture)
                    };
                    foreach (var name in new[] { "api_key", "access_token", "ApiKey" })
                        if (Request.Query.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)) query[name] = value.ToString();
                    var (native, status) = await _proxyService.GetJsonAsync("Items", query, Request.Headers);
                    using (native)
                    {
                        if (status is < 200 or >= 300) return StatusCode(status);
                        if (native == null || !native.RootElement.TryGetProperty("Items", out var rows) ||
                            nativeIds.Any(id => !rows.EnumerateArray().Any(row => row.GetProperty("Id").GetString() == id &&
                                row.TryGetProperty("Type", out var type) && type.GetString() == "Audio")))
                            return BadRequest(new { error = "A playlist song is unavailable." });
                    }
                }
                var remaining = replace ? current.ToList() : [];
                if (replace) current.Clear();
                foreach (var member in added)
                {
                    var index = remaining.FindIndex(track => track.BackendItemId == member.ItemId);
                    if (index >= 0) { current.Add(remaining[index]); remaining.RemoveAt(index); continue; }
                    var song = member.Song;
                    current.Add(new(current.Count, member.ItemId, song?.Title ?? member.ItemId, song?.Artist ?? "",
                        song?.Album, song?.AlbumArtist, song?.Duration * 1000L, null, TrackMatchState.Accepted,
                        RouteKind: song == null ? TrackRouteKind.Local : TrackRouteKind.External,
                        RouteProviderId: song?.ExternalProvider, RouteExternalId: song?.ExternalId,
                        SourceMetadata: song == null ? null : new(IsExplicit: song.ExplicitContentLyrics is { } explicitContent ? explicitContent != 0 : null),
                        NativePlaylistEntryId: member.OccurrenceId));
                }
            }
        }
        catch (ArgumentException exception) { return BadRequest(new { error = exception.Message }); }
        catch (InvalidOperationException) { return BadRequest(); }
        var tracks = current.Select((track, index) => track with
        {
            SourcePosition = index,
            NativePlaylistEntryId = ForkOccurrence(original, track),
            NativeEntryJson = null
        }).ToArray();
        fork ??= new(original.ProtocolId, original.TargetPlaylistId, protocol.BackendInstanceId, DateTimeOffset.UtcNow,
            original with
            {
                ProtocolId = PlaylistVirtualizationService.CreateProtocolId(Guid.CreateVersion7()),
                Mode = PlaylistLinkMode.Virtual,
                TargetPlaylistId = null,
                BackendOwner = protocol.VerifiedBackendPrincipalId,
                BackendPublic = false
            });
        await WritablePlaylists.SaveForkAsync(protocol, fork with { Playlist = fork.Playlist with { Tracks = tracks } }, token);
        return NoContent();
    }

    private static string ForkOccurrence(VirtualPlaylistReadModel playlist, VirtualPlaylistTrack track) =>
        track.NativePlaylistEntryId ?? (track.SourceEntryId is { } id ? $"allstarr-entry-{id:N}" :
            $"{playlist.ProtocolId}-entry-{track.SourcePosition}");
}

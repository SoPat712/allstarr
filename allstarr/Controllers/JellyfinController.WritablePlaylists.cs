using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Services.Jellyfin;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace allstarr.Controllers;

public partial class JellyfinController
{
    private WritablePlaylistMembership WritablePlaylists => HttpContext.RequestServices.GetRequiredService<WritablePlaylistMembership>();

    [HttpGet("Playlists/{playlistId}/Users", Order = 1)]
    [HttpGet("Playlists/{playlistId}/Users/{userId}", Order = 1)]
    public async Task<IActionResult> GetPlaylistPermissions(string playlistId, string? userId = null)
    {
        var protocol = HttpContext.RequireProtocolExecutionContext();
        var fork = await WritablePlaylists.ReadForkAsync(protocol, playlistId, HttpContext.RequestAborted);
        var imported = fork == null ? await WritablePlaylists.FindImportedPlaylistAsync(protocol, playlistId, HttpContext.RequestAborted) : null;
        if (fork != null || imported != null)
        {
            if (fork == null && await _virtualPlaylistProtocolAdapter.ReadOriginalAsync(protocol,
                    PlaylistVirtualizationService.CreateProtocolId(imported!.Id), HttpContext.RequestAborted) == null) return NotFound();
            var permission = new { UserId = protocol.VerifiedBackendPrincipalId, CanEdit = true };
            return userId == null ? new JsonResult(new[] { permission }) :
                new JsonResult(new { UserId = userId, CanEdit = userId == protocol.VerifiedBackendPrincipalId });
        }
        if (_virtualPlaylistProtocolAdapter.IsVirtualPlaylistId(playlistId))
        {
            var target = await _virtualPlaylistProtocolAdapter.GetTargetPlaylistIdAsync(protocol, playlistId, HttpContext.RequestAborted);
            if (target != null) playlistId = target;
            else
            {
                var definition = await _virtualPlaylistProtocolAdapter.ReadDefinitionAsync(protocol, playlistId, HttpContext.RequestAborted);
                if (definition == null) return NotFound();
                return userId == null ? new JsonResult(Array.Empty<object>()) : new JsonResult(new { UserId = userId, CanEdit = false });
            }
        }
        if (allstarr.Services.Common.PlaylistIdHelper.IsExternalPlaylist(playlistId))
            return userId == null ? new JsonResult(Array.Empty<object>()) : new JsonResult(new { UserId = userId, CanEdit = false });
        var path = $"Playlists/{Uri.EscapeDataString(playlistId)}/Users" + (userId == null ? "" : $"/{Uri.EscapeDataString(userId)}");
        var (body, status) = await _proxyService.GetJsonAsync(path + Request.QueryString.Value, null, Request.Headers);
        return HandleProxyResponse(body, status);
    }

    private async Task<IActionResult?> TryDeleteWritablePlaylistAsync(string path)
    {
        var segments = path.Trim('/').Split('/');
        if (!HttpMethods.IsDelete(Request.Method) || segments.Length != 2 ||
            !segments[0].Equals("Items", StringComparison.OrdinalIgnoreCase)) return null;
        var id = segments[1];
        var protocol = HttpContext.RequireProtocolExecutionContext();
        if (JellyfinMusicEndpointPolicy.IsSynthesizedPlaylistId(id))
        {
            if (!_virtualPlaylistProtocolAdapter.IsVirtualPlaylistId(id)) return Conflict(new { error = "Playlist is read-only." });
            var route = await _virtualPlaylistProtocolAdapter.ResolveMutationAsync(protocol, id, HttpContext.RequestAborted);
            if (route == null) return NotFound();
            if (!route.Writable || string.IsNullOrWhiteSpace(route.TargetPlaylistId)) return Conflict(new { error = "Playlist is read-only." });
            id = route.TargetPlaylistId;
        }
        else if (await WritablePlaylists.ReadAsync(protocol, id, HttpContext.RequestAborted) == null) return null;
        using var lease = await WritablePlaylists.LockAsync(HttpContext.RequestAborted);
        var (response, status) = await _proxyService.SendAsync(HttpMethod.Delete,
            $"Items/{Uri.EscapeDataString(id)}" + Request.QueryString.Value, null, Request.Headers);
        using (response)
        {
            if (status is >= 200 and < 300 && protocol.Actor != null)
            {
                if (protocol.Principal?.IsSynthetic == true)
                    await WritablePlaylists.ForgetSyntheticDeletedPlaylistAsync(protocol, id, HttpContext.RequestAborted);
                else await WritablePlaylists.SaveAsync(protocol, id, [], HttpContext.RequestAborted);
            }
            return status == StatusCodes.Status204NoContent ? NoContent() : HandleProxyResponse(response, status);
        }
    }

    private async Task<IActionResult?> ReadWritablePlaylistDefinitionAsync(string playlistId)
    {
        var protocol = HttpContext.RequireProtocolExecutionContext();
        if (await WritablePlaylists.ReadForkAsync(protocol, playlistId, HttpContext.RequestAborted) != null) return null;
        var id = _virtualPlaylistProtocolAdapter.IsVirtualPlaylistId(playlistId)
            ? await _virtualPlaylistProtocolAdapter.GetTargetPlaylistIdAsync(protocol, playlistId, HttpContext.RequestAborted)
            : playlistId;
        if (id == null) return null;
        var saved = await WritablePlaylists.ReadAsync(protocol, id, HttpContext.RequestAborted);
        if (saved == null) return null;
        var handover = await HandoverWritablePlaylistAsync(id, saved);
        if (handover.Error is { } error) return StatusCode(error);
        saved = handover.Saved;
        var (definition, status) = await _proxyService.GetJsonAsync($"Playlists/{Uri.EscapeDataString(id)}" + Request.QueryString.Value, null, Request.Headers);
        using (definition)
        {
            if (definition == null || status is < 200 or >= 300) return StatusCode(status);
            var native = await ReadWritableNativeItemsAsync(id);
            if (native.Envelope == null) return StatusCode(native.Status);
            var members = WritablePlaylistMembership.Reconcile(saved, NativeMembers(native.Envelope));
            var result = JsonNode.Parse(definition.RootElement.GetRawText())!.AsObject();
            result["ItemIds"] = new JsonArray(members.Select(member => (JsonNode?)JsonValue.Create(member.ItemId)).ToArray());
            return Content(result.ToJsonString(), "application/json");
        }
    }

    private async Task<IActionResult?> TryWritablePlaylistAsync(string path)
    {
        var segments = path.Trim('/').Split('/');
        var isRead = HttpMethods.IsGet(Request.Method) || HttpMethods.IsHead(Request.Method);
        if (!segments[0].Equals("Playlists", StringComparison.OrdinalIgnoreCase)) return null;
        var create = segments.Length == 1 && HttpMethods.IsPost(Request.Method);
        var items = segments.Length == 3 && segments[2].Equals("Items", StringComparison.OrdinalIgnoreCase);
        var replace = segments.Length == 2 && HttpMethods.IsPost(Request.Method);
        var move = segments.Length == 6 && segments[2].Equals("Items", StringComparison.OrdinalIgnoreCase) &&
                   segments[4].Equals("Move", StringComparison.OrdinalIgnoreCase) && HttpMethods.IsPost(Request.Method);
        if (!create && !items && !replace && !move) return null;
        var protocol = HttpContext.RequireProtocolExecutionContext();
        var id = create ? null : segments[1];
        if (isRead && id != null && await _virtualPlaylistProtocolAdapter.ReadForkAsync(protocol, id, HttpContext.RequestAborted) != null) return null;
        var originalId = id;
        if (id != null && JellyfinMusicEndpointPolicy.IsSynthesizedPlaylistId(id))
        {
            if (isRead && _virtualPlaylistProtocolAdapter.IsVirtualPlaylistId(id))
            {
                var target = await _virtualPlaylistProtocolAdapter.GetTargetPlaylistIdAsync(protocol, id, HttpContext.RequestAborted);
                var membership = target == null ? null : await WritablePlaylists.ReadAsync(protocol, target, HttpContext.RequestAborted);
                return membership == null ? null : await ReadWritablePlaylistItemsAsync(target!, membership);
            }
            if (!_virtualPlaylistProtocolAdapter.IsVirtualPlaylistId(id))
                return isRead ? null : Conflict(new { error = "Playlist is read-only." });
            var route = await _virtualPlaylistProtocolAdapter.ResolveMutationAsync(protocol, id, HttpContext.RequestAborted);
            if (route == null) return NotFound();
            if (!route.Writable || string.IsNullOrWhiteSpace(route.TargetPlaylistId))
                return isRead ? null : Conflict(new { error = "Playlist is read-only." });
            id = route.TargetPlaylistId;
            segments[1] = id;
            path = string.Join('/', segments);
        }
        var saved = id == null ? null : await WritablePlaylists.ReadAsync(protocol, id, HttpContext.RequestAborted);
        if (isRead)
            return saved == null ? null : await ReadWritablePlaylistItemsAsync(id!, saved);
        JsonObject? dto = null;
        if (create || replace)
        {
            Request.EnableBuffering();
            using var reader = new StreamReader(Request.Body, leaveOpen: true);
            var body = await reader.ReadToEndAsync(HttpContext.RequestAborted);
            Request.Body.Position = 0;
            if (!string.IsNullOrWhiteSpace(body)) dto = JsonNode.Parse(body) as JsonObject;
        }
        var ids = dto?["Ids"] is JsonArray array
            ? array.Select(item => item!.GetValue<string>()).ToArray()
            : Request.Query["Ids"].SelectMany(value => (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)).ToArray();
        if (saved == null && !WritablePlaylistMembership.HasProviderIds(ids))
            return id != originalId ? await RelayCurrentRequestAsync(path) : null;
        if (protocol.Actor == null) return StatusCode(StatusCodes.Status403Forbidden);
        using var lease = await WritablePlaylists.LockAsync(HttpContext.RequestAborted);
        saved = id == null ? null : await WritablePlaylists.ReadAsync(protocol, id, HttpContext.RequestAborted);
        var token = HttpContext.RequestAborted;
        var current = new List<WritablePlaylistMember>();
        if (id != null)
        {
            // The backend checks the caller's owner/share permissions before any membership is changed.
            var (permission, permissionStatus) = await _proxyService.PostJsonAsync(
                $"Playlists/{Uri.EscapeDataString(id)}/Items?UserId={Uri.EscapeDataString(protocol.VerifiedBackendPrincipalId)}", "{}", Request.Headers);
            permission?.Dispose();
            if (permissionStatus != StatusCodes.Status204NoContent) return StatusCode(permissionStatus);
            var read = await ReadWritableNativeItemsAsync(id);
            if (read.Envelope == null) return StatusCode(read.Status);
            current = WritablePlaylistMembership.Reconcile(saved, NativeMembers(read.Envelope)).ToList();
        }
        IReadOnlyList<WritablePlaylistMember> desired;
        if (create || replace && dto?["Ids"] is JsonArray)
        {
            desired = await WritablePlaylists.ResolveAsync(protocol, ids, token);
            desired = WritablePlaylistMembership.PreserveOccurrences(current, desired);
            dto ??= new JsonObject();
            dto["Ids"] = new JsonArray(desired.Where(member => !member.IsProvider).Select(member => (JsonNode?)JsonValue.Create(member.ItemId)).ToArray());
        }
        else if (items && HttpMethods.IsPost(Request.Method))
        {
            var added = await WritablePlaylists.ResolveAsync(protocol, ids, token);
            desired = current.Concat(added).ToArray();
            var query = Request.Query.ToDictionary(pair => pair.Key, pair => (string?)pair.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            query["Ids"] = string.Join(',', added.Where(member => !member.IsProvider).Select(member => member.ItemId));
            path = QueryHelpers.AddQueryString(path, query);
        }
        else if (items && HttpMethods.IsDelete(Request.Method))
        {
            var entries = Request.Query["EntryIds"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
            var removed = current.Where(member => entries.Contains(member.OccurrenceId)).ToArray();
            if (removed.Length != entries.Count) return BadRequest(new { error = "A playlist occurrence is unavailable." });
            desired = current.Where(member => !entries.Contains(member.OccurrenceId)).ToArray();
            var nativeEntries = removed.Where(member => !member.IsProvider).Select(member => member.NativeEntryId).ToArray();
            if (nativeEntries.Length == 0)
            {
                await WritablePlaylists.SaveAsync(protocol, id!, desired, token);
                return NoContent();
            }
            var query = Request.Query.ToDictionary(pair => pair.Key, pair => (string?)pair.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            query["EntryIds"] = string.Join(',', nativeEntries);
            path = QueryHelpers.AddQueryString(path, query);
        }
        else if (move)
        {
            var index = current.FindIndex(member => member.OccurrenceId == segments[3]);
            if (index < 0 || !int.TryParse(segments[5], NumberStyles.None, CultureInfo.InvariantCulture, out var destination) ||
                destination < 0 || destination >= current.Count) return BadRequest();
            var member = current[index];
            current.RemoveAt(index);
            current.Insert(destination, member);
            desired = current;
            if (member.IsProvider)
            {
                await WritablePlaylists.SaveAsync(protocol, id!, desired, token);
                return NoContent();
            }
            var nativePosition = current.Take(destination).Count(item => !item.IsProvider);
            path = $"Playlists/{Uri.EscapeDataString(id!)}/Items/{Uri.EscapeDataString(member.NativeEntryId!)}/Move/{nativePosition}";
        }
        else
        {
            desired = current;
        }
        if (!path.Contains('?') && Request.QueryString.HasValue)
        {
            var query = Request.Query.Where(pair => dto == null || !pair.Key.Equals("Ids", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(pair => pair.Key, pair => (string?)pair.Value.ToString());
            path = QueryHelpers.AddQueryString(path, query);
        }
        var (response, status) = await _proxyService.SendAsync(new HttpMethod(Request.Method), path, dto?.ToJsonString(), Request.Headers,
            dto == null ? null : "application/json");
        using (response)
        {
            if (status is < 200 or >= 300) return HandleProxyResponse(response, status);
            if (create)
            {
                if (response == null || !response.RootElement.TryGetProperty("Id", out var createdId) || string.IsNullOrWhiteSpace(createdId.GetString()))
                    return StatusCode(StatusCodes.Status502BadGateway);
                id = createdId.GetString();
            }
            var read = await ReadWritableNativeItemsAsync(id!);
            if (read.Envelope == null) return StatusCode(read.Status);
            var stored = WritablePlaylistMembership.Reconcile(new(desired), NativeMembers(read.Envelope));
            await WritablePlaylists.SaveAsync(protocol, id!, stored, token);
            return create ? new ContentResult { StatusCode = status, ContentType = "application/json", Content = response!.RootElement.GetRawText() }
                : status == StatusCodes.Status204NoContent ? NoContent() : HandleProxyResponse(response, status);
        }
    }

    private async Task<(JsonObject? Envelope, int Status, string? Original)> ReadWritableNativeItemsAsync(string id)
    {
        // Fetch full membership before reconciling occurrences and applying the client's query.
        var query = Request.Query.Where(pair => pair.Key.Equals("Fields", StringComparison.OrdinalIgnoreCase) ||
                pair.Key.Equals("EnableUserData", StringComparison.OrdinalIgnoreCase) ||
                pair.Key.Equals("EnableImages", StringComparison.OrdinalIgnoreCase) ||
                pair.Key.Equals("EnableImageTypes", StringComparison.OrdinalIgnoreCase) ||
                pair.Key.Equals("ImageTypeLimit", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(pair => pair.Key, pair => (string?)pair.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        var fields = JellyfinProviderQuery.Values(Request.Query["Fields"].ToString()).ToList();
        if (JellyfinProviderQuery.Values(Request.Query["SortBy"].ToString()).Contains("SortName", StringComparer.OrdinalIgnoreCase))
            fields.Add("SortName");
        if (!string.IsNullOrWhiteSpace(Request.Query["Genres"]) || !string.IsNullOrWhiteSpace(Request.Query["GenreIds"]))
            fields.Add("Genres");
        if (fields.Count > 0) query["Fields"] = string.Join(',', fields.Distinct(StringComparer.OrdinalIgnoreCase));
        if (bool.TryParse(Request.Query["IsFavorite"], out _) ||
            JellyfinProviderQuery.Values(Request.Query["Filters"].ToString()).Contains("IsFavorite", StringComparer.OrdinalIgnoreCase))
            query["EnableUserData"] = "true";
        query["UserId"] = HttpContext.RequireProtocolExecutionContext().VerifiedBackendPrincipalId;
        var (body, status) = await _proxyService.GetJsonAsync(QueryHelpers.AddQueryString($"Playlists/{Uri.EscapeDataString(id)}/Items", query), null, Request.Headers);
        using (body)
        {
            var original = body?.RootElement.GetRawText();
            return (status is >= 200 and < 300 && original != null ? JsonNode.Parse(original) as JsonObject : null, status, original);
        }
    }

    private static IReadOnlyList<WritablePlaylistMember> NativeMembers(JsonObject envelope) =>
        (envelope["Items"] as JsonArray ?? []).OfType<JsonObject>().Select(item => new WritablePlaylistMember(
            item["PlaylistItemId"]?.GetValue<string>() ?? item["Id"]!.GetValue<string>(), item["Id"]!.GetValue<string>(),
            NativeEntryId: item["PlaylistItemId"]?.GetValue<string>())).ToArray();

    private async Task<IActionResult> ReadWritablePlaylistItemsAsync(string id, WritablePlaylistState saved)
    {
        var handover = await HandoverWritablePlaylistAsync(id, saved);
        if (handover.Error is { } error) return StatusCode(error);
        saved = handover.Saved;
        var read = await ReadWritableNativeItemsAsync(id);
        if (read.Envelope == null) return StatusCode(read.Status);
        HttpContext.Items[JellyfinItemFields.PlaylistMembersKey] = true;
        var nativeRows = (read.Envelope["Items"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
        var natives = NativeMembers(read.Envelope);
        var members = WritablePlaylistMembership.Reconcile(saved, natives);
        var rows = new List<Dictionary<string, object?>>();
        var copies = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        var remaining = nativeRows.ToList();
        foreach (var member in members)
        {
            if (member.Song != null)
            {
                var row = _responseBuilder.ConvertPlaylistSongToJellyfinItem(member.Song,
                    ExternalTrackPresentation.Preferences(HttpContext), saved.StoredAt);
                row.TryAdd("ParentId", id);
                row["PlaylistItemId"] = member.OccurrenceId;
                rows.Add(row);
            }
            else
            {
                var index = member.NativeEntryId == null ? remaining.FindIndex(row => row["Id"]?.GetValue<string>() == member.ItemId) :
                    remaining.FindIndex(row => row["PlaylistItemId"]?.GetValue<string>() == member.NativeEntryId);
                if (index < 0)
                {
                    if (member.PromotionSourceId == null) continue;
                    if (!copies.TryGetValue(member.ItemId, out var copy))
                    {
                        var context = HttpContext.RequireProtocolExecutionContext();
                        var (body, status) = await _proxyService.GetJsonAsync(
                            $"Users/{Uri.EscapeDataString(context.VerifiedBackendPrincipalId)}/Items/{Uri.EscapeDataString(member.ItemId)}",
                            new Dictionary<string, string> { ["Fields"] = Request.Query["Fields"].ToString() }, Request.Headers);
                        using (body)
                        {
                            if (status is < 200 or >= 300) return StatusCode(status);
                            if (body?.RootElement.ValueKind != JsonValueKind.Object ||
                                !body.RootElement.TryGetProperty("Id", out var itemId) || itemId.GetString() != member.ItemId)
                                return StatusCode(StatusCodes.Status502BadGateway);
                            copies[member.ItemId] = copy = body.RootElement.Deserialize<Dictionary<string, object?>>()!;
                        }
                    }
                    var occurrence = new Dictionary<string, object?>(copy) { ["PlaylistItemId"] = member.OccurrenceId };
                    rows.Add(occurrence);
                    continue;
                }
                rows.Add(remaining[index].Deserialize<Dictionary<string, object?>>()!);
                remaining.RemoveAt(index);
            }
        }
        var response = await ShapeProviderQueryAsync(rows, deduplicate: false, totalRecordCount: members.Count);
        var page = JsonNode.Parse(response.Content!)!.AsObject();
        read.Envelope["Items"] = page["Items"]!.DeepClone();
        read.Envelope["TotalRecordCount"] = page["TotalRecordCount"]!.DeepClone();
        read.Envelope["StartIndex"] = page["StartIndex"]!.DeepClone();
        return Content(NativeItemText.PreserveJson(read.Original!, read.Envelope.ToJsonString(), "Id"), "application/json");
    }

    private async Task<(WritablePlaylistState Saved, int? Error)> HandoverWritablePlaylistAsync(string id, WritablePlaylistState saved)
    {
        var context = HttpContext.RequireProtocolExecutionContext();
        var token = HttpContext.RequestAborted;
        var resolved = await WritablePlaylists.PreferLibraryCopiesAsync(context, saved, token);
        if (!resolved.Members.Any(item => item.PromotionSourceId != null)) return (saved, null);
        using var lease = await WritablePlaylists.LockAsync(token);
        saved = await WritablePlaylists.ReadAsync(context, id, token) ?? saved;
        var read = await ReadWritableNativeItemsAsync(id);
        if (read.Envelope == null) return (saved, read.Status);
        var natives = NativeMembers(read.Envelope);
        if (saved.Members.Any(item => item.PromotionSourceId != null)) resolved = saved;
        else
        {
            var current = new WritablePlaylistState(WritablePlaylistMembership.Reconcile(saved, natives));
            resolved = await WritablePlaylists.PreferLibraryCopiesAsync(context, current, token);
            var (permission, status) = await _proxyService.PostJsonAsync(
                $"Playlists/{Uri.EscapeDataString(id)}/Items?UserId={Uri.EscapeDataString(context.VerifiedBackendPrincipalId)}", "{}", Request.Headers);
            permission?.Dispose();
            if (status == StatusCodes.Status403Forbidden) return (resolved, null);
            if (status != StatusCodes.Status204NoContent) return (saved, status);
            await WritablePlaylists.SaveAsync(context, id, resolved.Members, token);
        }
        var missing = resolved.Members.Where(item => !item.IsProvider).GroupBy(item => item.ItemId)
            .SelectMany(group => Enumerable.Repeat(group.Key, Math.Max(0, group.Count() - natives.Count(item => item.ItemId == group.Key)))).ToArray();
        if (missing.Length > 0)
        {
            var (body, status) = await _proxyService.PostJsonAsync(QueryHelpers.AddQueryString($"Playlists/{Uri.EscapeDataString(id)}/Items",
                new Dictionary<string, string?> { ["UserId"] = context.VerifiedBackendPrincipalId, ["Ids"] = string.Join(',', missing) }), "{}", Request.Headers);
            body?.Dispose();
            if (status == StatusCodes.Status403Forbidden) return (resolved, null);
            if (status is < 200 or >= 300) return (saved, status);
            read = await ReadWritableNativeItemsAsync(id);
            if (read.Envelope == null) return (saved, read.Status);
            natives = NativeMembers(read.Envelope);
        }
        if (resolved.Members.Where(item => !item.IsProvider).GroupBy(item => item.ItemId)
            .Any(group => natives.Count(item => item.ItemId == group.Key) < group.Count())) return (saved, StatusCodes.Status502BadGateway);
        var confirmed = new WritablePlaylistState(WritablePlaylistMembership.Reconcile(resolved, natives));
        await WritablePlaylists.SaveAsync(context, id, confirmed.Members, token);
        return (confirmed, null);
    }
}

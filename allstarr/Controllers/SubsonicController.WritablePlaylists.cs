using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Services.Common;
using allstarr.Services.Subsonic;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

public partial class SubsonicController
{
    private WritablePlaylistMembership WritablePlaylists => HttpContext.RequestServices.GetRequiredService<WritablePlaylistMembership>();

    private async Task<IActionResult?> TryWritablePlaylistMutationAsync(string? method, SubsonicRequestParameters parameters)
    {
        if (method is not ("createPlaylist" or "updatePlaylist" or "deletePlaylist")) return null;
        var format = parameters.GetValueOrDefault("f", "xml");
        var key = method == "deletePlaylist" ? "id" : "playlistId";
        var id = parameters.GetValueOrDefault(key);
        var originalId = id;
        if (PlaylistIdHelper.IsExternalPlaylist(id)) return _responseBuilder.CreateError(format, 50, "Playlist is read-only");
        if (_virtualPlaylistProtocolAdapter.IsVirtualPlaylistId(id))
        {
            var route = await _virtualPlaylistProtocolAdapter.ResolveMutationAsync(CurrentProtocolContext, id, HttpContext.RequestAborted);
            if (route == null) return _responseBuilder.CreateError(format, 50, "Playlist is not available to this user");
            if (!route.Writable || string.IsNullOrWhiteSpace(route.TargetPlaylistId))
                return _responseBuilder.CreateError(format, 50, "Playlist is read-only");
            id = route.TargetPlaylistId;
            parameters = parameters.ReplaceValue(key, id);
        }
        var saved = string.IsNullOrWhiteSpace(id) ? null : await WritablePlaylists.ReadAsync(CurrentProtocolContext, id, HttpContext.RequestAborted);
        var ids = parameters.GetAllValues(method == "createPlaylist" ? "songId" : "songIdToAdd");
        if (saved == null && !WritablePlaylistMembership.HasProviderIds(ids))
        {
            // Replacement and deletion must also translate writable virtual targets.
            return id != originalId
                ? await RelayMutation($"rest/{method}", parameters) : null;
        }
        if (CurrentProtocolContext.Actor == null) return _responseBuilder.CreateError(format, 50, "Playlist is not available to this user");
        using var lease = await WritablePlaylists.LockAsync(HttpContext.RequestAborted);
        saved = string.IsNullOrWhiteSpace(id) ? null : await WritablePlaylists.ReadAsync(CurrentProtocolContext, id, HttpContext.RequestAborted);
        var current = new List<WritablePlaylistMember>();
        if (!string.IsNullOrWhiteSpace(id))
        {
            var read = await ReadWritableNativePlaylistAsync(id, parameters);
            if (!SubsonicPlaylistSucceeded(read)) return _relayProtocolAdapter.CreateResult(read, $"application/{format}");
            if (SubsonicPlaylistOwner(read) != CurrentProtocolContext.VerifiedBackendPrincipalId)
                return _responseBuilder.CreateError(format, 50, "Playlist is read-only");
            current = WritablePlaylistMembership.Reconcile(saved, SubsonicNativeMembers(read)).ToList();
        }
        if (method == "deletePlaylist")
        {
            var deleted = await _proxyService.RelayRawAsync(Request.Path.Value?.TrimStart('/') ?? "rest/deletePlaylist", parameters,
                HttpContext.RequestAborted, Request.Headers);
            if (SubsonicPlaylistSucceeded(deleted))
            {
                if (CurrentProtocolContext.Principal?.IsSynthetic == true)
                    await WritablePlaylists.ForgetSyntheticDeletedPlaylistAsync(CurrentProtocolContext, id, HttpContext.RequestAborted);
                else await WritablePlaylists.SaveAsync(CurrentProtocolContext, id, [], HttpContext.RequestAborted);
            }
            return _relayProtocolAdapter.CreateResult(deleted, $"application/{format}");
        }
        IReadOnlyList<WritablePlaylistMember> desired;
        if (method == "createPlaylist")
        {
            desired = await WritablePlaylists.ResolveAsync(CurrentProtocolContext, ids, HttpContext.RequestAborted);
            desired = WritablePlaylistMembership.PreserveOccurrences(current, desired);
            parameters = PlaylistValues(parameters, "songId", desired.Where(member => !member.IsProvider).Select(member => member.ItemId));
        }
        else
        {
            var removals = new HashSet<int>();
            foreach (var value in parameters.GetAllValues("songIndexToRemove"))
            {
                if (!int.TryParse(value, out var index) || index < 0 || index >= current.Count)
                    return _responseBuilder.CreateError(format, 10, "Invalid playlist song index");
                removals.Add(index);
            }
            var nativeRemovals = current.Where((member, index) => !member.IsProvider && removals.Contains(index))
                .Select(member => member.NativePosition!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            parameters = PlaylistValues(parameters, "songIndexToRemove", nativeRemovals);
            var added = await WritablePlaylists.ResolveAsync(CurrentProtocolContext, ids, HttpContext.RequestAborted);
            desired = current.Where((_, index) => !removals.Contains(index)).Concat(added).ToArray();
            parameters = PlaylistValues(parameters, "songIdToAdd", added.Where(member => !member.IsProvider).Select(member => member.ItemId));
        }
        var endpoint = Request.Path.Value?.TrimStart('/') ?? $"rest/{method}";
        var response = await _proxyService.RelayRawAsync(endpoint, parameters, HttpContext.RequestAborted, Request.Headers);
        if (!SubsonicPlaylistSucceeded(response)) return _relayProtocolAdapter.CreateResult(response, $"application/{format}");
        if (string.IsNullOrWhiteSpace(id)) id = SubsonicPlaylistId(response);
        if (string.IsNullOrWhiteSpace(id)) return _responseBuilder.CreateError(format, 0, "The backend did not return a playlist identifier");
        var final = await ReadWritableNativePlaylistAsync(id, parameters);
        if (!SubsonicPlaylistSucceeded(final)) return _relayProtocolAdapter.CreateResult(final, $"application/{format}");
        var stored = WritablePlaylistMembership.Reconcile(new(desired), SubsonicNativeMembers(final));
        await WritablePlaylists.SaveAsync(CurrentProtocolContext, id, stored, HttpContext.RequestAborted);
        return method == "createPlaylist"
            ? _relayProtocolAdapter.CreateResult(MergeWritablePlaylist(final, new(stored)), $"application/{format}")
            : _relayProtocolAdapter.CreateResult(response, $"application/{format}");
    }

    private Task<SubsonicProxyResponse> ReadWritableNativePlaylistAsync(string id, SubsonicRequestParameters parameters)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "songId", "songIdToAdd", "songIndexToRemove", "playlistId", "name", "comment", "public", "id" };
        var read = parameters.Select(parameters.Keys.Where(key => !keys.Contains(key)).ToHashSet(StringComparer.OrdinalIgnoreCase)).SetValue("id", id);
        return _proxyService.RelayRawAsync("rest/getPlaylist.view", read, HttpContext.RequestAborted, Request.Headers);
    }

    private static SubsonicRequestParameters PlaylistValues(SubsonicRequestParameters original, string name, IEnumerable<string> values)
    {
        var replacements = values.ToArray();
        var matching = original.Ordered.Where(pair => pair.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        var source = matching.FirstOrDefault()?.Source ?? SubsonicParameterSource.Query;
        var remaining = original.Ordered.Where(pair => !pair.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        remaining.AddRange(replacements.Select(value => new SubsonicParameter(name, value, source)));
        var body = original.RawBody;
        if (source != SubsonicParameterSource.Query || matching.Any(pair => pair.Source != SubsonicParameterSource.Query))
        {
            if (original.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
            {
                var json = string.IsNullOrWhiteSpace(body) ? new JsonObject() : JsonNode.Parse(body)!.AsObject();
                foreach (var key in json.Select(pair => pair.Key).Where(key => key.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray())
                    json.Remove(key);
                if (source != SubsonicParameterSource.Query && replacements.Length > 0)
                    json[name] = new JsonArray(replacements.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
                body = json.ToJsonString();
            }
            else body = SubsonicRequestParameters.EncodePairs(remaining.Where(pair => pair.Source != SubsonicParameterSource.Query));
        }
        return new(original.Method, original.ContentType, body, remaining);
    }

    private static bool SubsonicPlaylistJson(SubsonicProxyResponse response) => response.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true;
    private static bool SubsonicPlaylistSucceeded(SubsonicProxyResponse response)
    {
        if (!response.IsSuccessStatusCode) return false;
        return SubsonicPlaylistJson(response)
            ? JsonNode.Parse(response.Body)?["subsonic-response"]?["status"]?.GetValue<string>() == "ok"
            : XDocument.Parse(Encoding.UTF8.GetString(response.Body)).Root?.Attribute("status")?.Value == "ok";
    }
    private static string? SubsonicPlaylistId(SubsonicProxyResponse response) => SubsonicPlaylistJson(response)
        ? JsonNode.Parse(response.Body)?["subsonic-response"]?["playlist"]?["id"]?.GetValue<string>()
        : XDocument.Parse(Encoding.UTF8.GetString(response.Body)).Root?.Elements().FirstOrDefault(item => item.Name.LocalName == "playlist")?.Attribute("id")?.Value;
    private static string? SubsonicPlaylistOwner(SubsonicProxyResponse response) => SubsonicPlaylistJson(response)
        ? JsonNode.Parse(response.Body)?["subsonic-response"]?["playlist"]?["owner"]?.GetValue<string>()
        : XDocument.Parse(Encoding.UTF8.GetString(response.Body)).Root?.Elements().FirstOrDefault(item => item.Name.LocalName == "playlist")?.Attribute("owner")?.Value;
    private static IReadOnlyList<WritablePlaylistMember> SubsonicNativeMembers(SubsonicProxyResponse response)
    {
        var ids = SubsonicPlaylistJson(response)
            ? (JsonNode.Parse(response.Body)?["subsonic-response"]?["playlist"]?["entry"] as JsonArray ?? [])
                .Select(item => item!["id"]!.GetValue<string>())
            : XDocument.Parse(Encoding.UTF8.GetString(response.Body)).Root!.Elements().Where(item => item.Name.LocalName == "playlist")
                .Elements().Where(item => item.Name.LocalName == "entry").Select(item => item.Attribute("id")!.Value);
        var playlistId = SubsonicPlaylistId(response);
        var copies = new Dictionary<string, int>(StringComparer.Ordinal);
        return ids.Select((id, index) =>
        {
            var ordinal = copies.GetValueOrDefault(id);
            copies[id] = ordinal + 1;
            var occurrence = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes($"{playlistId}\n{id}\n{ordinal}")));
            return new WritablePlaylistMember($"native-{occurrence}", id) { NativePosition = index };
        }).ToArray();
    }

    private SubsonicProxyResponse MergeWritablePlaylist(SubsonicProxyResponse response, WritablePlaylistState saved)
    {
        if (!SubsonicPlaylistSucceeded(response)) return response;
        var members = WritablePlaylistMembership.Reconcile(saved, SubsonicNativeMembers(response));
        var original = Encoding.UTF8.GetString(response.Body);
        string updated;
        if (SubsonicPlaylistJson(response))
        {
            var root = JsonNode.Parse(original)!.AsObject();
            var playlist = root["subsonic-response"]!["playlist"]!.AsObject();
            var native = (playlist["entry"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            var rows = new List<JsonNode>();
            foreach (var member in members)
            {
                if (member.Song != null)
                {
                    var row = JsonSerializer.SerializeToNode(_responseBuilder.ConvertSongToJson(member.Song))!.AsObject();
                    row["playlistItemId"] = member.OccurrenceId;
                    rows.Add(row);
                }
                else
                {
                    var index = native.FindIndex(row => row["id"]?.GetValue<string>() == member.ItemId);
                    if (index < 0) continue;
                    rows.Add(native[index].DeepClone());
                    native.RemoveAt(index);
                }
            }
            playlist["entry"] = new JsonArray(rows.ToArray());
            playlist["songCount"] = members.Count;
            var durations = rows.OfType<JsonObject>().Select(row => row["duration"]?.GetValue<long>()).ToArray();
            if (durations.All(value => value.HasValue)) playlist["duration"] = durations.Sum(value => value!.Value);
            else playlist.Remove("duration");
            updated = NativeItemText.PreserveJson(original, root.ToJsonString(), "id");
        }
        else
        {
            var document = XDocument.Parse(original, LoadOptions.PreserveWhitespace);
            var playlist = document.Root!.Elements().Single(item => item.Name.LocalName == "playlist");
            var native = playlist.Elements().Where(item => item.Name.LocalName == "entry").ToList();
            var rows = new List<XElement>();
            foreach (var member in members)
            {
                if (member.Song != null)
                {
                    var row = _responseBuilder.ConvertSongToXml(member.Song, playlist.Name.Namespace);
                    row.Name = playlist.Name.Namespace + "entry";
                    row.Add(new XAttribute("playlistItemId", member.OccurrenceId));
                    rows.Add(row);
                }
                else
                {
                    var index = native.FindIndex(row => row.Attribute("id")?.Value == member.ItemId);
                    if (index < 0) continue;
                    rows.Add(new XElement(native[index]));
                    native.RemoveAt(index);
                }
            }
            playlist.Elements().Where(item => item.Name.LocalName == "entry").Remove();
            playlist.Add(rows);
            playlist.SetAttributeValue("songCount", members.Count);
            var durations = rows.Select(row => long.TryParse(row.Attribute("duration")?.Value,
                System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
                ? (long?)seconds : null).ToArray();
            if (durations.All(value => value.HasValue)) playlist.SetAttributeValue("duration", durations.Sum(value => value!.Value));
            else playlist.Attribute("duration")?.Remove();
            updated = NativeItemText.PreserveXml(original, document.ToString(SaveOptions.DisableFormatting));
        }
        return response with { Body = Encoding.UTF8.GetBytes(updated) };
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Core.Protocols;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace allstarr.Controllers;

public partial class JellyfinController
{
    private async Task<IActionResult> ReadItemsByIdsAsync(
        string ids, int startIndex, int limit, string? userId)
    {
        var requested = ids.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (requested.Length == 0) return CreateEmptyItemsResponse(startIndex);

        var items = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var nativeIds = new List<string>();
        foreach (var itemId in requested.Distinct(StringComparer.Ordinal))
        {
            var (isExternal, provider, type, externalId) = _localLibraryService.ParseExternalId(itemId);
            if (!isExternal)
            {
                if (!itemId.StartsWith("ext-", StringComparison.OrdinalIgnoreCase)) nativeIds.Add(itemId);
                continue;
            }

            Dictionary<string, object?>? item = null;
            var protocol = HttpContext.RequireProtocolExecutionContext();
            switch (type)
            {
                case "song":
                    var song = await _providerGateway.GetSongAsync(protocol, provider!, externalId!);
                    if (song != null) item = _responseBuilder.ConvertSongToJellyfinItem(song);
                    break;
                case "album":
                    var album = await _providerGateway.GetAlbumAsync(protocol, provider!, externalId!);
                    if (album != null) item = _responseBuilder.ConvertAlbumToJellyfinItem(album);
                    break;
                case "artist":
                    var artist = await _providerGateway.GetArtistAsync(protocol, provider!, externalId!);
                    if (artist != null) item = _responseBuilder.ConvertArtistToJellyfinItem(artist);
                    break;
            }
            if (item != null) items[itemId] = JsonSerializer.SerializeToNode(item)!.AsObject();
        }

        JsonObject envelope = new();
        if (nativeIds.Count > 0)
        {
            var query = Request.Query.ToDictionary(
                pair => pair.Key, pair => (string?)pair.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            query["Ids"] = string.Join(',', nativeIds);
            query["StartIndex"] = "0";
            query["Limit"] = nativeIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var endpoint = string.IsNullOrWhiteSpace(userId) ? "Items" : $"Users/{userId}/Items";
            var (result, statusCode) = await _proxyService.GetJsonAsync(
                QueryHelpers.AddQueryString(endpoint, query), null, Request.Headers);
            if (statusCode != StatusCodes.Status200OK || result == null)
                return HandleProxyResponse(result, statusCode);

            envelope = JsonNode.Parse(result.RootElement.GetRawText()) as JsonObject ?? new JsonObject();
            var selected = nativeIds.ToHashSet(StringComparer.Ordinal);
            if (envelope["Items"] is JsonArray nativeItems)
            {
                foreach (var row in nativeItems.OfType<JsonObject>())
                {
                    if (row["Id"] is JsonValue value && value.TryGetValue<string>(out var id) &&
                        selected.Contains(id))
                        items[id] = row;
                }
            }
        }

        var ordered = requested.Where(items.ContainsKey).Select(id => items[id]).ToArray();
        envelope["Items"] = new JsonArray(ordered.Skip(Math.Max(0, startIndex)).Take(Math.Max(0, limit))
            .Select(item => item.DeepClone()).ToArray());
        envelope["TotalRecordCount"] = ordered.Length;
        envelope["StartIndex"] = startIndex;
        return new JsonResult(envelope);
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

public partial class JellyfinController
{
    private async Task<IActionResult?> TryReadNativeCatalogAsync(
        string? artistIds, string? parentId, string? includeItemTypes, string? userId)
    {
        var artistAlbums = !string.IsNullOrWhiteSpace(artistIds) && !artistIds.Contains(',') &&
                           string.Equals(includeItemTypes, "MusicAlbum", StringComparison.OrdinalIgnoreCase);
        var albumSongs = string.IsNullOrWhiteSpace(artistIds) && !string.IsNullOrWhiteSpace(parentId) &&
                         (string.IsNullOrWhiteSpace(includeItemTypes) ||
                          string.Equals(includeItemTypes, "Audio", StringComparison.OrdinalIgnoreCase));
        if (!artistAlbums && !albumSongs) return null;
        var parent = artistAlbums ? artistIds! : parentId!;
        var context = HttpContext.RequireProtocolExecutionContext();
        var endpoint = userId == null ? "Items" : $"Users/{Uri.EscapeDataString(userId)}/Items";
        var upstream = await _proxyService.SendPassthroughResponseAsync(
            Request, $"{endpoint}{Request.QueryString}", HttpContext.RequestAborted);
        try
        {
            if (!upstream.IsSuccessStatusCode) return new ProtocolRelayResponseResult(upstream);
            var bytes = await upstream.Content.ReadAsByteArrayAsync(HttpContext.RequestAborted);
            var envelope = JsonNode.Parse(bytes) as JsonObject;
            if (envelope?["Items"] is not JsonArray original) return new ProtocolRelayResponseResult(upstream);
            var (detail, status) = await _proxyService.GetJsonAsync(
                $"Users/{Uri.EscapeDataString(context.VerifiedBackendPrincipalId)}/Items/{Uri.EscapeDataString(parent)}",
                null, Request.Headers);
            using (detail)
            {
                var expectedType = artistAlbums ? "MusicArtist" : "MusicAlbum";
                if (status != 200 || detail == null ||
                    !detail.RootElement.TryGetProperty("Type", out var type) || type.GetString() != expectedType ||
                    !detail.RootElement.TryGetProperty("Name", out var name))
                    return new ProtocolRelayResponseResult(upstream);
                var owned = await ReadAllNativeCatalogRowsAsync(endpoint, envelope, original);
                if (owned == null) return new ProtocolRelayResponseResult(upstream);
                var titles = owned.Select(row => row["Name"]?.GetValue<string>() ?? "").ToArray();
                var merge = new MusicCatalogMerge(_providerGateway);
                var additions = new List<Dictionary<string, object?>>();
                if (artistAlbums)
                {
                    foreach (var album in await merge.MissingArtistAlbumsAsync(context, name.GetString() ?? "", titles))
                    {
                        var item = _responseBuilder.ConvertAlbumToJellyfinItem(album);
                        item["Name"] = ExternalTrackPresentation.Title(album.Title, false, context.Policy?.Preferences);
                        item["SortName"] = item["Name"];
                        var parentCredit = new[] { new { Id = parent, Name = name.GetString() } };
                        item["Artists"] = new[] { name.GetString() };
                        item["AlbumArtist"] = name.GetString();
                        item["ArtistItems"] = parentCredit;
                        item["AlbumArtists"] = parentCredit;
                        additions.Add(item);
                    }
                }
                else
                {
                    var artist = detail.RootElement.TryGetProperty("AlbumArtist", out var credit) ? credit.GetString() : null;
                    if (string.IsNullOrWhiteSpace(artist) && detail.RootElement.TryGetProperty("Artists", out var artists) &&
                        artists.ValueKind == JsonValueKind.Array)
                        artist = artists.EnumerateArray().FirstOrDefault().ValueKind == JsonValueKind.String
                            ? artists[0].GetString() : null;
                    foreach (var song in await merge.MissingAlbumSongsAsync(context, name.GetString() ?? "", artist ?? "", titles))
                        additions.Add(_responseBuilder.ConvertSongToJellyfinItem(song));
                }
                if (additions.Count == 0) return new ProtocolRelayResponseResult(upstream);
                var start = int.TryParse(Request.Query["StartIndex"], out var offset) ? Math.Max(0, offset) : 0;
                var limit = int.TryParse(Request.Query["Limit"], out var count) ? Math.Max(0, count) : int.MaxValue;
                var combined = owned.Concat(additions.Select(item => JsonSerializer.SerializeToNode(item)!)).ToList();
                envelope!["Items"] = new JsonArray(combined.Skip(start).Take(limit).Select(row => row.DeepClone()).ToArray());
                envelope["TotalRecordCount"] = combined.Count;
                envelope["StartIndex"] = start;
                var result = Content(envelope.ToJsonString(), "application/json");
                result.StatusCode = (int)upstream.StatusCode;
                upstream.Dispose();
                return result;
            }
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            upstream.Dispose();
            throw;
        }
        catch (Exception error)
        {
            _logger.LogDebug("Provider catalog additions were unavailable ({ExceptionType})", error.GetType().Name);
            return new ProtocolRelayResponseResult(upstream);
        }
    }

    private async Task<List<JsonNode>?> ReadAllNativeCatalogRowsAsync(string endpoint, JsonObject? envelope, JsonArray original)
    {
        var total = envelope?["TotalRecordCount"]?.GetValue<int>();
        var start = int.TryParse(Request.Query["StartIndex"], out var offset) ? Math.Max(0, offset) : 0;
        if (start == 0 && (!total.HasValue || total.Value == original.Count))
            return original.OfType<JsonObject>().Cast<JsonNode>().ToList();
        if (!total.HasValue || total.Value < 0) return null;
        var rows = new List<JsonNode>();
        var query = Request.Query.ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        while (rows.Count < total.Value)
        {
            query["StartIndex"] = rows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            query["Limit"] = Math.Min(500, total.Value - rows.Count).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var (page, status) = await _proxyService.GetJsonAsync(endpoint, query, Request.Headers);
            using (page)
            {
                if (status != 200 || page == null || !page.RootElement.TryGetProperty("Items", out var items) ||
                    items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0) return null;
                if (page.RootElement.TryGetProperty("TotalRecordCount", out var currentTotal) && currentTotal.GetInt32() != total)
                    return null;
                rows.AddRange(items.EnumerateArray().Select(item => JsonNode.Parse(item.GetRawText())!));
                if (rows.Count > total.Value) return null;
            }
        }
        return rows;
    }
}

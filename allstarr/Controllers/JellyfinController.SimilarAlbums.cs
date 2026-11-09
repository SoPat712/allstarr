using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Core.ClientApi;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

public partial class JellyfinController
{
    // An album's Similar list is Allstarr's similar albums: related artists from the listener's sources, each shown by
    // the album the listener plays most or the artist's newest. Null when the item isn't an album, so the caller
    // carries on as before.
    private async Task<IActionResult?> TrySimilarAlbumsAsync(string itemId, int limit)
    {
        if (HttpContext.RequestServices.GetService<SimilarAlbumsReader>() is not { } reader ||
            HttpContext.GetProtocolExecutionContext() is not { Actor: not null } context) return null;
        ClientSimilarAlbums similar;
        try { similar = await reader.ReadAsync(HttpContext, context, itemId, limit); }
        catch (Exception exception) when (exception is ArgumentException or KeyNotFoundException or UnauthorizedAccessException
                                              or HttpRequestException)
        { return null; }

        var native = await NativeAlbumsAsync(similar.Albums.Where(album => !album.IsExternal).Select(album => album.Id).ToArray());
        var items = new JsonArray();
        foreach (var album in similar.Albums)
        {
            JsonObject? item;
            if (album.IsExternal)
            {
                var (_, provider, _, externalId) = _localLibraryService.ParseExternalId(album.Id);
                item = JsonSerializer.SerializeToNode(_responseBuilder.ConvertAlbumToJellyfinItem(new Album
                {
                    Id = album.Id,
                    Title = album.Title,
                    Artist = album.Artist,
                    ArtistId = album.ArtistId,
                    Year = album.Year,
                    SongCount = album.SongCount,
                    ExternalProvider = provider ?? album.Source,
                    ExternalId = externalId,
                    IsLocal = false
                }))?.AsObject();
            }
            else item = native.GetValueOrDefault(album.Id)?.DeepClone().AsObject();
            if (item == null) continue;
            item["AllstarrSource"] = album.Source;
            if (album.Provider != null) item["AllstarrProvider"] = JsonSerializer.SerializeToNode(album.Provider, ClientApiContract.JsonOptions);
            items.Add(item);
        }
        return new JsonResult(new JsonObject
        {
            ["Items"] = items,
            ["TotalRecordCount"] = items.Count,
            ["StartIndex"] = 0,
            ["AllstarrBasedOn"] = similar.BasedOn
        });
    }

    // Library picks come back exactly as the media server describes them, with every field and image tag.
    private async Task<Dictionary<string, JsonObject>> NativeAlbumsAsync(IReadOnlyList<string> ids)
    {
        var found = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        if (ids.Count == 0) return found;
        var user = HttpContext.RequireProtocolExecutionContext().VerifiedBackendPrincipalId;
        var fields = Request.Query["Fields"].ToString();
        var (body, status) = await _proxyService.GetJsonAsync($"Users/{Uri.EscapeDataString(user)}/Items", new()
        {
            ["Ids"] = string.Join(',', ids),
            ["Fields"] = string.IsNullOrWhiteSpace(fields) ? "PrimaryImageAspectRatio,ProductionYear,ChildCount" : fields
        }, Request.Headers, HttpContext.RequestAborted);
        using (body)
        {
            if (status != 200 || body?.RootElement.TryGetProperty("Items", out var rows) != true) return found;
            foreach (var row in rows.EnumerateArray())
                if (row.TryGetProperty("Id", out var id) && id.GetString() is { } value)
                    found[value] = JsonNode.Parse(row.GetRawText())!.AsObject();
        }
        return found;
    }
}

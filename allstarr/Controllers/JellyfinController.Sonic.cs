using System.Text.Json;
using allstarr.Core.Protocols;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

public partial class JellyfinController
{
    private async Task<IActionResult?> TryGetSonicInstantMixAsync(
        string itemId,
        int limit,
        string? fields)
    {
        if (_audioMuse?.IsAvailable != true || limit is < 1 or > 200 ||
            _localLibraryService.ParseExternalId(itemId).isExternal)
            return null;

        var path = Request.Path.Value ?? "";
        var kind = path.Contains("/Albums/", StringComparison.OrdinalIgnoreCase) ? "album"
            : path.Contains("/Artists", StringComparison.OrdinalIgnoreCase) ? "artist"
            : path.Contains("/Songs/", StringComparison.OrdinalIgnoreCase) ||
              path.Contains("/Items/", StringComparison.OrdinalIgnoreCase) ? "song"
            : null;
        if (kind == null) return null;

        try
        {
            var context = HttpContext.RequireProtocolExecutionContext();
            var scope = await SonicProtocolScope.ResolveAsync(
                context, itemId, _libraryScopes, _intelligencePolicies, HttpContext.RequestAborted);
            if (scope == null) return null;

            var seeds = kind == "song"
                ? [itemId]
                : await ReadSonicSeedsAsync(itemId, kind, Math.Min(limit, 10));
            if (seeds.Length == 0) return null;

            var tracks = await _audioMuse.FindSimilarAsync(
                scope, seeds, limit, HttpContext.RequestAborted);
            var items = await ReadSonicItemsAsync(context, tracks.Select(track =>
                track.Identity?.BackendItemId).OfType<string>().ToArray(), fields);
            return items == null
                ? null
                : CreateProtocolResponse(_interactionProtocolAdapter.ShapeInstantMix(items));
        }
        catch (Exception exception) when (exception is not OperationCanceledException ||
                                           !HttpContext.RequestAborted.IsCancellationRequested)
        {
            _logger.LogWarning(
                "AudioMuse Instant Mix fell back to Jellyfin ({ExceptionType})",
                exception.GetType().Name);
            return null;
        }
    }

    private async Task<string[]> ReadSonicSeedsAsync(
        string itemId,
        string kind,
        int limit)
    {
        var (body, statusCode) = await _proxyService.GetItemsAsync(
            parentId: kind == "album" ? itemId : null,
            includeItemTypes: ["Audio"],
            limit: limit,
            artistIds: kind == "artist" ? itemId : null,
            clientHeaders: Request.Headers);
        using (body)
        {
            if (statusCode is < 200 or >= 300 || body == null ||
                !body.RootElement.TryGetProperty("Items", out var items)) return [];
            return items.EnumerateArray().Select(item =>
                    item.TryGetProperty("Id", out var id) ? id.GetString() : null)
                .OfType<string>().Distinct(StringComparer.Ordinal).Take(limit).ToArray();
        }
    }

    private async Task<IReadOnlyList<Dictionary<string, object?>>?> ReadSonicItemsAsync(
        ProtocolExecutionContext context,
        IReadOnlyList<string> orderedIds,
        string? fields)
    {
        var ids = orderedIds.Distinct(StringComparer.Ordinal).Take(200).ToArray();
        if (ids.Length == 0) return [];
        var query = new Dictionary<string, string>
        {
            ["Ids"] = string.Join(',', ids),
            ["UserId"] = context.VerifiedBackendPrincipalId,
            ["Recursive"] = "true",
            ["EnableImages"] = "true",
            ["EnableUserData"] = "true",
            ["Limit"] = ids.Length.ToString()
        };
        if (!string.IsNullOrWhiteSpace(fields)) query["Fields"] = fields;

        var (body, statusCode) = await _proxyService.GetJsonAsync("Items", query, Request.Headers);
        using (body)
        {
            if (statusCode is < 200 or >= 300 || body == null ||
                !body.RootElement.TryGetProperty("Items", out var values)) return null;
            var byId = values.EnumerateArray().Where(value => value.TryGetProperty("Id", out _))
                .ToDictionary(value => value.GetProperty("Id").GetString()!, value =>
                    JsonSerializer.Deserialize<Dictionary<string, object?>>(value.GetRawText())!,
                    StringComparer.Ordinal);
            return orderedIds.Where(byId.ContainsKey).Select(id => byId[id]).ToArray();
        }
    }
}

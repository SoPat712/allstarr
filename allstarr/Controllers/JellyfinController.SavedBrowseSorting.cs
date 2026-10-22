using System.Globalization;
using System.Text.Json;
using allstarr.Core.Protocols;
using allstarr.Services.Jellyfin;

namespace allstarr.Controllers;

public partial class JellyfinController
{
    private async Task ReadNativeAddedDatesAsync(IReadOnlyList<Dictionary<string, object?>> rows,
        Dictionary<string, string> query, IHeaderDictionary headers, bool latest)
    {
        var sorts = JellyfinProviderQuery.Values(query.GetValueOrDefault("SortBy") ?? "");
        if (!(latest && sorts.Length == 0) && !sorts.Contains("DateCreated", StringComparer.OrdinalIgnoreCase)) return;
        if (JellyfinProviderQuery.Values(query.GetValueOrDefault("Fields") ?? "").Contains("DateCreated", StringComparer.OrdinalIgnoreCase)) return;
        var missing = rows.Where(row => !IsProviderQueryItem(row) && !row.ContainsKey("DateCreated"))
            .GroupBy(row => JellyfinProviderQuery.Text(row, "Id"), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Key.Length != 0)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var user = HttpContext.RequireProtocolExecutionContext().VerifiedBackendPrincipalId;
        foreach (var batch in missing.Keys.Chunk(400))
        {
            var metadataQuery = query.Where(pair => pair.Key.Equals("ApiKey", StringComparison.OrdinalIgnoreCase) ||
                pair.Key.Equals("api_key", StringComparison.OrdinalIgnoreCase) || pair.Key.Equals("access_token", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            metadataQuery["Ids"] = string.Join(',', batch);
            metadataQuery["Fields"] = "DateCreated";
            metadataQuery["IncludeItemTypes"] = JellyfinMusicEndpointPolicy.DefaultMusicItemTypes;
            metadataQuery["Recursive"] = "true";
            metadataQuery["StartIndex"] = "0";
            metadataQuery["Limit"] = batch.Length.ToString(CultureInfo.InvariantCulture);
            var (metadata, status) = await _proxyService.GetJsonAsync($"Users/{Uri.EscapeDataString(user)}/Items",
                metadataQuery, headers, HttpContext.RequestAborted);
            using (metadata)
            {
                if (status != 200 || metadata == null || !metadata.RootElement.TryGetProperty("Items", out var items) ||
                    items.ValueKind != JsonValueKind.Array) continue;
                foreach (var item in items.EnumerateArray())
                    if (item.TryGetProperty("Id", out var id) && id.ValueKind == JsonValueKind.String &&
                        missing.TryGetValue(id.GetString()!, out var originals) && item.TryGetProperty("DateCreated", out var date) &&
                        date.ValueKind == JsonValueKind.String)
                        foreach (var row in originals) row["DateCreated"] = date.GetString();
            }
        }
    }
}

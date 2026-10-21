using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace allstarr.Filters;

public sealed class JellyfinHistoryQueryFilter(ProviderUserDataStore userData) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var http = context.HttpContext;
        var fields = Values(http.Request.Query["SortBy"].ToString());
        if (!HttpMethods.IsGet(http.Request.Method) || !fields.Any(IsHistoryField) || !IsProviderCollection(http.Request))
        {
            await next();
            return;
        }
        var original = http.Request.QueryString;
        var start = int.TryParse(http.Request.Query["StartIndex"], out var offset) ? Math.Max(0, offset) : 0;
        var limit = int.TryParse(http.Request.Query["Limit"], out var count) ? Math.Max(0, count) : int.MaxValue;
        var orders = Values(http.Request.Query["SortOrder"].ToString());
        var parameters = http.Request.Query.Where(pair => !pair.Key.Equals("StartIndex", StringComparison.OrdinalIgnoreCase) &&
                !pair.Key.Equals("Limit", StringComparison.OrdinalIgnoreCase))
            .SelectMany(pair => pair.Value.Select(value => new KeyValuePair<string, string?>(pair.Key, value)))
            .Concat([new("StartIndex", "0"), new("Limit", int.MaxValue.ToString(CultureInfo.InvariantCulture))]);
        ActionExecutedContext executed;
        try
        {
            http.Request.QueryString = QueryString.Create(parameters);
            // Model binding has already run, so update bound page arguments as well.
            if (context.ActionArguments.ContainsKey("startIndex")) context.ActionArguments["startIndex"] = 0;
            if (context.ActionArguments.ContainsKey("limit")) context.ActionArguments["limit"] = int.MaxValue;
            executed = await next();
        }
        finally { http.Request.QueryString = original; }
        if (executed.Exception != null) return;
        var source = executed.Result switch
        {
            JsonResult json => JsonSerializer.Serialize(json.Value),
            ContentResult { Content: { } content, ContentType: { } type } when type.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) => content,
            _ => null
        };
        if (source == null || JsonNode.Parse(source) is not JsonObject root || root["Items"] is not JsonArray items) return;
        var rows = items.OfType<JsonObject>().ToList();
        var saved = await userData.ReadForRequestAsync(http, rows.Select(item => item["Id"]?.ToString() ?? ""));
        foreach (var item in rows)
        {
            var id = item["Id"]?.ToString() ?? "";
            if (ProviderUserDataStore.IsProviderItem(id) && item["UserData"] is JsonObject data)
                MusicUserDataProjection.Jellyfin(data, saved.GetValueOrDefault(id) ?? new());
        }
        var ordered = rows.OrderBy(item => item, Comparer<JsonObject>.Create((left, right) =>
        {
            for (var index = 0; index < fields.Length; index++)
            {
                var comparison = Compare(left, right, fields[index]);
                if (comparison == 0) continue;
                var order = orders.Length == 0 ? "Ascending" : orders[index < orders.Length ? index : 0];
                return order.Equals("Descending", StringComparison.OrdinalIgnoreCase) ? -comparison : comparison;
            }
            return 0;
        }));
        root["Items"] = new JsonArray(ordered.Skip(start).Take(limit).Select(item => item.DeepClone()).ToArray());
        root["StartIndex"] = start;
        executed.Result = new ContentResult
        {
            Content = NativeItemText.PreserveJson(source, root.ToJsonString(), "Id"),
            ContentType = "application/json",
            StatusCode = (executed.Result as ContentResult)?.StatusCode ?? (executed.Result as JsonResult)?.StatusCode
        };
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.Remove("ETag");
        http.Response.Headers.Remove("Last-Modified");
    }

    private static bool IsProviderCollection(HttpRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Query["SearchTerm"]) ||
            bool.TryParse(request.Query["IsFavorite"], out var favorite) && favorite ||
            Values(request.Query["Filters"].ToString()).Contains("IsFavorite", StringComparer.OrdinalIgnoreCase)) return true;
        var ids = new[] { "ParentId", "ArtistIds", "AlbumArtistIds", "ContributingArtistIds", "AlbumIds", "Ids" }
            .SelectMany(key => Values(request.Query[key].ToString())).ToArray();
        if (ids.Length > 0) return ids.Any(id => id.StartsWith("ext-", StringComparison.OrdinalIgnoreCase));
        var path = request.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];
        return path.Length == 3 && path[0].Equals("Playlists", StringComparison.OrdinalIgnoreCase) &&
            path[1].StartsWith("ext-", StringComparison.OrdinalIgnoreCase) && path[2].Equals("Items", StringComparison.OrdinalIgnoreCase);
    }

    private static int Compare(JsonObject left, JsonObject right, string field)
    {
        if (field.Equals("PlayCount", StringComparison.OrdinalIgnoreCase))
            return Number(left["UserData"]?["PlayCount"]).CompareTo(Number(right["UserData"]?["PlayCount"]));
        if (field.Equals("DatePlayed", StringComparison.OrdinalIgnoreCase))
            return Nullable.Compare(Date(left["UserData"]?["LastPlayedDate"]), Date(right["UserData"]?["LastPlayedDate"]));
        var key = field.ToLowerInvariant() switch
        {
            "indexnumber" => "IndexNumber",
            "parentindexnumber" => "ParentIndexNumber",
            "runtime" => "RunTimeTicks",
            "productionyear" => "ProductionYear",
            "name" => "Name",
            "sortname" => "SortName",
            "album" => "Album",
            _ => field
        };
        if (key is "IndexNumber" or "ParentIndexNumber" or "RunTimeTicks" or "ProductionYear")
            return Number(left[key]).CompareTo(Number(right[key]));
        return StringComparer.OrdinalIgnoreCase.Compare((left[key] ?? left["Name"])?.ToString(), (right[key] ?? right["Name"])?.ToString());
    }

    private static bool IsHistoryField(string value) => value.Equals("PlayCount", StringComparison.OrdinalIgnoreCase) || value.Equals("DatePlayed", StringComparison.OrdinalIgnoreCase);
    private static string[] Values(string value) => value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    private static long Number(JsonNode? value) => long.TryParse(value?.ToString(), CultureInfo.InvariantCulture, out var number) ? number : 0;
    private static DateTimeOffset? Date(JsonNode? value) => DateTimeOffset.TryParse(value?.ToString(), CultureInfo.InvariantCulture, out var date) ? date : null;
}

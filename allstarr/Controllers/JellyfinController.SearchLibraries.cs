using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

public partial class JellyfinController
{
    private Task<IReadOnlyList<string>?>? _searchedLibraries;

    /// <summary>
    /// The music libraries a search reads, as the listener chose them and within the app's own pick of one;
    /// null when nothing narrows the search.
    /// </summary>
    private Task<IReadOnlyList<string>?> SearchedLibrariesAsync(CancellationToken token)
    {
        return _searchedLibraries ??= ReadAsync();

        async Task<IReadOnlyList<string>?> ReadAsync()
        {
            if (HttpContext.RequestServices.GetService<ListeningLibraries>() is not { } libraries ||
                HttpContext.GetProtocolExecutionContext() is not { } context) return null;
            try { return await libraries.SearchedAsync(context, Request.Query["ParentId"].ToString(), token); }
            // Search never fails over the choice; it searches every library instead.
            catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested) { return null; }
        }
    }

    /// <summary>Copies of service songs come only from the libraries the search reads.</summary>
    private async Task<INativeMusicSearch<T>> SearchedCopiesAsync<T>(INativeMusicSearch<T> native, CancellationToken token) =>
        await SearchedLibrariesAsync(token) is { } searched && HttpContext.RequestServices.GetService<ListeningLibraries>() is { } libraries
            ? new SearchedLibraryCopies<T>(native, libraries, HttpContext.RequireProtocolExecutionContext(), searched) : native;

    private bool AppPickedLibrary => !string.IsNullOrWhiteSpace(Request.Query["ParentId"]);

    /// <summary>
    /// A library search narrowed to the chosen libraries: the media server searches one library at a time, so each is
    /// asked on its own and their rows are joined. An app that names a library is asked as it asked.
    /// </summary>
    private async Task<(JsonDocument? Body, int StatusCode)> ReadSearchedLibrariesAsync(string endpoint,
        Dictionary<string, string> query, string rows, CancellationToken token)
    {
        var searched = await SearchedLibrariesAsync(token);
        if (searched == null || searched.Count > 0 && AppPickedLibrary)
            return await _proxyService.GetJsonAsync(endpoint, query, Request.Headers, token);
        if (searched.Count == 0) return (JsonDocument.Parse(EmptyRows(rows, 0)), StatusCodes.Status200OK);
        Dictionary<string, string> With(string key, string value)
        {
            var copy = new Dictionary<string, string>(query, StringComparer.OrdinalIgnoreCase);
            copy[key] = value;
            return copy;
        }
        // Merged search ranks only songs, albums and artists, so no playlist search is added here.
        return JoinRows(await Task.WhenAll(searched.Select(library =>
            _proxyService.GetJsonAsync(endpoint, With("ParentId", library), Request.Headers, token))), rows);
    }

    /// <summary>The app's own library search, read from each chosen library, ranked together and paged as the app asked.</summary>
    private async Task<IActionResult?> ReadLibraryScopeAsync(string path, IReadOnlyList<string> parts, string search)
    {
        var token = HttpContext.RequestAborted;
        var searched = await SearchedLibrariesAsync(token);
        if (searched == null || searched.Count > 0 && AppPickedLibrary) return null;
        var start = int.TryParse(Request.Query["StartIndex"], out var first) ? Math.Max(0, first) : 0;
        int? limit = int.TryParse(Request.Query["Limit"], out var count) ? Math.Max(0, count) : null;
        if (searched.Count == 0) return Content(EmptyRows("Items", start), "application/json");
        static string Key(string part) => Uri.UnescapeDataString(part.Split('=', 2)[0]);
        var kept = parts.Where(part => Key(part).ToLowerInvariant() is not ("startindex" or "limit" or "parentid")).ToList();
        if (limit != null) kept.Add("Limit=" + (start + limit.Value).ToString(CultureInfo.InvariantCulture));
        var reads = searched.Select(library => _proxyService.GetJsonAsync(
            path + "?" + string.Join('&', kept.Append("ParentId=" + Uri.EscapeDataString(library))), null, Request.Headers, token)).ToList();
        var types = ParseItemTypes(QueryList("IncludeItemTypes", null));
        // Playlists belong to no music library, so they are searched on their own.
        if (types is not { Length: > 0 } || types.Contains("Playlist", StringComparer.OrdinalIgnoreCase))
            reads.Add(_proxyService.GetJsonAsync(path + "?" + string.Join('&', kept
                .Where(part => !Key(part).Equals("IncludeItemTypes", StringComparison.OrdinalIgnoreCase))
                .Append("IncludeItemTypes=Playlist")), null, Request.Headers, token));
        var (body, status) = JoinRows(await Task.WhenAll(reads), "Items");
        if (status != StatusCodes.Status200OK || body == null) return HandleProxyResponse(body, status);
        using (body)
        {
            var rows = body.RootElement.GetProperty("Items").EnumerateArray().Select(row => JsonNode.Parse(row.GetRawText())!)
                .Select((row, index) => (Row: row, Index: index,
                    Score: CalculateItemRelevanceScore(search, row.Deserialize<Dictionary<string, object?>>() ?? [])))
                .OrderByDescending(item => item.Score).ThenBy(item => item.Index).Select(item => item.Row);
            var page = new JsonArray(rows.Skip(start).Take(limit ?? int.MaxValue).ToArray());
            var total = body.RootElement.GetProperty("TotalRecordCount").GetInt32();
            return Content(new JsonObject { ["Items"] = page, ["TotalRecordCount"] = total, ["StartIndex"] = start }.ToJsonString(),
                "application/json");
        }
    }

    private static string EmptyRows(string rows, int start) =>
        new JsonObject { [rows] = new JsonArray(), ["TotalRecordCount"] = 0, ["StartIndex"] = start }.ToJsonString();

    /// <summary>One answer with every library's rows, each item once; the first answer stands when none succeeded.</summary>
    internal static (JsonDocument? Body, int StatusCode) JoinRows(IReadOnlyList<(JsonDocument? Body, int StatusCode)> answers, string rows)
    {
        static bool Listed(JsonDocument? body, string rows) => body?.RootElement.ValueKind == JsonValueKind.Object &&
            body.RootElement.TryGetProperty(rows, out var items) && items.ValueKind == JsonValueKind.Array;
        var found = answers.Where(answer => answer.StatusCode == StatusCodes.Status200OK && Listed(answer.Body, rows)).ToArray();
        if (found.Length == 0)
        {
            foreach (var answer in answers.Skip(1)) answer.Body?.Dispose();
            return answers[0];
        }
        var joined = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var total = 0;
        foreach (var (body, _) in found)
        {
            var items = body!.RootElement.GetProperty(rows);
            var added = 0;
            foreach (var item in items.EnumerateArray())
            {
                var id = item.ValueKind == JsonValueKind.Object && (item.TryGetProperty("Id", out var value) || item.TryGetProperty("ItemId", out value)) &&
                    value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                if (id != null && !seen.Add(id)) continue;
                joined.Add(JsonNode.Parse(item.GetRawText()));
                added++;
            }
            total += body.RootElement.TryGetProperty("TotalRecordCount", out var reported) && reported.TryGetInt32(out var counted)
                ? Math.Max(added, counted - (items.GetArrayLength() - added)) : added;
        }
        foreach (var answer in answers) answer.Body?.Dispose();
        return (JsonDocument.Parse(new JsonObject { [rows] = joined, ["TotalRecordCount"] = total, ["StartIndex"] = 0 }.ToJsonString()),
            StatusCodes.Status200OK);
    }
}

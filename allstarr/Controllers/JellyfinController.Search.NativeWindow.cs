using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Core.Protocols;
using allstarr.Services.Jellyfin;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

public partial class JellyfinController
{
    private async Task<IActionResult> ReadNativePlaylistSearchAsync(string endpoint, Dictionary<string, string> parameters,
        string search, int startIndex, int limit, bool musicLibraryParent, CancellationToken token)
    {
        var context = HttpContext.RequireProtocolExecutionContext();
        var requestedFields = JellyfinProviderQuery.Values(parameters.GetValueOrDefault("Fields") ?? "");
        var fields = string.Join(',', new[] { parameters.GetValueOrDefault("Fields"),
            "DateCreated,Etag,ParentId,Path,SortName,ProviderIds,ChildCount,Overview" }.Where(value => !string.IsNullOrEmpty(value)));
        if (parameters.GetValueOrDefault("SortBy")?.Contains("DateLastContentAdded", StringComparison.OrdinalIgnoreCase) == true)
            fields += ",DateLastMediaAdded";
        var heavy = requestedFields.Where(field =>
            field.Equals("MediaSources", StringComparison.OrdinalIgnoreCase) || field.Equals("MediaStreams", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mergeFields = new List<string> { "SortName", "ParentId" };
        if (parameters.GetValueOrDefault("SortBy")?.Contains("DateCreated", StringComparison.OrdinalIgnoreCase) == true)
            mergeFields.Add("DateCreated");
        if (parameters.GetValueOrDefault("SortBy")?.Contains("DateLastContentAdded", StringComparison.OrdinalIgnoreCase) == true)
            mergeFields.Add("DateLastMediaAdded");
        parameters["Fields"] = string.Join(',', requestedFields.Where(field => !heavy.Contains(field)).Concat(mergeFields)
            .Distinct(StringComparer.OrdinalIgnoreCase));
        parameters["SearchTerm"] = search;
        parameters["StartIndex"] = "0";
        var sort = parameters.GetValueOrDefault("SortBy");
        var simpleSort = string.IsNullOrWhiteSpace(sort) || sort.Equals("SortName", StringComparison.OrdinalIgnoreCase);
        var totalsDisabled = parameters.GetValueOrDefault("EnableTotalRecordCount")?.Equals("false", StringComparison.OrdinalIgnoreCase) == true;
        if (simpleSort && !totalsDisabled) parameters["SortBy"] = string.IsNullOrWhiteSpace(sort) ? "SortName" : sort!;
        else if (sort?.Contains("Random", StringComparison.OrdinalIgnoreCase) == true) parameters["SortBy"] = "SortName";
        var nativeCap = 10000;
        // Insertions from served summaries can only push native rows back. One lookahead detects a tied page boundary.
        var prefix = (int)Math.Min((long)nativeCap, Math.Max(0L, startIndex) + Math.Max(0L, limit));
        // Preserve the old first-page count without enabling a count the client disabled.
        var fetch = simpleSort ? Math.Min(nativeCap, prefix + 1) : nativeCap;
        if (totalsDisabled) fetch = simpleSort ? Math.Max(500, fetch) : 500;
        parameters["Limit"] = fetch.ToString(CultureInfo.InvariantCulture);
        var nativeTask = _proxyService.GetJsonAsync(endpoint, parameters, Request.Headers, token);
        var summariesTask = _virtualPlaylistProtocolAdapter.ListAsync(context, token, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var (body, status) = await nativeTask;
        if (status == 400 && sort != null && JellyfinProviderQuery.Values(sort).Any(field =>
            field.ToLowerInvariant() is not ("sortname" or "name" or "datecreated" or "datelastcontentadded" or "random")))
        {
            body?.Dispose();
            parameters.Remove("SortBy");
            (body, status) = await _proxyService.GetJsonAsync(endpoint, parameters, Request.Headers, token);
        }
        using (body)
        {
            if (status != 200 || body == null) return HandleProxyResponse(body, status);
            await RememberNativeCatalogTypesAsync(body.RootElement);
            var root = JsonNode.Parse(body.RootElement.GetRawText()) as JsonObject;
            if (root?["Items"] is not JsonArray nativeRows) return HandleProxyResponse(body, status);
            var reportedTotal = root["TotalRecordCount"]?.GetValue<int>() ?? 0;
            if (reportedTotal <= 0)
            {
                nativeCap = 500;
                while (nativeRows.Count > nativeCap) nativeRows.RemoveAt(nativeRows.Count - 1);
            }
            var total = reportedTotal > 0 ? Math.Min(nativeCap, reportedTotal) : nativeRows.Count;
            string Name(JsonNode? row) => row?["SortName"]?.ToString() ?? row?["Name"]?.ToString() ?? "";
            var descending = parameters.GetValueOrDefault("SortOrder")?.Equals("Descending", StringComparison.OrdinalIgnoreCase) == true;
            var monotonic = nativeRows.Zip(nativeRows.Skip(1)).All(pair =>
                (descending ? -1 : 1) * StringComparer.OrdinalIgnoreCase.Compare(Name(pair.First), Name(pair.Second)) <= 0);
            var preserveCappedSet = reportedTotal > nativeCap && string.IsNullOrWhiteSpace(sort);
            if (nativeRows.Count < total && (!simpleSort || !monotonic || preserveCappedSet || prefix > 0 && nativeRows.Count > prefix &&
                StringComparer.OrdinalIgnoreCase.Equals(Name(nativeRows[prefix - 1]), Name(nativeRows[nativeRows.Count - 1]))))
            {
                // Jellyfin has no ID sort key; a tie across the boundary needs the complete lightweight set for the existing ID tie-break.
                parameters["Limit"] = nativeCap.ToString(CultureInfo.InvariantCulture);
                if (preserveCappedSet) parameters.Remove("SortBy");
                var (complete, completeStatus) = await _proxyService.GetJsonAsync(endpoint, parameters, Request.Headers, token);
                using (complete)
                {
                    if (completeStatus != 200 || complete == null) return HandleProxyResponse(complete, completeStatus);
                    await RememberNativeCatalogTypesAsync(complete.RootElement);
                    root = JsonNode.Parse(complete.RootElement.GetRawText()) as JsonObject;
                    if (root?["Items"] is not JsonArray completeRows) return HandleProxyResponse(complete, completeStatus);
                    nativeRows = completeRows;
                    total = nativeRows.Count;
                }
            }
            var summaryQuery = new JellyfinProviderQuery(new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
            { ["SearchTerm"] = search }));
            var summaries = (await summariesTask).Where(item => item.TargetPlaylistId != null ||
                summaryQuery.Matches(_virtualPlaylistProtocolAdapter.ToItem(item))).ToArray();
            var visibleTargets = nativeRows.OfType<JsonObject>().Where(row => row["Type"]?.ToString() == "Playlist")
                .Select(row => row["Id"]?.ToString() ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (visibleTargets.Count > 0)
                summaries = (await _virtualPlaylistProtocolAdapter.ListAsync(context, token, visibleTargets)).Where(item => item.TargetPlaylistId != null ||
                    summaryQuery.Matches(_virtualPlaylistProtocolAdapter.ToItem(item))).ToArray();
            using var snapshot = JsonDocument.Parse(root!.ToJsonString());
            var result = await MergeVirtualPlaylistBrowseAsync(snapshot, limit, startIndex, token, musicLibraryParent, summaries, total);
            if (result is not ContentResult { Content: { } content } ||
                !JellyfinProviderQuery.Values(fields).Except(JellyfinProviderQuery.Values(parameters["Fields"]), StringComparer.OrdinalIgnoreCase).Any())
                return result;
            var page = JsonNode.Parse(content)!.AsObject();
            var rows = page["Items"]!.AsArray();
            var ids = rows.OfType<JsonObject>().Where(row => !JellyfinItemIdComparer.IsExternalId(row["Id"]?.ToString() ?? ""))
                .Select(row => row["Id"]!.ToString()).ToArray();
            if (ids.Length == 0) return result;
            var hydrate = new Dictionary<string, string>(parameters, StringComparer.OrdinalIgnoreCase)
            {
                ["Ids"] = string.Join(',', ids),
                ["Limit"] = ids.Length.ToString(CultureInfo.InvariantCulture),
                ["Fields"] = fields
            };
            hydrate.Remove("SearchTerm");
            var (details, detailStatus) = await _proxyService.GetJsonAsync(endpoint, hydrate, Request.Headers, token);
            using (details)
            {
                if (detailStatus != 200 || details == null || !details.RootElement.TryGetProperty("Items", out var items))
                    return HandleProxyResponse(details, detailStatus);
                var byId = items.EnumerateArray().ToDictionary(item => item.GetProperty("Id").GetString()!, JellyfinItemIdComparer.Instance);
                for (var index = 0; index < rows.Count; index++)
                {
                    if (rows[index] is not JsonObject row || !byId.TryGetValue(row["Id"]!.ToString(), out var detail)) continue;
                    var hydrated = new JsonObject();
                    foreach (var property in detail.EnumerateObject())
                    {
                        if (row.TryGetPropertyValue(property.Name, out var existing)) hydrated[property.Name] = existing?.DeepClone();
                        else hydrated[property.Name] = JsonNode.Parse(property.Value.GetRawText());
                    }
                    foreach (var property in row)
                        if (!hydrated.ContainsKey(property.Key)) hydrated[property.Key] = property.Value?.DeepClone();
                    if (hydrated["Type"]?.ToString() == "Playlist") new JellyfinItemFields(Request.Query).Apply(hydrated);
                    rows[index] = hydrated;
                }
            }
            return Content(page.ToJsonString(), "application/json");
        }
    }
}

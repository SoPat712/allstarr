using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Core.Capabilities;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Core.Protocols.Jellyfin;
using allstarr.Services.Jellyfin;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

public partial class JellyfinController
{
    private async Task<IActionResult?> TryReadSavedBrowseAsync(string? types = null, bool facets = false, bool latest = false)
    {
        if (!string.IsNullOrWhiteSpace(Request.Query["SearchTerm"]) || Request.Query.ContainsKey("Ids")) return null;
        var parent = Request.Query["ParentId"].ToString();
        if (parent.Length != 0 && parent != _settings.LibraryId) return null;
        string[] relations = ["AlbumIds", "ArtistIds", "AlbumArtistIds", "ContributingArtistIds"];
        if (parent.Length == 0 && relations.Any(key => JellyfinProviderQuery.Values(Request.Query[key].ToString())
            .Any(id => _localLibraryService.ParseExternalId(id).isExternal))) return null;
        var reader = HttpContext.RequestServices?.GetService<SavedLibraryReader>();
        if (reader == null) return null;
        var saved = await reader.ReadForRequestAsync(HttpContext);
        if (saved.Count == 0) return null;
        var favorites = _favoriteViews == null ? new HashSet<string>(StringComparer.Ordinal) :
            (await _favoriteViews.ReadForRequestAsync(HttpContext)).Select(item => item.ItemId).ToHashSet(StringComparer.Ordinal);
        var query = Request.Query.ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        if (types != null) query["IncludeItemTypes"] = types;
        if (!query.TryGetValue("IncludeItemTypes", out var requestedTypes) || string.IsNullOrWhiteSpace(requestedTypes))
            query["IncludeItemTypes"] = JellyfinMusicEndpointPolicy.DefaultMusicItemTypes;
        var genreQueries = await SavedGenreQueriesAsync(query, saved.SelectMany(item => item.Display.Genres));
        var providerQuery = new QueryCollection(genreQueries.Provider.ToDictionary(pair => pair.Key, pair => new Microsoft.Extensions.Primitives.StringValues(pair.Value), StringComparer.OrdinalIgnoreCase));
        var defaultLimit = latest ? 10 : Request.Path.Value!.StartsWith("/Artists", StringComparison.OrdinalIgnoreCase) ? 50 : 20;
        var filter = new JellyfinProviderQuery(providerQuery, defaultLimit: defaultLimit, membership: true);
        var additions = saved.Where(item => JellyfinFavoriteScope.Matches(item, providerQuery, _settings.LibraryId))
            .Select(item =>
            {
                var row = _responseBuilder.ConvertFavoriteToJellyfinItem(item);
                if (row.GetValueOrDefault("UserData") is IDictionary<string, object?> data) data["IsFavorite"] = favorites.Contains(item.ItemId);
                row["DateCreated"] = item.FavoritedAt.ToString("O");
                row["ParentId"] = item.Resource.ResourceKind == ProviderResourceKind.Track ? item.Display.AlbumId ?? _settings.LibraryId : _settings.LibraryId;
                if (item.Resource.ResourceKind == ProviderResourceKind.Album)
                    row["ChildCount"] = item.Display.Count ?? 0;
                row["Genres"] = item.Display.Genres;
                row["GenreItems"] = item.Display.Genres.Select(genre => new { Id = $"genre-{genre.ToLowerInvariant()}", Name = genre }).ToArray();
                return row;
            }).Where(filter.Matches).ToArray();
        if (additions.Length == 0) return null;
        if (HttpContext.RequestServices?.GetService<ProviderUserDataStore>() is { } userData)
        {
            var states = await userData.ReadForRequestAsync(HttpContext, additions.Select(row => row["Id"]!.ToString()!));
            foreach (var row in additions)
            {
                var data = JsonSerializer.SerializeToNode(row["UserData"])!.AsObject();
                MusicUserDataProjection.Jellyfin(data, states.GetValueOrDefault(row["Id"]!.ToString()!) ?? new());
                row["UserData"] = data;
            }
        }
        var genreList = facets && Request.Path.Value!.EndsWith("Genres", StringComparison.OrdinalIgnoreCase);
        var nativeQuery = genreQueries.Native;
        if ((!facets || genreList) && !latest) { nativeQuery["StartIndex"] = "0"; nativeQuery["Limit"] = "500"; }
        if (latest) nativeQuery["Limit"] = int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var endpoint = Request.Path.Value!.TrimStart('/');
        var headers = new HeaderDictionary(Request.Headers.Where(header =>
            !header.Key.Equals("If-None-Match", StringComparison.OrdinalIgnoreCase) &&
            !header.Key.Equals("If-Modified-Since", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(header => header.Key, header => header.Value, StringComparer.OrdinalIgnoreCase));
        var providerOnly = JellyfinFavoriteScope.RestrictNativeQuery(nativeQuery);
        var (native, status) = providerOnly
            ? (JsonSerializer.SerializeToDocument(new { Items = Array.Empty<object>(), TotalRecordCount = 0 }), 200)
            : await _proxyService.GetJsonAsync(endpoint, nativeQuery, headers);
        using (native)
        {
            if (native == null || status != 200) return HandleProxyResponse(native, status);
            var original = native.RootElement.GetRawText();
            if (facets && !genreList) return MergeSavedFacets(original, additions);
            var envelope = JsonNode.Parse(original) as JsonObject;
            var originalItems = latest ? JsonNode.Parse(original) as JsonArray : envelope?["Items"] as JsonArray;
            if (originalItems == null) return HandleProxyResponse(native, status);
            var rows = originalItems.OfType<JsonObject>().Select(item => item.Deserialize<Dictionary<string, object?>>() ?? []).ToList();
            var nativeText = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            void Remember(JsonElement element)
            {
                foreach (var item in element.EnumerateArray())
                    if (item.TryGetProperty("Id", out var id) && id.GetString() is { } value) nativeText[value] = item.GetRawText();
            }
            Remember(latest ? native.RootElement : native.RootElement.GetProperty("Items"));
            var totalNative = envelope?["TotalRecordCount"]?.GetValue<int>() ?? rows.Count;
            while (!latest && rows.Count < totalNative)
            {
                nativeQuery["StartIndex"] = rows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var (page, pageStatus) = await _proxyService.GetJsonAsync(endpoint, nativeQuery, headers);
                using (page)
                {
                    if (pageStatus != 200 || page == null || !page.RootElement.TryGetProperty("Items", out var items) ||
                        items.GetArrayLength() == 0 || page.RootElement.GetProperty("TotalRecordCount").GetInt32() != totalNative)
                        return HandleProxyResponse(native, status);
                    Remember(items);
                    rows.AddRange(items.EnumerateArray().Select(item => item.Deserialize<Dictionary<string, object?>>() ?? []));
                    if (rows.Count > totalNative) return HandleProxyResponse(native, status);
                }
            }
            if (!latest) HttpContext.Items[JellyfinQueryResponseRows.NativeRowsKey] = nativeText;
            if (genreList)
            {
                envelope!["Items"] = JsonSerializer.SerializeToNode(rows);
                return MergeSavedFacets(envelope.ToJsonString(), additions);
            }
            await ReadNativeAddedDatesAsync(rows, query, headers, latest);
            rows.AddRange(additions);
            var merged = filter.FilterAndSort(rows, latest ? "DateCreated" : null, nativeAlreadyFiltered: true);
            if (latest && !query.ContainsKey("SortBy")) merged = merged.OrderByDescending(row => JellyfinProviderQuery.Date(JellyfinProviderQuery.Text(row, "DateCreated"))).ToList();
            if (query.GetValueOrDefault("SortBy")?.Equals("Random", StringComparison.OrdinalIgnoreCase) == true)
                Random.Shared.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(merged));
            var pageRows = merged.Skip(filter.StartIndex).Take(filter.Limit).ToArray();
            foreach (var row in pageRows.Where(IsProviderQueryItem)) new JellyfinItemFields(Request.Query).Apply(row);
            Response.Headers.CacheControl = "no-store";
            if (latest)
            {
                using var output = new MemoryStream();
                using (var writer = new Utf8JsonWriter(output))
                {
                    writer.WriteStartArray();
                    foreach (var row in pageRows)
                    {
                        if (!IsProviderQueryItem(row) && nativeText.TryGetValue(JellyfinProviderQuery.Text(row, "Id"), out var raw))
                            writer.WriteRawValue(raw);
                        else JsonSerializer.Serialize(writer, row);
                    }
                    writer.WriteEndArray();
                }
                return Content(System.Text.Encoding.UTF8.GetString(output.ToArray()), "application/json");
            }
            envelope!["Items"] = JsonSerializer.SerializeToNode(pageRows);
            envelope["TotalRecordCount"] = merged.Count;
            envelope["StartIndex"] = filter.StartIndex;
            return Content(JellyfinQueryResponseRows.Preserve(envelope.ToJsonString(), HttpContext), "application/json");
        }
    }

    private IActionResult MergeSavedFacets(string original, IReadOnlyList<Dictionary<string, object?>> additions)
    {
        var root = JsonNode.Parse(original) as JsonObject;
        if (root == null) return Content(original, "application/json");
        var genres = additions.SelectMany(row => JsonSerializer.SerializeToElement(row).GetProperty("Genres").EnumerateArray().Select(value => value.GetString()!))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (Request.Path.Value!.EndsWith("Genres", StringComparison.OrdinalIgnoreCase))
        {
            if (root["Items"] is not JsonArray items) return Content(original, "application/json");
            var native = items.OfType<JsonObject>().Select(row => row["Name"]?.GetValue<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var genre in genres.Where(genre => !native.Contains(genre)))
                items.Add(new JsonObject { ["Id"] = $"genre-{genre.ToLowerInvariant()}", ["Name"] = genre, ["Type"] = "MusicGenre" });
            root["TotalRecordCount"] = items.Count;
            var paging = new JellyfinProviderQuery(Request.Query);
            var ordered = items.OfType<JsonObject>().OrderBy(item => item["SortName"]?.ToString() ?? item["Name"]?.ToString(), StringComparer.OrdinalIgnoreCase);
            IEnumerable<JsonObject> sorted = Request.Query["SortOrder"].ToString().Equals("Descending", StringComparison.OrdinalIgnoreCase) ? ordered.Reverse() : ordered;
            var page = sorted.Skip(paging.StartIndex).Take(paging.Limit).Select(item => item.DeepClone()).ToArray();
            root["Items"] = new JsonArray(page);
            root["StartIndex"] = paging.StartIndex;
        }
        else
        {
            var genreValues = root["Genres"] as JsonArray ?? new JsonArray();
            root["Genres"] = null;
            var detailed = Request.Path.Value.EndsWith("Filters2", StringComparison.OrdinalIgnoreCase);
            foreach (var genre in genres)
                if (!genreValues.Any(value => (value is JsonObject row ? row["Name"]?.GetValue<string>() : value?.GetValue<string>())?.Equals(genre, StringComparison.OrdinalIgnoreCase) == true))
                    genreValues.Add(detailed ? new JsonObject { ["Id"] = $"genre-{genre.ToLowerInvariant()}", ["Name"] = genre } : JsonValue.Create(genre));
            root["Genres"] = genreValues;
            var years = root["Years"] as JsonArray ?? new JsonArray();
            root["Years"] = null;
            foreach (var year in additions.Select(row => row.GetValueOrDefault("ProductionYear")).OfType<int>().Distinct().Order())
                if (!years.Any(value => value?.GetValue<int>() == year)) years.Add(year);
            root["Years"] = years;
        }
        Response.Headers.CacheControl = "no-store";
        var text = NativeItemText.PreserveJson(original, root.ToJsonString(), "Id");
        return Content(JellyfinQueryResponseRows.Preserve(text, HttpContext), "application/json");
    }
}

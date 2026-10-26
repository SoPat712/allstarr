using System.Globalization;
using System.Text.Json;

namespace allstarr.Services.Jellyfin;

internal sealed class JellyfinProviderQuery(IQueryCollection query, string? searchTerm = null, int defaultLimit = int.MaxValue, bool membership = false)
{
    public int StartIndex => int.TryParse(query["StartIndex"], out var value) ? Math.Max(0, value) : 0;
    public int Limit => int.TryParse(query["Limit"], out var value) ? Math.Max(0, value) : Math.Max(0, defaultLimit);

    public List<Dictionary<string, object?>> Apply(IEnumerable<Dictionary<string, object?>> source,
        out int total, string? defaultSort = null, bool deduplicate = true, bool nativeAlreadyFiltered = false,
        Func<IEnumerable<Dictionary<string, object?>>, IEnumerable<Dictionary<string, object?>>>? reconcile = null)
    {
        var rows = FilterAndSort(source, defaultSort, deduplicate, nativeAlreadyFiltered, reconcile);
        total = rows.Count;
        return rows.Skip(StartIndex).Take(Limit).ToList();
    }

    public List<Dictionary<string, object?>> FilterAndSort(IEnumerable<Dictionary<string, object?>> source,
        string? defaultSort = null, bool deduplicate = true, bool nativeAlreadyFiltered = false,
        Func<IEnumerable<Dictionary<string, object?>>, IEnumerable<Dictionary<string, object?>>>? reconcile = null)
    {
        var rows = source.Where(item => nativeAlreadyFiltered && !JellyfinItemIdComparer.IsExternalId(Text(item, "Id")) || Matches(item));
        if (reconcile != null) rows = reconcile(rows);
        if (deduplicate) rows = rows.DistinctBy(item => Text(item, "Id"), JellyfinItemIdComparer.Instance);
        var result = rows.ToList();
        var fields = Values(query["SortBy"].ToString());
        if (fields.Length == 0) fields = Values(defaultSort ?? "");
        if (fields.Any(field => SortKey(field).Length > 0))
        {
            var orders = Values(query["SortOrder"].ToString());
            result = result.OrderBy(item => item, Comparer<Dictionary<string, object?>>.Create((left, right) =>
            {
                for (var index = 0; index < fields.Length; index++)
                {
                    var comparison = Compare(left, right, fields[index]);
                    if (comparison == 0) continue;
                    var order = orders.Length == 0 ? "Ascending" : orders[index < orders.Length ? index : 0];
                    return order.Equals("Descending", StringComparison.OrdinalIgnoreCase) ? -comparison : comparison;
                }
                return JellyfinItemIdComparer.Instance.Compare(Text(left, "Id"), Text(right, "Id"));
            })).ToList();
        }
        return result;
    }

    public bool Matches(Dictionary<string, object?> item)
    {
        var root = JsonSerializer.SerializeToElement(item);
        string Read(string name) => Text(item, name);
        var search = (searchTerm ?? query["SearchTerm"].ToString()).Trim().Trim('"');
        if (search.Length > 0 && !new[] { Read("Name"), Read("Album"), Read("AlbumArtist") }
            .Concat(Strings(root, "Artists")).Any(value => value.Contains(search, StringComparison.OrdinalIgnoreCase))) return false;
        var name = Read("Name");
        if (!string.IsNullOrEmpty(query["NameStartsWith"]) && !name.StartsWith(query["NameStartsWith"].ToString(), StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrEmpty(query["NameStartsWithOrGreater"]) && StringComparer.OrdinalIgnoreCase.Compare(name, query["NameStartsWithOrGreater"].ToString()) < 0) return false;
        if (!string.IsNullOrEmpty(query["NameLessThan"]) && StringComparer.OrdinalIgnoreCase.Compare(name, query["NameLessThan"].ToString()) >= 0) return false;
        if (Values(query["ExcludeItemIds"].ToString()).Contains(Read("Id"), JellyfinItemIdComparer.Instance)) return false;
        if (!MatchAny(query["IncludeItemTypes"].ToString(), [Read("Type")])) return false;
        if (Values(query["ExcludeItemTypes"].ToString()).Contains(Read("Type"), StringComparer.OrdinalIgnoreCase)) return false;
        if (Values(query["ExcludeLocationTypes"].ToString()).Contains(Read("LocationType"), StringComparer.OrdinalIgnoreCase)) return false;
        var excludedArtists = Values(query["ExcludeArtistIds"].ToString());
        if (Objects(root, "ArtistItems").Concat(Objects(root, "AlbumArtists"))
            .Any(artist => excludedArtists.Contains(String(artist, "Id"), JellyfinItemIdComparer.Instance))) return false;
        if (!MatchAny(query["Genres"].ToString(), Strings(root, "Genres"), genres: true)) return false;
        var genreIds = Values(query["GenreIds"].ToString());
        if (genreIds.Length > 0 && !Objects(root, "GenreItems").Select(value => String(value, "Id"))
            .Any(value => genreIds.Contains(value, JellyfinItemIdComparer.Instance))) return false;
        if (!MatchAny(query["Years"].ToString(), [Read("ProductionYear")])) return false;
        if (int.TryParse(query["MinYear"], out var min) && (!int.TryParse(Read("ProductionYear"), out var minimumYear) || minimumYear < min)) return false;
        if (int.TryParse(query["MaxYear"], out var max) && (!int.TryParse(Read("ProductionYear"), out var maximumYear) || maximumYear > max)) return false;
        var userData = root.TryGetProperty("UserData", out var data) ? data : default;
        var favorite = String(userData, "IsFavorite").Equals("True", StringComparison.OrdinalIgnoreCase);
        if (bool.TryParse(query["IsFavorite"], out var wanted) && favorite != wanted) return false;
        if (Values(query["Filters"].ToString()).Contains("IsFavorite", StringComparer.OrdinalIgnoreCase) && !favorite) return false;
        return true;
    }

    private static bool MatchAny(string requested, IEnumerable<string> actual, bool genres = false)
    {
        var values = genres ? requested.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : Values(requested);
        return values.Length == 0 || actual.Any(value => values.Contains(value, StringComparer.OrdinalIgnoreCase));
    }

    internal static string[] Values(string value) => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    internal static string Text(Dictionary<string, object?> item, string key) => item.GetValueOrDefault(key)?.ToString() ?? "";
    private static string String(JsonElement item, string key) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value) ? value.ToString() : "";
    private static IEnumerable<JsonElement> Objects(JsonElement item, string key) => item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];
    private static IEnumerable<string> Strings(JsonElement item, string key) => Objects(item, key).Select(value => value.ToString());

    private string SortKey(string field) => field.ToLowerInvariant() switch
    {
        "sortname" => "SortName",
        "name" => "Name",
        "album" => "Album",
        "artist" => "Artists",
        "albumartist" => "AlbumArtist",
        "productionyear" => "ProductionYear",
        "premieredate" => "PremiereDate",
        "datecreated" => membership ? "DateCreated" : "",
        "playcount" => membership ? "PlayCount" : "",
        "dateplayed" => membership ? "LastPlayedDate" : "",
        "indexnumber" => "IndexNumber",
        "parentindexnumber" => "ParentIndexNumber",
        "runtime" => "RunTimeTicks",
        _ => ""
    };

    private int Compare(Dictionary<string, object?> left, Dictionary<string, object?> right, string field)
    {
        var key = SortKey(field);
        if (key.Length == 0) return 0;
        if (key == "PlayCount") return HistoryNumber(left).CompareTo(HistoryNumber(right));
        if (key == "LastPlayedDate") return Nullable.Compare(Date(HistoryText(left, key)), Date(HistoryText(right, key)));
        if (key == "DateCreated") return Nullable.Compare(Date(Text(left, key)), Date(Text(right, key)));
        if (key is "ProductionYear" or "IndexNumber" or "ParentIndexNumber" or "RunTimeTicks")
            return Nullable.Compare(Number(left, key), Number(right, key));
        if (key == "Artists")
            return StringComparer.OrdinalIgnoreCase.Compare(Artist(left), Artist(right));
        return StringComparer.OrdinalIgnoreCase.Compare(SortText(left, key), SortText(right, key));
    }

    internal static DateTimeOffset? Date(string value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal, out var date) ? date : null;

    private static string HistoryText(Dictionary<string, object?> item, string key)
    {
        var root = JsonSerializer.SerializeToElement(item);
        return root.TryGetProperty("UserData", out var data) ? String(data, key) : "";
    }

    private static long HistoryNumber(Dictionary<string, object?> item) => long.TryParse(HistoryText(item, "PlayCount"), out var count) ? count : 0;

    private static string SortText(Dictionary<string, object?> item, string key) => key == "SortName" && string.IsNullOrWhiteSpace(Text(item, key))
        ? Text(item, "Name") : Text(item, key);

    private static long? Number(Dictionary<string, object?> item, string key) => long.TryParse(Text(item, key), NumberStyles.Integer,
        CultureInfo.InvariantCulture, out var number) ? number : null;
    private static string Artist(Dictionary<string, object?> item) => string.Join(" ", Strings(JsonSerializer.SerializeToElement(item), "Artists"));
}

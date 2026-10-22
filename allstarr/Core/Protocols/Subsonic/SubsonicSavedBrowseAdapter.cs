using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using allstarr.Core.Capabilities;
using allstarr.Core.Favorites;
using allstarr.Services.Subsonic;

namespace allstarr.Core.Protocols.Subsonic;

public static class SubsonicSavedBrowseAdapter
{
    private static readonly HashSet<string> AlbumModes = new(StringComparer.Ordinal)
    { "newest", "alphabeticalByName", "alphabeticalByArtist", "byGenre", "byYear", "random", "starred" };

    public static int Number(SubsonicRequestParameters parameters, string name, int fallback, int maximum) =>
        int.TryParse(parameters.GetValueOrDefault(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, 0, maximum) : fallback;

    public static IReadOnlyList<FavoriteViewItem> Select(IReadOnlyList<FavoriteViewItem> saved, string container,
        SubsonicRequestParameters parameters)
    {
        var mode = parameters.GetValueOrDefault("type");
        if (container == "albumList2" && !AlbumModes.Contains(mode)) return [];
        var kind = container switch
        {
            "artists" or "indexes" => ProviderResourceKind.Artist,
            "albumList2" => ProviderResourceKind.Album,
            _ => ProviderResourceKind.Track
        };
        IEnumerable<FavoriteViewItem> result = container == "genres"
            ? saved.Where(item => (item.Resource.ResourceKind is ProviderResourceKind.Track or ProviderResourceKind.Album) && item.Display.Genres.Length != 0)
            : saved.Where(item => item.Resource.ResourceKind == kind);
        if (container == "songsByGenre" || (container == "albumList2" && mode == "byGenre") ||
            (container == "randomSongs" && parameters.HasNonEmptyValue("genre")))
        {
            var genre = parameters.GetValueOrDefault("genre");
            result = result.Where(item => item.Display.Genres.Contains(genre, StringComparer.OrdinalIgnoreCase));
        }
        if (container == "randomSongs" || (container == "albumList2" && mode == "byYear"))
        {
            var from = Number(parameters, "fromYear", 0, 9999);
            var to = Number(parameters, "toYear", 9999, 9999);
            if (parameters.HasNonEmptyValue("fromYear") || parameters.HasNonEmptyValue("toYear"))
                result = result.Where(item => item.Display.Year is { } year && year >= Math.Min(from, to) && year <= Math.Max(from, to));
        }
        return result.OrderByDescending(item => item.FavoritedAt).ThenBy(item => item.ItemId, StringComparer.Ordinal).ToArray();
    }

    public static int Count(SubsonicProxyResponse native, string container, string format)
    {
        try { return Document.Read(native, container, format)?.Rows(container == "albumList2" ? "album" : "song").Count ?? -1; }
        catch (Exception exception) when (Malformed(exception)) { return -1; }
    }

    public static SubsonicProxyResponse Merge(IReadOnlyList<SubsonicProxyResponse> pages,
        IReadOnlyList<FavoriteViewItem> saved, string container, string format, SubsonicRequestParameters parameters,
        SubsonicResponseBuilder builder, IReadOnlyDictionary<string, DateTimeOffset>? starred = null)
    {
        var native = pages[0];
        if (pages.Any(page => !page.IsSuccessStatusCode)) return pages.First(page => !page.IsSuccessStatusCode);
        try
        {
            var documents = pages.Select(page => Document.Read(page, container, format)).ToArray();
            if (documents.Any(page => page == null)) return native;
            var document = documents[0]!;
            if (container == "genres") MergeGenres(document, saved);
            else if (container is "artists" or "indexes") MergeArtists(document, saved, builder, starred);
            else
            {
                var element = container == "albumList2" ? "album" : "song";
                var rows = documents.SelectMany(page => page!.Rows(element)).ToList();
                var ids = rows.Select(row => row.Field("id")).ToHashSet(StringComparer.Ordinal);
                rows.AddRange(saved.Where(item => ids.Add(item.ItemId)).Select(item => SavedRow(item, document.Namespace, builder, starred != null && starred.TryGetValue(item.ItemId, out var date) ? date : null)));
                var ordered = Order(rows, container, parameters);
                var offset = container is "albumList2" or "songsByGenre" ? Number(parameters, "offset", 0, int.MaxValue) : 0;
                var size = Number(parameters, container == "songsByGenre" ? "count" : "size", 10, 500);
                document.Replace(element, ordered.Skip(offset).Take(size));
            }
            var body = document.Text();
            foreach (var page in pages)
                body = format.Equals("json", StringComparison.OrdinalIgnoreCase)
                    ? NativeItemText.PreserveJson(Encoding.UTF8.GetString(page.Body), body, "id")
                    : NativeItemText.PreserveXml(Encoding.UTF8.GetString(page.Body), body);
            return native with
            {
                Body = Encoding.UTF8.GetBytes(body),
                Headers = native.Headers.Where(header => !InvalidatedHeaders.Contains(header.Key))
                    .ToDictionary(header => header.Key, header => header.Value, StringComparer.OrdinalIgnoreCase)
            };
        }
        catch (Exception exception) when (Malformed(exception)) { return native; }
    }

    private static readonly HashSet<string> InvalidatedHeaders = new(StringComparer.OrdinalIgnoreCase)
    { "ETag", "Cache-Control", "Expires", "Content-MD5", "Digest", "Content-Encoding", "Last-Modified", "Content-Length" };

    private static bool Malformed(Exception exception) => exception is JsonException or System.Xml.XmlException or InvalidOperationException;

    private static Row SavedRow(FavoriteViewItem item, XNamespace ns, SubsonicResponseBuilder builder, DateTimeOffset? starred = null)
    {
        var json = JsonSerializer.SerializeToNode(builder.ConvertFavoriteToJson(item))!.AsObject();
        var xml = builder.ConvertFavoriteToXml(item, ns);
        json.Remove("starred");
        xml.SetAttributeValue("starred", null);
        var created = item.FavoritedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        json["created"] = created;
        xml.SetAttributeValue("created", created);
        if (starred is { } favoriteDate)
        {
            var timestamp = favoriteDate.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
            json["starred"] = timestamp;
            xml.SetAttributeValue("starred", timestamp);
        }
        return new(json, xml);
    }

    private static IEnumerable<Row> Order(IEnumerable<Row> rows, string container, SubsonicRequestParameters parameters)
    {
        var mode = container == "randomSongs" ? "random" : parameters.GetValueOrDefault("type");
        var comparer = StringComparer.OrdinalIgnoreCase;
        IOrderedEnumerable<Row>? ordered = mode switch
        {
            "alphabeticalByName" => rows.OrderBy(row => row.Title, comparer),
            "alphabeticalByArtist" => rows.OrderBy(row => row.Field("artist"), comparer).ThenBy(row => row.Title, comparer),
            "newest" => rows.OrderByDescending(row => Date(row.Field("created"))),
            "starred" => rows.OrderByDescending(row => Date(row.Field("starred"))),
            "byGenre" => rows.OrderBy(row => row.Title, comparer),
            "byYear" => Number(parameters, "fromYear", 0, 9999) > Number(parameters, "toYear", 9999, 9999)
                ? rows.OrderByDescending(row => row.Integer("year")) : rows.OrderBy(row => row.Integer("year")),
            "random" => rows.OrderBy(_ => Random.Shared.NextDouble()),
            _ => null
        };
        return ordered?.ThenBy(row => row.Field("id"), StringComparer.Ordinal) ?? rows;
    }

    private static DateTimeOffset Date(string? value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal, out var date) ? date : DateTimeOffset.MinValue;

    private static void MergeArtists(Document document, IReadOnlyList<FavoriteViewItem> saved, SubsonicResponseBuilder builder, IReadOnlyDictionary<string, DateTimeOffset>? starred)
    {
        var indexes = document.Rows("index");
        var ids = indexes.SelectMany(index => index.Children("artist")).Select(row => row.Field("id")).ToHashSet(StringComparer.Ordinal);
        foreach (var group in saved.Where(item => ids.Add(item.ItemId)).GroupBy(item => item.Display.Title[..1].ToUpperInvariant()))
        {
            var index = indexes.FirstOrDefault(row => row.Field("name") == group.Key);
            if (index == null)
            {
                index = new(new JsonObject { ["name"] = group.Key }, new XElement(document.Namespace + "index", new XAttribute("name", group.Key)));
                indexes.Add(index);
            }
            var artists = index.Children("artist").Concat(group.Select(item => SavedRow(item, document.Namespace, builder, starred != null && starred.TryGetValue(item.ItemId, out var date) ? date : null)))
                .OrderBy(row => row.Field("name"), StringComparer.OrdinalIgnoreCase).ThenBy(row => row.Field("id"), StringComparer.Ordinal).ToArray();
            index.Replace("artist", artists);
        }
        document.Replace("index", indexes.OrderBy(row => row.Field("name"), StringComparer.OrdinalIgnoreCase));
    }

    private static void MergeGenres(Document document, IReadOnlyList<FavoriteViewItem> saved)
    {
        var rows = document.Rows("genre");
        foreach (var group in saved.SelectMany(item => item.Display.Genres.Distinct(StringComparer.OrdinalIgnoreCase)
                     .Select(genre => (Genre: genre, Item: item))).GroupBy(item => item.Genre, StringComparer.OrdinalIgnoreCase))
        {
            var row = rows.FirstOrDefault(item => item.Field("value")?.Equals(group.Key, StringComparison.OrdinalIgnoreCase) == true);
            if (row == null)
            {
                row = new(new JsonObject { ["value"] = group.Key }, new XElement(document.Namespace + "genre", group.Key));
                rows.Add(row);
            }
            row.SetNumber("songCount", row.Integer("songCount") + group.Count(item => item.Item.Resource.ResourceKind == ProviderResourceKind.Track));
            row.SetNumber("albumCount", row.Integer("albumCount") + group.Count(item => item.Item.Resource.ResourceKind == ProviderResourceKind.Album));
        }
        document.Replace("genre", rows.OrderBy(row => row.Field("value"), StringComparer.OrdinalIgnoreCase));
    }

    private sealed class Row(JsonObject? json, XElement? xml)
    {
        public JsonObject? Json { get; } = json;
        public XElement? Xml { get; } = xml;
        public string? Field(string name) => Json != null ? Json[name]?.ToString()
            : name == "value" && Xml?.Name.LocalName == "genre" ? Xml.Value : Xml?.Attribute(name)?.Value;
        public string? Title => Field("name") ?? Field("title");
        public int Integer(string name) => int.TryParse(Field(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
        public void SetNumber(string name, int value)
        {
            if (Json != null) Json[name] = value;
            Xml?.SetAttributeValue(name, value);
        }
        public List<Row> Children(string element)
        {
            if (Json == null) return Xml!.Elements(Xml.Name.Namespace + element).Select(item => new Row(null, item)).ToList();
            if (Json[element] == null) return [];
            if (Json[element] is not JsonArray children || children.Any(item => item is not JsonObject))
                throw new InvalidOperationException("The native browse collection is malformed.");
            return children.Cast<JsonObject>().Select(item => new Row(item, null)).ToList();
        }
        public void Replace(string element, IEnumerable<Row> rows)
        {
            var values = rows.ToArray();
            if (Json != null) Json[element] = new JsonArray(values.Select(row => (JsonNode?)row.Json!.DeepClone()).ToArray());
            if (Xml != null)
            {
                Xml.Elements(Xml.Name.Namespace + element).Remove();
                Xml.Add(values.Select(row => new XElement(row.Xml!)));
            }
        }
    }

    private sealed class Document(JsonObject? json, XDocument? xml, Row list)
    {
        public XNamespace Namespace => xml?.Root?.Name.Namespace ?? XNamespace.Get("http://subsonic.org/restapi");
        public List<Row> Rows(string element) => list.Children(element);
        public void Replace(string element, IEnumerable<Row> rows) => list.Replace(element, rows);
        public string Text() => json?.ToJsonString() ?? xml!.ToString(SaveOptions.DisableFormatting);
        public static Document? Read(SubsonicProxyResponse response, string container, string format)
        {
            if (response.Body.Length == 0) return null;
            if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
            {
                if (JsonNode.Parse(response.Body) is not JsonObject json || json["subsonic-response"] is not JsonObject root ||
                    root["status"]?.ToString() != "ok" || root[container] is not JsonObject list) return null;
                return new(json, null, new(list, null));
            }
            var xml = XDocument.Parse(Encoding.UTF8.GetString(response.Body), LoadOptions.PreserveWhitespace);
            if (xml.Root is not { } xmlRoot || xmlRoot.Name.LocalName != "subsonic-response" ||
                xmlRoot.Attribute("status")?.Value != "ok" || xmlRoot.Element(xmlRoot.Name.Namespace + container) is not { } xmlList) return null;
            return new(null, xml, new(null, xmlList));
        }
    }
}

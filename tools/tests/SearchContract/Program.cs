using System.Text.Json;
using System.Text.RegularExpressions;
using allstarr.Services.Common;

// Offline observer of captured protocol responses; execute the production scorer directly.
try
{
    return CheckCapture();
}
catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
{
    Console.WriteLine(JsonSerializer.Serialize(new { scores = Array.Empty<double>(), localPreferenceChecks = 0, errors = new[] { "invalid-capture" } }));
    return 1;
}

static int CheckCapture()
{
    using var capture = JsonDocument.Parse(Console.In.ReadToEnd());
    var root = capture.RootElement;
    var query = (root.GetProperty("query").GetString() ?? "").Trim().Trim('"');
    if (string.IsNullOrWhiteSpace(query)) throw new InvalidOperationException();
    var page = ReadItems(root.GetProperty("page"), complete: false);
    var local = ReadItems(root.GetProperty("local"), complete: true);
    if (local.Any(item => Text(item, "Id").StartsWith("ext-", StringComparison.Ordinal)) ||
        local.Select(item => Text(item, "Id")).Distinct(StringComparer.OrdinalIgnoreCase).Count() != local.Length)
        throw new InvalidOperationException();
    var errors = new List<string>();
    var scores = new List<double>();
    var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var localPreferenceChecks = 0;
    foreach (var item in page)
    {
        var id = Text(item, "Id");
        if (id.Length == 0 || Text(item, "Type") != "Audio") errors.Add("invalid-audio-item");
        var external = id.StartsWith("ext-", StringComparison.Ordinal);
        var title = Text(item, "Name");
        string artist;
        if (external)
        {
            // The synthetic path retains the exact primary artist, unlike the multi-credit Artists list.
            var suffix = $"/{Text(item, "Album")}/{title}.flac";
            var path = Text(item, "Path");
            if (!path.StartsWith("/music/", StringComparison.Ordinal) || !path.EndsWith(suffix, StringComparison.Ordinal) ||
                path.Length < 7 + suffix.Length)
            {
                errors.Add("missing-source-metadata");
                artist = "";
            }
            else artist = Regex.Replace(path[7..^suffix.Length], @" \[[^\[\]]+\]$", "");
            title = Regex.Replace(title, @" \[(A|E)\](/\[E\])?$", "");
        }
        else
        {
            artist = item.TryGetProperty("Artists", out var artists) && artists.GetArrayLength() > 0
                ? string.Join(" ", artists.EnumerateArray().Take(3).Select(value => value.GetString()))
                : Text(item, "AlbumArtist") is { Length: > 0 } albumArtist ? albumArtist : Text(item, "Artist");
        }
        scores.Add(SearchRelevanceRanker.Score(query, title, artist));
        var keys = Keys(item);
        if (keys.Any(identities.Contains)) errors.Add("duplicate-recording");
        identities.UnionWith(keys);
        var nativeCopies = local.Where(native => Keys(native).Overlaps(keys)).ToArray();
        if (nativeCopies.Length > 0)
        {
            localPreferenceChecks++;
            if (external || !nativeCopies.Any(native => Text(native, "Id") == id)) errors.Add("local-copy-not-preferred");
        }
    }
    if (page.Length == 0) errors.Add("empty-first-page");
    if (scores.Zip(scores.Skip(1)).Any(pair => pair.First < pair.Second)) errors.Add("relevance-order");
    Console.WriteLine(JsonSerializer.Serialize(new { scores, localPreferenceChecks, errors = errors.Distinct().ToArray() }));
    return errors.Count == 0 ? 0 : 1;
}

static JsonElement[] ReadItems(JsonElement envelope, bool complete)
{
    var items = envelope.GetProperty("Items").EnumerateArray().ToArray();
    if (envelope.GetProperty("StartIndex").GetInt32() != 0 ||
        !envelope.GetProperty("TotalRecordCount").TryGetInt32(out var total) ||
        total < items.Length || (complete && total != items.Length))
        throw new InvalidOperationException();
    foreach (var item in items)
    {
        if (string.IsNullOrWhiteSpace(Text(item, "Id")) || string.IsNullOrWhiteSpace(Text(item, "Name")) ||
            Text(item, "Type") != "Audio" || item.GetProperty("ProviderIds").ValueKind != JsonValueKind.Object ||
            item.GetProperty("ProviderIds").EnumerateObject().Any(property => property.Value.ValueKind != JsonValueKind.String) ||
            (item.TryGetProperty("Artists", out var artists) &&
                (artists.ValueKind != JsonValueKind.Array || artists.EnumerateArray().Any(artist => artist.ValueKind != JsonValueKind.String))))
            throw new InvalidOperationException();
    }
    return items;
}

static string Text(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
    ? value.GetString() ?? "" : "";

static HashSet<string> Keys(JsonElement item)
{
    var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id:" + Text(item, "Id") };
    if (!item.TryGetProperty("ProviderIds", out var ids)) return keys;
    foreach (var property in ids.EnumerateObject())
    {
        if (property.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.Value.GetString())) continue;
        var kind = property.Name.ToLowerInvariant();
        // Album/artist identifiers and presentation provenance are not recording identities.
        if (kind.Contains("album") || kind.Contains("artist") || kind.Contains("release") || kind == "allstarrsource") continue;
        if (kind is "musicbrainztrack" or "musicbrainzrecording") kind = "mbid";
        var value = property.Value.GetString()!;
        if (kind == "isrc") value = value.Replace("-", "", StringComparison.Ordinal).ToUpperInvariant();
        keys.Add(kind + ":" + value);
    }
    return keys;
}

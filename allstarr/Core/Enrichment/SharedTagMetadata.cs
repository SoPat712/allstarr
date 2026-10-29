using System.Text.Json;
using allstarr.Services.Local;
using allstarr.Core.Providers.AppleMusicKit;

namespace allstarr.Core.Enrichment;

internal sealed record SharedTagMetadata(string Title, string Artist, IReadOnlyList<string> ArtistKeys,
    IReadOnlyList<SharedTagBatch> Batches, IReadOnlyList<string> ArtistNames, string? Isrc)
{
    internal static JsonElement Property(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object
        ? root.EnumerateObject().FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value : default;
    internal static string? Text(JsonElement root, string name) => Property(root, name) is { ValueKind: JsonValueKind.String } value
        ? value.GetString() : null;

    public static SharedTagMetadata Read(string provider, string json, DateTimeOffset fetchedAt)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var genres = new List<string>();
        if (Text(root, "genre") is { Length: > 0 } genre) genres.Add(genre);
        if (Property(root, "genres") is { ValueKind: JsonValueKind.Array } values)
            genres.AddRange(values.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString()!));
        var artist = Text(root, "artist");
        var names = new List<string>();
        if (!string.IsNullOrWhiteSpace(artist)) names.Add(artist);
        var keys = new List<string>();
        if (Text(root, "artistId") is { Length: > 0 } artistId) keys.Add(ArtistIdentity(provider, artistId));
        if (Property(root, "artistIds") is { ValueKind: JsonValueKind.Array } artistIds)
            keys.AddRange(artistIds.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => ArtistIdentity(provider, value.GetString()!)));
        if (Property(root, "artists") is { ValueKind: JsonValueKind.Array } credits)
            foreach (var credit in credits.EnumerateArray())
            {
                var name = credit.ValueKind == JsonValueKind.String ? credit.GetString() : Text(credit, "name");
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
                var id = Property(credit, "artistId");
                if (Text(id, "value") is { Length: > 0 } value)
                    keys.Add(ArtistIdentity(Text(id, "providerId") ?? provider, value));
            }
        var source = provider == "apple-musickit" ? "apple" : provider;
        return new(Text(root, "title") ?? "", names.FirstOrDefault() ?? "", keys.Where(key => key.Length > 0).Distinct().ToArray(),
            [new(source, genres.Distinct(StringComparer.OrdinalIgnoreCase).Select(label => new SharedTagInput(label, IsGenre: true)).ToArray(), fetchedAt)], names, Text(root, "isrc"));
    }

    internal static string ArtistIdentity(string provider, string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";
        var parsed = LocalLibraryService.ParseExternalResource(id);
        provider = parsed.isExternal ? parsed.provider! : provider;
        var value = parsed.isExternal ? parsed.externalId! : id;
        if (value.StartsWith("credit:", StringComparison.Ordinal) ||
            provider.StartsWith("apple", StringComparison.Ordinal) && AppleMusicKitMetadataCapabilityAdapter.IsLibrary(value)) return "";
        if (provider is "apple" or "apple-musickit") provider = "apple-download";
        return $"ext-{provider}-artist-{value}";
    }
}

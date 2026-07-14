using System.Text.Json;

namespace allstarr.Core.Protocols.Jellyfin;

public interface IJellyfinSearchProtocolAdapter
{
    JellyfinProtocolResponse ShapeItemsResponse(
        IReadOnlyList<Dictionary<string, object?>> items,
        int startIndex,
        int limit);
}

public sealed record JellyfinProtocolResponse(
    int StatusCode,
    string ContentType,
    string Body);

public sealed class JellyfinSearchProtocolAdapter : IJellyfinSearchProtocolAdapter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = null,
        DictionaryKeyPolicy = null
    };

    public JellyfinProtocolResponse ShapeItemsResponse(
        IReadOnlyList<Dictionary<string, object?>> items,
        int startIndex,
        int limit)
    {
        ArgumentNullException.ThrowIfNull(items);

        var response = new JellyfinSearchItemsResponse(
            items.Skip(startIndex).Take(limit).ToList(),
            items.Count,
            startIndex);

        return new JellyfinProtocolResponse(
            StatusCodes.Status200OK,
            "application/json",
            JsonSerializer.Serialize(response, SerializerOptions));
    }

    private sealed record JellyfinSearchItemsResponse(
        IReadOnlyList<Dictionary<string, object?>> Items,
        int TotalRecordCount,
        int StartIndex);
}

using System.Text.Json;

namespace allstarr.Core.Protocols.Jellyfin;

public interface IJellyfinInteractionProtocolAdapter
{
    bool CanRunOptionalUserWork(ProtocolExecutionContext? context);

    JellyfinProtocolResponse ShapeFavorite(string itemId, bool isFavorite);

    int ShapeCapabilitiesStatus(int upstreamStatusCode);

    JellyfinProtocolResponse ShapeInstantMix(IReadOnlyList<Dictionary<string, object?>> items);
}

public sealed class JellyfinInteractionProtocolAdapter : IJellyfinInteractionProtocolAdapter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = null,
        DictionaryKeyPolicy = null
    };

    public bool CanRunOptionalUserWork(ProtocolExecutionContext? context) =>
        context is { Protocol: ProtocolKind.Jellyfin, CanRunUserScopedWork: true };

    public JellyfinProtocolResponse ShapeFavorite(string itemId, bool isFavorite)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        return new JellyfinProtocolResponse(
            StatusCodes.Status200OK,
            "application/json",
            JsonSerializer.Serialize(new JellyfinFavoriteResponse(isFavorite, itemId, itemId), SerializerOptions));
    }

    public int ShapeCapabilitiesStatus(int upstreamStatusCode) => upstreamStatusCode switch
    {
        StatusCodes.Status200OK => StatusCodes.Status204NoContent,
        StatusCodes.Status204NoContent => StatusCodes.Status204NoContent,
        _ => upstreamStatusCode
    };

    public JellyfinProtocolResponse ShapeInstantMix(IReadOnlyList<Dictionary<string, object?>> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return new JellyfinProtocolResponse(
            StatusCodes.Status200OK,
            "application/json",
            JsonSerializer.Serialize(
                new JellyfinInstantMixResponse(items, items.Count, 0),
                SerializerOptions));
    }

    private sealed record JellyfinFavoriteResponse(bool IsFavorite, string ItemId, string Key);

    private sealed record JellyfinInstantMixResponse(
        IReadOnlyList<Dictionary<string, object?>> Items,
        int TotalRecordCount,
        int StartIndex);
}

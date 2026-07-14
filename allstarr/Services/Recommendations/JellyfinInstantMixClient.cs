using allstarr.Core.Intelligence;
using allstarr.Services.Jellyfin;

namespace allstarr.Services.Recommendations;

public sealed class JellyfinInstantMixClient(JellyfinProxyService proxy) : IJellyfinInstantMixClient
{
    public async Task<IReadOnlyList<RecommendationSourceItem>> GetInstantMixAsync(
        ScopedRecommendationQuery query, CancellationToken cancellationToken)
    {
        if (query.Scope.Protocol != "jellyfin") throw new NotSupportedException("InstantMix requires a Jellyfin scope.");
        var seed = query.SeedTrackKeys.Select(Normalize).FirstOrDefault(value => value.Length > 0)
            ?? throw new NotSupportedException("InstantMix requires a backend seed item.");
        var (body, status) = await proxy.GetJsonAsync($"Items/{Uri.EscapeDataString(seed)}/InstantMix",
            new() { ["Limit"] = query.Limit.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        using (body)
        {
            if (status is 401 or 403) throw new UnauthorizedAccessException();
            if (status is 404 or 405 or 501) throw new NotSupportedException();
            if (status < 200 || status >= 300 || body == null) throw new HttpRequestException("Jellyfin InstantMix failed.");
            if (!body.RootElement.TryGetProperty("Items", out var items) || items.ValueKind != System.Text.Json.JsonValueKind.Array) return [];
            return items.EnumerateArray().Select(item => item.TryGetProperty("Id", out var id) ? id.GetString() : null)
                .Where(id => !string.IsNullOrWhiteSpace(id) && id != seed).Distinct(StringComparer.Ordinal).Take(query.Limit)
                .Select((id, index) => new RecommendationSourceItem(id!, Math.Max(.25, 1d - index / (double)Math.Max(1, query.Limit)),
                    [new("jellyfin-instant-mix", .8, "Jellyfin included this item in the seed's InstantMix.")],
                    new("jellyfin", BackendItemId: id))).ToArray();
        }
    }
    private static string Normalize(string value) => value.StartsWith("backend:", StringComparison.Ordinal) ? value[8..] : value;
}

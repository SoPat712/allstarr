using System.Globalization;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Playlists;

namespace allstarr.Core.Providers.AppleMusicKit;

public sealed partial class AppleMusicKitPlaylistCapabilityAdapter
{
    public Task<ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>> GetServiceFeedsAsync(
        ProviderExecutionContext context, ProviderPageRequest page) => ExecuteAsync(context, async (credential, _, ct) =>
    {
        if (!TryOffset(page.Cursor, out var offset)) return FailurePage();
        var pending = new Queue<string>(["v1/me/recommendations?include=contents&limit=25", "v1/me/library/playlists?limit=25"]);
        var scheduled = new HashSet<string>(pending, StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var feeds = new Dictionary<string, ProviderPlaylistSummary>(StringComparer.Ordinal);
        try
        {
            while (pending.TryDequeue(out var relative))
            {
                visited.Add(relative);
                var result = await SendAsync(credential, relative, ct);
                if (!result.Outcome.IsSuccess)
                    return ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Failure(result.Outcome.Error!);
                using var document = JsonDocument.Parse(result.Body!);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                    return FailurePage();
                var continuations = new List<string>();
                CollectServiceFeeds(root, result.ETag, feeds, continuations);
                foreach (var next in continuations)
                {
                    if (!TryFeedContinuation(next, out var continuation) || visited.Contains(continuation))
                        return FailurePage();
                    if (scheduled.Add(continuation))
                    {
                        if (scheduled.Count > 100) return FailurePage();
                        pending.Enqueue(continuation);
                    }
                }
            }
            var items = feeds.Values.OrderBy(item => item.Id.Value, StringComparer.Ordinal).Skip(offset).Take(page.Limit).ToArray();
            var nextCursor = (long)offset + items.Length < feeds.Count
                ? (offset + items.Length).ToString(CultureInfo.InvariantCulture) : null;
            return ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Success(new(StableProviderId, items, nextCursor, nextCursor != null));
        }
        catch (JsonException) { return FailurePage(); }
    });

    private static void CollectServiceFeeds(JsonElement value, string? etag,
        Dictionary<string, ProviderPlaylistSummary> feeds, List<string> continuations, int depth = 0)
    {
        if (depth > 16) throw new JsonException();
        if (value.ValueKind != JsonValueKind.Object) return;
        if (String(value, "type") is "playlists" or "library-playlists")
        {
            var id = String(value, "id");
            var curator = String(Object(value, "attributes"), "curatorName");
            if (id != null && PlaylistSourceKinds.Classify(StableProviderId, id, curator, null) == PlaylistSourceKinds.Feed)
                feeds.TryAdd(id, MapSummary(value, etag));
            return;
        }
        foreach (var item in Data(value)) CollectServiceFeeds(item, etag, feeds, continuations, depth + 1);
        if (String(value, "next") is { Length: > 0 } next) continuations.Add(next);
        if (value.TryGetProperty("included", out var included) && included.ValueKind == JsonValueKind.Array)
            foreach (var item in included.EnumerateArray()) CollectServiceFeeds(item, etag, feeds, continuations, depth + 1);
        var relationships = Object(value, "relationships");
        foreach (var name in new[] { "contents", "recommendations", "playlists" })
            CollectServiceFeeds(Object(relationships, name), etag, feeds, continuations, depth + 1);
    }

    private static bool TryFeedContinuation(string next, out string relative)
    {
        relative = "";
        if (!Uri.TryCreate(ApiOrigin, next, out var uri) || uri.Scheme != ApiOrigin.Scheme || uri.Host != ApiOrigin.Host ||
            uri.Port != ApiOrigin.Port || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            !(uri.AbsolutePath is "/v1/me/recommendations" or "/v1/me/library/playlists" ||
              uri.AbsolutePath.StartsWith("/v1/me/recommendations/", StringComparison.Ordinal))) return false;
        relative = uri.PathAndQuery.TrimStart('/');
        return true;
    }
}

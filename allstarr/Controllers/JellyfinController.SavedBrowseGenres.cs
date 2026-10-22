using System.Text.Json;
using allstarr.Core.Protocols;
using allstarr.Services.Jellyfin;

namespace allstarr.Controllers;

public partial class JellyfinController
{
    private async Task<(Dictionary<string, string> Provider, Dictionary<string, string> Native)> SavedGenreQueriesAsync(
        Dictionary<string, string> query, IEnumerable<string> genres)
    {
        var provider = new Dictionary<string, string>(query, StringComparer.OrdinalIgnoreCase);
        var native = new Dictionary<string, string>(query, StringComparer.OrdinalIgnoreCase);
        var ids = JellyfinProviderQuery.Values(query.GetValueOrDefault("GenreIds") ?? "");
        if (ids.Length == 0) return (provider, native);
        var known = genres.Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(genre => $"genre-{genre.ToLowerInvariant()}", StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var credentials = query.Where(pair => pair.Key.Equals("ApiKey", StringComparison.OrdinalIgnoreCase) ||
            pair.Key.Equals("api_key", StringComparison.OrdinalIgnoreCase) || pair.Key.Equals("access_token", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var user = HttpContext.RequireProtocolExecutionContext().VerifiedBackendPrincipalId;
        foreach (var id in ids)
        {
            if (id.StartsWith("genre-", StringComparison.OrdinalIgnoreCase))
            {
                names[id] = known.GetValueOrDefault(id) ?? id[6..];
                continue;
            }
            var (item, status) = await _proxyService.GetJsonAsync(
                $"Users/{Uri.EscapeDataString(user)}/Items/{Uri.EscapeDataString(id)}", credentials, Request.Headers, HttpContext.RequestAborted);
            using (item)
            {
                if (status == 200 && item?.RootElement is { ValueKind: JsonValueKind.Object } root &&
                    root.TryGetProperty("Type", out var type) && type.GetString() == "MusicGenre" &&
                    root.TryGetProperty("Name", out var name) && name.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(name.GetString())) names[id] = name.GetString()!;
            }
        }
        provider["GenreIds"] = string.Join(',', ids.Select(id => names.TryGetValue(id, out var name)
            ? $"genre-{name.ToLowerInvariant()}" : id));
        if (ids.Any(id => id.StartsWith("genre-", StringComparison.OrdinalIgnoreCase)) && names.Count != 0)
        {
            IEnumerable<string> selected = names.Values.Distinct(StringComparer.OrdinalIgnoreCase);
            var requested = query.GetValueOrDefault("Genres")?.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
            if (requested.Length != 0) selected = selected.Intersect(requested, StringComparer.OrdinalIgnoreCase);
            var values = selected.ToArray();
            if (values.Length != 0)
            {
                native.Remove("GenreIds");
                native["Genres"] = string.Join('|', values);
            }
        }
        return (provider, native);
    }
}

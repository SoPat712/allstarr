using System.Text.Json.Nodes;
using allstarr.Core.Music;
using allstarr.Core.Settings;
using allstarr.Models.Domain;
using allstarr.Services.Jellyfin;
using allstarr.Services.Local;
using allstarr.Services.Subsonic;

namespace allstarr.Core.Protocols;

public sealed class CleanOnlyProtocolContent(IEffectiveProviderPolicyResolver policies,
    JellyfinProxyService jellyfin, IProtocolProviderGateway providers)
{
    public const string UnavailableMessage = "Only verified clean recordings are available for this listener.";

    public async Task<bool> IsActiveAsync(ProtocolExecutionContext context) =>
        (context.Actor?.EffectiveUserId is { } user
            ? await policies.ResolveForUserAsync(user, context.CancellationToken)
            : context.Policy ?? await policies.ResolveAsync(context.CancellationToken)).Preferences.ExplicitFilter == "CleanOnly";

    public async Task<bool> AllowsAsync(HttpContext http, string id)
    {
        var context = http.GetProtocolExecutionContext();
        if (context == null) return (await policies.ResolveAsync(http.RequestAborted)).Preferences.ExplicitFilter != "CleanOnly";
        if (!await IsActiveAsync(context)) return true;
        var parsed = LocalLibraryService.ParseExternalResource(id);
        if (parsed.isExternal)
        {
            if (parsed.type != "song") return false;
            var song = await providers.GetSongAsync(context, parsed.provider!, parsed.externalId!);
            return song != null && CleanRecordingPolicy.IsVerifiedClean(song);
        }
        try
        {
            JsonNode? item;
            if (context.Protocol == ProtocolKind.Jellyfin)
            {
                var (body, status) = await jellyfin.GetJsonAsync(
                    $"Users/{Uri.EscapeDataString(context.VerifiedBackendPrincipalId)}/Items/{Uri.EscapeDataString(id)}",
                    queryParams: JellyfinCredentials(http), clientHeaders: http.Request.Headers, cancellationToken: http.RequestAborted);
                using (body)
                    item = status == 200 && body != null ? JsonNode.Parse(body.RootElement.GetRawText()) : null;
            }
            else
            {
                var parameters = http.Items.TryGetValue(allstarr.Filters.SubsonicAuthFilter.RequestParametersItemKey, out var value) &&
                    value is SubsonicRequestParameters supplied ? supplied : SubsonicRequestParameters.FromDictionary(
                        http.Request.Query.ToDictionary(pair => pair.Key, pair => pair.Value.ToString()));
                var response = await http.RequestServices.GetRequiredService<SubsonicProxyService>().RelayRawAsync("rest/getSong.view", parameters.SetValue("id", id).SetValue("f", "json"), http.RequestAborted);
                var envelope = response.IsSuccessStatusCode ? JsonNode.Parse(response.Body)?["subsonic-response"] as JsonObject : null;
                item = envelope != null && Text(envelope, "status") == "ok" ? envelope["song"] : null;
            }
            return item is JsonObject row && Text(row, context.Protocol == ProtocolKind.Jellyfin ? "Id" : "id") == id &&
                (context.Protocol != ProtocolKind.Jellyfin || Text(row, "Type") == "Audio") && Rating(row, context.Protocol) is 0 or 3;
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or HttpRequestException) { return false; }
    }

    public async Task<bool> IsPlaylistAsync(HttpContext http)
    {
        if (http.Request.Path.Value?.Contains("playlist", StringComparison.OrdinalIgnoreCase) == true) return true;
        var parent = http.Request.Query["ParentId"].ToString();
        if (parent.Length == 0 || http.GetProtocolExecutionContext() is not { Protocol: ProtocolKind.Jellyfin } context) return false;
        const string key = "allstarr.clean-only.playlist-parent";
        if (http.Items.TryGetValue(key, out var known)) return known is true;
        var (body, status) = await jellyfin.GetJsonAsync(
            $"Users/{Uri.EscapeDataString(context.VerifiedBackendPrincipalId)}/Items/{Uri.EscapeDataString(parent)}",
            queryParams: JellyfinCredentials(http), clientHeaders: http.Request.Headers, cancellationToken: http.RequestAborted);
        using (body)
        {
            var playlist = status == 200 && body != null && body.RootElement.TryGetProperty("Type", out var type) && type.GetString() == "Playlist";
            http.Items[key] = playlist;
            return playlist;
        }
    }

    private static Dictionary<string, string> JellyfinCredentials(HttpContext http) => http.Request.Query
        .Where(pair => pair.Key.Equals("api_key", StringComparison.OrdinalIgnoreCase) ||
            pair.Key.Equals("ApiKey", StringComparison.OrdinalIgnoreCase) || pair.Key.Equals("access_token", StringComparison.OrdinalIgnoreCase))
        .ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.OrdinalIgnoreCase);

    internal static int? Rating(JsonObject row, ProtocolKind protocol)
    {
        var value = Text(row, protocol == ProtocolKind.Jellyfin ? "OfficialRating" : "explicitStatus").ToLowerInvariant();
        return value switch { "clean" or "notexplicit" or "not explicit" => 0, "edited" => 3, "explicit" => 1, _ => null };
    }

    internal static string Text(JsonObject row, string key) => row[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    internal static Song Song(JsonObject row, ProtocolKind protocol) => protocol == ProtocolKind.Jellyfin
        ? new Song
        {
            Id = Text(row, "Id"),
            Title = Text(row, "Name"),
            Album = Text(row, "Album"),
            Artist = row["Artists"] is JsonArray artists ? artists.FirstOrDefault()?.GetValue<string>() ?? "" : Text(row, "AlbumArtist"),
            Duration = row["RunTimeTicks"] is JsonValue ticks && ticks.TryGetValue<long>(out var duration) ? (int)(duration / TimeSpan.TicksPerSecond) : null,
            ExplicitContentLyrics = Rating(row, protocol)
        }
        : new Song
        {
            Id = Text(row, "id"),
            Title = Text(row, "title"),
            Album = Text(row, "album"),
            Artist = Text(row, "artist"),
            Duration = row["duration"] is JsonValue seconds && seconds.TryGetValue<int>(out var secondsValue) ? secondsValue : null,
            ExplicitContentLyrics = Rating(row, protocol)
        };
}

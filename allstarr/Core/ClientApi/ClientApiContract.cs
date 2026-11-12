using System.Text.Json;
using System.Text.Json.Serialization;

namespace allstarr.Core.ClientApi;

public static class ClientApiContract
{
    public const string GroupName = "allstarr-client-v1";
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    { DictionaryKeyPolicy = JsonNamingPolicy.CamelCase, NumberHandling = JsonNumberHandling.Strict };
}

public sealed record ClientCapabilities(
    [property: JsonPropertyName("serverVersion")] string ServerVersion,
    [property: JsonPropertyName("apiVersion")] int ApiVersion,
    [property: JsonPropertyName("features")] ClientFeatures Features,
    [property: JsonPropertyName("library")] ItemDetailsProvider Library,
    [property: JsonPropertyName("libraryAdditions")] ClientLibraryAdditionCapabilities LibraryAdditions,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ClientDashboard? Dashboard = null);

public sealed record ClientDashboard(string Url);

public sealed record ClientFeatures
{
    [JsonPropertyName("titleMarkers")] public bool TitleMarkers { get; init; } = true;
    [JsonPropertyName("itemDetails")] public bool ItemDetails { get; init; } = true;
    [JsonPropertyName("playback")] public bool Playback { get; init; } = true;
    [JsonPropertyName("lyricsRich")] public bool LyricsRich { get; init; } = true;
    [JsonPropertyName("lyricsVersions")] public bool LyricsVersions { get; init; } = true;
    [JsonPropertyName("lyricsSources")] public bool LyricsSources { get; init; } = true;
    [JsonPropertyName("fixesMatch")] public bool FixesMatch { get; init; } = true;
    [JsonPropertyName("reports")] public bool Reports { get; init; } = true;
    [JsonPropertyName("mixes")] public bool Mixes { get; init; } = true;
    [JsonPropertyName("releases")] public bool Releases { get; init; } = true;
    [JsonPropertyName("follow")] public bool Follow { get; init; } = true;
    [JsonPropertyName("lyricsSearch")] public bool LyricsSearch { get; init; } = true;
    [JsonPropertyName("searchStream")] public bool SearchStream { get; init; } = true;
    [JsonPropertyName("searchStreamRanked")] public bool SearchStreamRanked { get; init; } = true;
    [JsonPropertyName("searchStreamRankedLibrary")] public bool SearchStreamRankedLibrary { get; init; } = true;
    [JsonPropertyName("searchStreamScores")] public bool SearchStreamScores { get; init; } = true;
    [JsonPropertyName("events")] public bool Events { get; init; } = true;
    [JsonPropertyName("appearances")] public bool Appearances { get; init; } = true;
    [JsonPropertyName("similarAlbums")] public bool SimilarAlbums { get; init; } = true;
    [JsonPropertyName("playlistKinds")] public bool PlaylistKinds { get; init; } = true;
    [JsonPropertyName("stats")] public bool Stats { get; init; }
    [JsonPropertyName("taste")] public bool Taste { get; init; }
    [JsonPropertyName("dashboardLink")] public bool DashboardLink { get; init; }
    [JsonPropertyName("history")] public bool History { get; init; } = true;
    [JsonPropertyName("playCounts")] public bool PlayCounts { get; init; } = true;
    [JsonPropertyName("sound")] public bool Sound { get; init; }
    [JsonPropertyName("playActivity")] public bool PlayActivity { get; init; }
    [JsonPropertyName("historyImports")] public bool HistoryImports { get; init; }
    [JsonPropertyName("historyRecheck")] public bool HistoryRecheck { get; init; }
    [JsonPropertyName("libraryAdditions")] public bool LibraryAdditions { get; init; } = true;
    [JsonPropertyName("preferences")] public bool Preferences { get; init; } = true;
    [JsonPropertyName("karaokeModel")] public bool KaraokeModel { get; init; }
    [JsonPropertyName("v2Tracks")] public bool V2Tracks { get; init; } = true;
    [JsonPropertyName("v2Batch")] public bool V2Batch { get; init; } = true;
    [JsonPropertyName("v2ArtistLinks")] public bool V2ArtistLinks { get; init; } = true;
}

public sealed record KaraokeModelMetadata(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes,
    [property: JsonPropertyName("computeUnits")] string ComputeUnits,
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("license")] string License);

public sealed record ClientApiError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("error")] string Error)
{
    public static int NormalizeStatus(int status) => status switch
    {
        400 or 401 or 403 or 404 or 409 or 416 => status,
        405 or 406 or 413 or 415 or 422 => 400,
        _ => 503
    };

    public static ClientApiError ForStatus(int status) => NormalizeStatus(status) switch
    {
        400 => new("invalid_request", "The request is invalid."),
        401 => new("unauthorized", "A valid Jellyfin user token is required."),
        403 => new("forbidden", "This token is not bound to an available listener."),
        404 => new("not_found", "The requested resource was not found."),
        409 => new("conflict", "The request conflicts with the current state."),
        416 => new("range_not_satisfiable", "The requested range cannot be satisfied."),
        _ => new("unavailable", "The service is temporarily unavailable.")
    };

    public static Task WriteAsync(HttpContext context, int status)
    {
        context.Response.StatusCode = NormalizeStatus(status);
        return context.Response.WriteAsJsonAsync(ForStatus(status), ClientApiContract.JsonOptions, context.RequestAborted);
    }
}

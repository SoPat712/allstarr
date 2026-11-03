using System.Text.Json;
using System.Text.Json.Serialization;

namespace allstarr.Core.ClientApi;

public static class ClientApiContract
{
    public const string GroupName = "allstarr-client-v1";
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    { DictionaryKeyPolicy = JsonNamingPolicy.CamelCase, NumberHandling = JsonNumberHandling.Strict };

    public static ClientCapabilities Capabilities { get; } = new(AppVersion.Version, 1, new());
}

public sealed record ClientCapabilities(
    [property: JsonPropertyName("serverVersion")] string ServerVersion,
    [property: JsonPropertyName("apiVersion")] int ApiVersion,
    [property: JsonPropertyName("features")] ClientFeatures Features);

public sealed record ClientFeatures
{
    [JsonPropertyName("titleMarkers")] public bool TitleMarkers { get; init; } = true;
    [JsonPropertyName("itemDetails")] public bool ItemDetails { get; init; } = true;
    [JsonPropertyName("playback")] public bool Playback { get; init; } = true;
    [JsonPropertyName("lyricsRich")] public bool LyricsRich { get; init; } = true;
    [JsonPropertyName("lyricsSources")] public bool LyricsSources { get; init; } = true;
    [JsonPropertyName("fixesMatch")] public bool FixesMatch { get; init; } = true;
    [JsonPropertyName("reports")] public bool Reports { get; init; } = true;
    [JsonPropertyName("mixes")] public bool Mixes { get; init; } = true;
    [JsonPropertyName("releases")] public bool Releases { get; init; } = true;
    [JsonPropertyName("follow")] public bool Follow { get; init; } = true;
    [JsonPropertyName("lyricsSearch")] public bool LyricsSearch { get; init; }
    [JsonPropertyName("events")] public bool Events { get; init; } = true;
}

public sealed record ClientApiError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("error")] string Error)
{
    public static int NormalizeStatus(int status) => status switch
    {
        400 or 401 or 403 or 404 or 409 => status,
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
        _ => new("unavailable", "The service is temporarily unavailable.")
    };

    public static Task WriteAsync(HttpContext context, int status)
    {
        context.Response.StatusCode = NormalizeStatus(status);
        return context.Response.WriteAsJsonAsync(ForStatus(status), ClientApiContract.JsonOptions, context.RequestAborted);
    }
}

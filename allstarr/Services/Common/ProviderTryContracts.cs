using System.Text.Json.Serialization;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Spotify;

namespace allstarr.Services.Common;

public sealed class ProviderTryRequest
{
    [JsonPropertyName("capability")]
    public string Capability { get; init; } = "";
    [JsonPropertyName("operation")]
    public string Operation { get; init; } = "";
    [JsonPropertyName("accountId")]
    public Guid? AccountId { get; init; }
    [JsonPropertyName("query")]
    public string? Query { get; init; }
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }
    [JsonPropertyName("id")]
    public string? Id { get; init; }
    [JsonPropertyName("isrc")]
    public string? Isrc { get; init; }
    [JsonPropertyName("url")]
    public string? Url { get; init; }
    [JsonPropertyName("title")]
    public string? Title { get; init; }
    [JsonPropertyName("artist")]
    public string? Artist { get; init; }
    [JsonPropertyName("album")]
    public string? Album { get; init; }
    [JsonPropertyName("durationSeconds")]
    public int? DurationSeconds { get; init; }
    [JsonPropertyName("trackId")]
    public string? TrackId { get; init; }
    [JsonPropertyName("quality")]
    public string? Quality { get; init; }

    public string? Validate(string? providerId = null)
    {
        if (new[] { Capability, Operation, Query, Kind, Id, Isrc, Url, Title, Artist, Album, TrackId, Quality }.Any(item => item?.Length > 500))
            return "Text inputs must be at most 500 characters.";
        if (new[] { Capability, Operation, Query, Kind, Id, Isrc, Url, Title, Artist, Album, TrackId, Quality }.Any(item => item?.Any(char.IsControl) == true))
            return "Text inputs must not contain control characters.";
        if (Kind != null && Kind is not ("tracks" or "albums" or "artists"))
            return "Search kind must be tracks, albums or artists.";
        if (Quality != null && Quality is not ("any" or "lossy" or "lossless" or "high-resolution"))
            return "Quality must be any, lossy, lossless or high-resolution.";
        if (DurationSeconds is < 0 or > 86400) return "Duration must be between zero and 86400 seconds.";
        if (Operation == "lyrics" && providerId == "spicylyrics")
            return SpotifyTrackReference.TryNormalize(TrackId, out _) ? null : "A Spotify track link or 22-character ID is required.";
        return Operation switch
        {
            "search" when string.IsNullOrWhiteSpace(Query) => "A search query is required.",
            "track" when string.IsNullOrWhiteSpace(Id) => "A track id is required.",
            "isrc" when string.IsNullOrWhiteSpace(Isrc) => "An ISRC is required.",
            "isrc" or "lyrics" when Isrc?.Length > 20 => "An ISRC must be at most 20 characters.",
            "playlist" when string.IsNullOrWhiteSpace(Url) => "A playlist URL or id is required.",
            "download" when string.IsNullOrWhiteSpace(TrackId) => "A track id is required.",
            "lyrics" when string.IsNullOrWhiteSpace(Title) && string.IsNullOrWhiteSpace(Isrc) => "A title or ISRC is required.",
            _ => null
        };
    }
}

public sealed record ProviderTryCatalog([property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<ProviderTryCapability> Capabilities);
public sealed record ProviderTryCapability([property: JsonPropertyName("capability")] string Capability,
    [property: JsonPropertyName("accountRequirement")] string AccountRequirement,
    [property: JsonPropertyName("operations")] IReadOnlyList<ProviderTryOperation> Operations);
public sealed record ProviderTryOperation([property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("inputs")] IReadOnlyList<string> Inputs,
    [property: JsonPropertyName("kinds"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Kinds = null);

public sealed record ProviderTryLyricLine(
    [property: JsonPropertyName("startMs"), JsonPropertyOrder(0)] double? StartMs,
    [property: JsonPropertyName("text"), JsonPropertyOrder(1)] string Text);

public sealed record ProviderTryLyrics(
    [property: JsonPropertyName("format"), JsonPropertyOrder(0)] string Format,
    [property: JsonPropertyName("source"), JsonPropertyOrder(1)] string Source,
    [property: JsonPropertyName("language"), JsonPropertyOrder(2)] string? Language,
    [property: JsonPropertyName("lineCount"), JsonPropertyOrder(3)] int LineCount,
    [property: JsonPropertyName("lines"), JsonPropertyOrder(4)] IReadOnlyList<ProviderTryLyricLine> Lines,
    [property: JsonPropertyName("attribution"), JsonPropertyOrder(5), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ProviderLyricsAttribution? Attribution = null);

public sealed class ProviderTryResult
{
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "";
    [JsonPropertyName("capability")]
    public string Capability { get; set; } = "";
    [JsonPropertyName("operation")]
    public string Operation { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    [JsonPropertyName("accountId")]
    public Guid? AccountId { get; set; }
    [JsonPropertyName("succeeded")]
    public bool Succeeded { get; set; }
    [JsonPropertyName("elapsedMs")]
    public double ElapsedMs { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    [JsonPropertyName("reasonCode")]
    public string? ReasonCode { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    [JsonPropertyName("message")]
    public string? Message { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("tracks")]
    public IReadOnlyList<object>? Tracks { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("albums")]
    public IReadOnlyList<object>? Albums { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("artists")]
    public IReadOnlyList<object>? Artists { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("playlist")]
    public object? Playlist { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("playlists")]
    public IReadOnlyList<object>? Playlists { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("lyrics")]
    public object? Lyrics { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("media")]
    public object? Media { get; set; }

    public static ProviderTryResult Failure(string reason, string? message = null) => new()
    {
        ReasonCode = reason,
        Message = message ?? (reason == "not_found" ? "No results were found." :
            reason == "timeout" ? "The provider operation timed out." : "The provider operation failed.")
    };
}

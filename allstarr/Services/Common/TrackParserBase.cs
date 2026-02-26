using System.Text.Json;

namespace allstarr.Services.Common;

/// <summary>
/// Shared helpers for provider-specific track/album/artist parsers.
/// Keeps ID and date parsing behavior consistent across metadata services.
/// </summary>
public abstract class TrackParserBase
{
    protected static string BuildExternalSongId(string provider, string externalId)
    {
        return $"ext-{provider}-song-{externalId}";
    }

    protected static string BuildExternalAlbumId(string provider, string externalId)
    {
        return $"ext-{provider}-album-{externalId}";
    }

    protected static string BuildExternalArtistId(string provider, string externalId)
    {
        return $"ext-{provider}-artist-{externalId}";
    }

    protected static int? ParseYearFromDateString(string? dateString)
    {
        if (string.IsNullOrWhiteSpace(dateString) || dateString.Length < 4)
        {
            return null;
        }

        return int.TryParse(dateString.Substring(0, 4), out var year)
            ? year
            : null;
    }

    protected static string GetIdAsString(JsonElement idElement)
    {
        return idElement.ValueKind switch
        {
            JsonValueKind.Number => idElement.GetInt64().ToString(),
            JsonValueKind.String => idElement.GetString() ?? string.Empty,
            _ => string.Empty
        };
    }
}

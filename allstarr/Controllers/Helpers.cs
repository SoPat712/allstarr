using System.Text.Json;
using System.Text;
using allstarr.Services.Common;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

public partial class JellyfinController
{
    #region Helpers

    /// <summary>
    /// Helper to handle proxy responses with proper status code handling.
    /// </summary>
    private IActionResult HandleProxyResponse(JsonDocument? result, int statusCode, object? fallbackValue = null)
    {
        return ProxyResponseResultFactory.Create(result, statusCode, fallbackValue);
    }

    /// <summary>
    /// Records retention-bounded endpoint usage without query strings.
    /// </summary>
    private async Task LogEndpointUsageAsync(string path, string method)
    {
        try
        {
            var execution = HttpContext.GetProtocolExecutionContext();
            var actor = execution?.Actor;
            await HttpContext.RequestServices.GetRequiredService<EndpointUsageAudit>().RecordAsync(
                method,
                path,
                actor?.TenantId,
                actor?.UserId,
                HttpContext.TraceIdentifier,
                HttpContext.RequestAborted);
        }
        catch (Exception ex)
        {
            // Don't let logging failures break the request
            _logger.LogWarning(ex, "Failed to record endpoint usage");
        }
    }

    // Redacts security-sensitive query params before any logging or analytics persistence.
    private static string MaskSensitiveQueryString(string? queryString)
    {
        if (string.IsNullOrEmpty(queryString))
        {
            return string.Empty;
        }

        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(queryString);
        var parts = new List<string>();

        foreach (var kv in query)
        {
            var key = kv.Key;
            var value = kv.Value.ToString();
            if (string.Equals(key, "api_key", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "token", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "auth", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "authorization", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "x-emby-token", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "x-emby-authorization", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("auth", StringComparison.OrdinalIgnoreCase))
            {
                parts.Add($"{key}=<redacted>");
            }
            else
            {
                parts.Add($"{key}={value}");
            }
        }

        return parts.Count > 0 ? "?" + string.Join("&", parts) : string.Empty;
    }

    private static string[]? ParseItemTypes(string? includeItemTypes)
    {
        if (string.IsNullOrWhiteSpace(includeItemTypes))
        {
            return null;
        }

        return includeItemTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string? GetExactPlaylistItemsRequestId(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 ||
            !parts[0].Equals("playlists", StringComparison.OrdinalIgnoreCase) ||
            !parts[2].Equals("items", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return parts[1];
    }

    private static string? ExtractImageTag(JsonElement item, string imageType)
    {
        if (item.TryGetProperty("ImageTags", out var imageTags) &&
            imageTags.ValueKind == JsonValueKind.Object)
        {
            foreach (var imageTag in imageTags.EnumerateObject())
            {
                if (string.Equals(imageTag.Name, imageType, StringComparison.OrdinalIgnoreCase))
                {
                    return imageTag.Value.GetString();
                }
            }
        }

        if (string.Equals(imageType, "Primary", StringComparison.OrdinalIgnoreCase) &&
            item.TryGetProperty("PrimaryImageTag", out var primaryImageTag))
        {
            return primaryImageTag.GetString();
        }

        return null;
    }

    /// <summary>
    /// Determines whether Spotify playlist count enrichment should run for a response.
    /// We only run enrichment for playlist-oriented payloads to avoid mutating unrelated item lists
    /// (for example, album browse responses requested by clients like Finer).
    /// </summary>
    private bool ShouldProcessSpotifyPlaylistCounts(JsonDocument response, string? includeItemTypes)
    {
        if (!_spotifySettings.Enabled)
        {
            return false;
        }

        if (response.RootElement.ValueKind != JsonValueKind.Object ||
            !response.RootElement.TryGetProperty("Items", out var items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var requestedTypes = ParseItemTypes(includeItemTypes);
        if (requestedTypes != null && requestedTypes.Length > 0)
        {
            return requestedTypes.Contains("Playlist", StringComparer.OrdinalIgnoreCase);
        }

        // If the request did not explicitly constrain types, inspect payload types.
        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("Type", out var typeProp))
            {
                continue;
            }

            if (string.Equals(typeProp.GetString(), "Playlist", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Recovers SearchTerm directly from raw query string.
    /// Handles malformed clients that do not URL-encode '&' inside SearchTerm.
    /// </summary>
    private static string? RecoverSearchTermFromRawQuery(string? rawQueryString)
    {
        if (string.IsNullOrWhiteSpace(rawQueryString))
        {
            return null;
        }

        var query = rawQueryString[0] == '?' ? rawQueryString[1..] : rawQueryString;
        const string key = "SearchTerm=";
        var start = query.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        var valueStart = start + key.Length;
        if (valueStart >= query.Length)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        var i = valueStart;
        while (i < query.Length)
        {
            var ch = query[i];
            if (ch == '&')
            {
                var next = i + 1;
                var equalsIndex = query.IndexOf('=', next);
                var nextAmp = query.IndexOf('&', next);

                var isParameterDelimiter = equalsIndex > next &&
                                           (nextAmp < 0 || equalsIndex < nextAmp);

                if (isParameterDelimiter)
                {
                    break;
                }
            }

            sb.Append(ch);
            i++;
        }

        var encoded = sb.ToString();
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return string.Empty;
        }

        var plusAsSpace = encoded.Replace("+", " ");
        return Uri.UnescapeDataString(plusAsSpace);
    }

    /// <summary>
    /// Uses model-bound SearchTerm when valid; falls back to raw query recovery when needed.
    /// </summary>
    private static string? GetEffectiveSearchTerm(string? boundSearchTerm, string? rawQueryString)
    {
        var recovered = RecoverSearchTermFromRawQuery(rawQueryString);
        if (string.IsNullOrWhiteSpace(recovered))
        {
            return boundSearchTerm;
        }

        if (string.IsNullOrWhiteSpace(boundSearchTerm))
        {
            return recovered;
        }

        // Prefer recovered when it is meaningfully longer (common malformed '&' case).
        var boundTrimmed = boundSearchTerm.Trim();
        var recoveredTrimmed = recovered.Trim();
        return recoveredTrimmed.Length > boundTrimmed.Length
            ? recoveredTrimmed
            : boundSearchTerm;
    }

    private static string GetContentType(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension switch
        {
            ".mp3" => "audio/mpeg",
            ".flac" => "audio/flac",
            ".ogg" => "audio/ogg",
            ".m4a" => "audio/mp4",
            ".wav" => "audio/wav",
            ".aac" => "audio/aac",
            _ => "audio/mpeg"
        };
    }

    /// <summary>
    /// Scores search results based on fuzzy matching against the query.
    /// Returns items with their relevance scores.
    /// External results get a small boost to prioritize the larger catalog.
    /// </summary>
    private static List<(T Item, int Score)> ScoreSearchResults<T>(
        string query,
        List<T> items,
        Func<T, string> titleField,
        Func<T, string?> artistField,
        Func<T, string?> albumField,
        bool isExternal = false)
    {
        return items.Select(item =>
        {
            var title = titleField(item) ?? "";
            var artist = artistField(item) ?? "";
            var album = albumField(item) ?? "";

            // Token-based fuzzy matching: split query and fields into words
            var queryTokens = query.ToLower()
                .Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
                .ToList();

            var fieldText = $"{title} {artist} {album}".ToLower();
            var fieldTokens = fieldText
                .Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
                .ToList();

            if (queryTokens.Count == 0) return (item, 0);

            // Count how many query tokens match field tokens (with fuzzy tolerance)
            var matchedTokens = 0;
            foreach (var queryToken in queryTokens)
            {
                // Check if any field token matches this query token
                var hasMatch = fieldTokens.Any(fieldToken =>
                {
                    // Exact match or substring match
                    if (fieldToken.Contains(queryToken) || queryToken.Contains(fieldToken))
                        return true;

                    // Fuzzy match with Levenshtein distance
                    var similarity = FuzzyMatcher.CalculateSimilarity(queryToken, fieldToken);
                    return similarity >= 70; // 70% similarity threshold for individual words
                });

                if (hasMatch) matchedTokens++;
            }

            // Score = percentage of query tokens that matched
            var baseScore = (matchedTokens * 100) / queryTokens.Count;

            // Give external results a small boost (+5 points) to prioritize the larger catalog
            var finalScore = isExternal ? Math.Min(100, baseScore + 5) : baseScore;

            return (item, finalScore);
        }).ToList();
    }

    #endregion
}

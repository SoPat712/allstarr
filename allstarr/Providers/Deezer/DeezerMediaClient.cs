using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using allstarr.Services.Deezer;

namespace allstarr.Core.Providers.Deezer;

// Adapted from V1ck3s/octo-fiesta, revision 6841a2ea9c227b212157d81acc4aafa5965419b2.
// Modified 2026-10-08; see docs/architecture/reference-projects.md#deezer-implementation-notice.

/// <summary>
/// C# port of the DeezerDownloader JavaScript.
/// Resolves Deezer media using credentials supplied for the current operation.
/// </summary>
public sealed class DeezerMediaClient(DeezerHttpClient http, ILogger<DeezerMediaClient> logger)
{
    private const string ApiBase = "https://api.deezer.com";

    internal async Task<DeezerDownloadResult> ResolveDownloadAsync(
        string trackId,
        string? arl,
        string? arlFallback,
        string? quality,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var requestedId = NormalizeTrackId(trackId) ?? throw NotFound();
        if (string.IsNullOrWhiteSpace(arl)) throw Unauthorized();
        try
        {
            return await ResolveWithCredentialAsync(requestedId, arl, quality, cancellationToken);
        }
        catch (HttpRequestException exception) when (
            exception.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden &&
            !string.IsNullOrWhiteSpace(arlFallback) && !string.Equals(arl, arlFallback, StringComparison.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            logger.LogDebug("Deezer media resolution is trying the configured fallback credential.");
            return await ResolveWithCredentialAsync(requestedId, arlFallback, quality, cancellationToken);
        }
    }

    private async Task<DeezerDownloadResult> ResolveWithCredentialAsync(
        string requestedId,
        string arl,
        string? quality,
        CancellationToken cancellationToken)
    {
        var session = await InitializeAsync(arl, cancellationToken);
        var original = await GetTrackAsync(requestedId, requireId: false, cancellationToken) ?? throw NotFound();
        var selected = original;
        if (original.Readable == false || string.IsNullOrWhiteSpace(original.Token))
        {
            selected = await FindAlternativeAsync(original, session, arl, cancellationToken) ?? throw NotFound();
        }

        var formats = AllowedFormats(quality);
        var result = await TryResolveMediaAsync(selected, session, formats, cancellationToken);
        if (result != null) return result;
        if (selected.Id == original.Id)
        {
            var alternative = await FindAlternativeAsync(original, session, arl, cancellationToken);
            if (alternative != null)
            {
                result = await TryResolveMediaAsync(alternative, session, formats, cancellationToken);
                if (result != null) return result;
            }
        }
        throw NotFound();
    }

    private async Task<DeezerDownloadResult?> TryResolveMediaAsync(
        Track selected,
        Session session,
        string[] formats,
        CancellationToken cancellationToken)
    {
        using var response = await http.SendAsync(() => JsonRequest(
            "https://media.deezer.com/v1/get_url",
            null,
            new
            {
                license_token = session.LicenseToken,
                media = new[] { new { type = "FULL", formats = formats.Select(format => new { cipher = "BF_CBC_STRIPE", format }).ToArray() } },
                track_tokens = new[] { selected.Token }
            }), cancellationToken);
        using var document = await ReadJsonAsync(response, cancellationToken);
        var root = document.RootElement;
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array ||
            data.GetArrayLength() == 0 || data[0].ValueKind != JsonValueKind.Object ||
            !data[0].TryGetProperty("media", out var media) ||
            media.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var format in formats)
        {
            foreach (var item in media.EnumerateArray())
            {
                if (ReadString(item, "format") != format ||
                    !item.TryGetProperty("sources", out var sources) || sources.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var source in sources.EnumerateArray())
                {
                    var url = ReadString(source, "url");
                    if (string.IsNullOrWhiteSpace(url)) continue;
                    logger.LogDebug("Deezer media resolved in format {Format}.", format);
                    return new DeezerDownloadResult
                    {
                        DownloadUrl = url,
                        Format = format,
                        Title = selected.Title,
                        Artist = selected.Artist,
                        TrackId = selected.Id
                    };
                }
            }
        }
        return null;
    }

    private async Task<Session> InitializeAsync(string arl, CancellationToken cancellationToken)
    {
        using var response = await http.SendAsync(() => JsonRequest(
            "https://www.deezer.com/ajax/gw-light.php?method=deezer.getUserData&input=3&api_version=1.0&api_token=null",
            arl,
            new { }), cancellationToken);
        using var document = await ReadJsonAsync(response, cancellationToken);
        var root = document.RootElement;
        if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Object &&
            results.TryGetProperty("checkForm", out var checkForm) &&
            results.TryGetProperty("USER", out var user) && user.ValueKind == JsonValueKind.Object &&
            user.TryGetProperty("OPTIONS", out var options) &&
            ReadString(options, "license_token") is { } licenseToken && !string.IsNullOrWhiteSpace(licenseToken))
            return new Session(licenseToken, checkForm.ValueKind == JsonValueKind.String ? checkForm.GetString() : null);
        throw Unauthorized();
    }

    private async Task<Track?> GetTrackAsync(
        string trackId,
        bool requireId,
        CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync($"{ApiBase}/track/{trackId}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        using var document = await ReadJsonAsync(response, cancellationToken);
        var root = document.RootElement;
        if (HasError(root)) return null;
        var id = ReadTrackId(root, "id");
        if ((requireId && id == null) || (root.TryGetProperty("id", out _) && id != trackId)) return null;
        bool? readable = root.TryGetProperty("readable", out var value)
            ? value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => (bool?)false }
            : null;
        var artist = root.TryGetProperty("artist", out var artistValue) ? ReadString(artistValue, "name") : null;
        var alternativeId = root.TryGetProperty("alternative", out var alternative)
            ? ReadTrackId(alternative, "id")
            : null;
        return new Track(trackId, ReadString(root, "track_token"), ReadString(root, "title") ?? "",
            artist ?? "", ReadString(root, "isrc"), readable, alternativeId);
    }

    private async Task<Track?> FindAlternativeAsync(
        Track original,
        Session session,
        string arl,
        CancellationToken cancellationToken)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal) { original.Id };
        var publicAlternative = await TryCandidateAsync(original.AlternativeId, null);
        if (publicAlternative != null) return publicAlternative;

        var page = await GetPageDataAsync(original.Id, session, arl, cancellationToken);
        var privateAlternative = await TryCandidateAsync(page?.FallbackId, null);
        if (privateAlternative != null) return privateAlternative;

        var isrc = !string.IsNullOrWhiteSpace(original.Isrc) ? original.Isrc : page?.Isrc;
        if (string.IsNullOrWhiteSpace(isrc)) return null;
        using var response = await http.GetAsync($"{ApiBase}/track/isrc:{Uri.EscapeDataString(isrc)}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        using var document = await ReadJsonAsync(response, cancellationToken);
        var lookup = document.RootElement;
        if (HasError(lookup) || !SameIsrc(isrc, ReadString(lookup, "isrc"))) return null;
        return await TryCandidateAsync(ReadTrackId(lookup, "id"), isrc);

        async Task<Track?> TryCandidateAsync(string? candidateId, string? requiredIsrc)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidateId == null || !visited.Add(candidateId)) return null;
            var candidate = await GetTrackAsync(candidateId, requireId: true, cancellationToken);
            if (candidate?.Readable != true ||
                (requiredIsrc != null && !SameIsrc(requiredIsrc, candidate.Isrc))) return null;
            var candidatePage = await GetPageDataAsync(candidate.Id, session, arl, cancellationToken);
            var token = !string.IsNullOrWhiteSpace(candidatePage?.TrackToken) ? candidatePage.TrackToken : candidate.Token;
            return string.IsNullOrWhiteSpace(token) ? null : candidate with { Token = token };
        }
    }

    private async Task<PageData?> GetPageDataAsync(
        string trackId,
        Session session,
        string arl,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(session.CheckForm)) return null;
        using var response = await http.SendAsync(() => JsonRequest(
            "https://www.deezer.com/ajax/gw-light.php?method=deezer.pageTrack&input=3&api_version=1.0&api_token=" +
            Uri.EscapeDataString(session.CheckForm), arl, new { SNG_ID = trackId }), cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        using var document = await ReadJsonAsync(response, cancellationToken);
        var root = document.RootElement;
        if (HasError(root) || !root.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Object || !results.TryGetProperty("DATA", out var data) ||
            data.ValueKind != JsonValueKind.Object) return null;
        if (data.TryGetProperty("SNG_ID", out _) && ReadTrackId(data, "SNG_ID") != trackId) return null;
        var fallbackId = data.TryGetProperty("FALLBACK", out var fallback) ? ReadTrackId(fallback, "SNG_ID") : null;
        return new PageData(fallbackId, ReadString(data, "ISRC"), ReadString(data, "TRACK_TOKEN"));
    }

    internal async Task DecryptDownloadAsync(
        Stream input,
        Stream output,
        string trackId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var decrypted = new DeezerDecryptedStream(input, trackId, leaveOpen: true);
        await decrypted.CopyToAsync(output, cancellationToken);
    }

    private static HttpRequestMessage JsonRequest(string url, string? arl, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (arl != null) request.Headers.Add("Cookie", $"arl={arl}");
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return request;
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("Deezer provider request failed.", null, response.StatusCode);
        try
        {
            var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (document.RootElement.ValueKind == JsonValueKind.Object) return document;
            document.Dispose();
        }
        catch (JsonException)
        {
            // Provider payloads are deliberately excluded from errors and logs.
        }
        throw new HttpRequestException("Deezer returned an invalid response.", null, HttpStatusCode.BadGateway);
    }

    private static bool HasError(JsonElement root) => root.TryGetProperty("error", out var error) &&
        (error.ValueKind switch
        {
            JsonValueKind.Null => false,
            JsonValueKind.Array => error.GetArrayLength() > 0,
            JsonValueKind.Object => error.EnumerateObject().Any(),
            _ => true
        });

    private static string? ReadString(JsonElement root, string property) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? ReadTrackId(JsonElement root, string property) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(property, out var value)
            ? NormalizeTrackId(value.ValueKind == JsonValueKind.String ? value.GetString() :
                value.ValueKind == JsonValueKind.Number ? value.GetRawText() : null)
            : null;

    private static string? NormalizeTrackId(string? id) =>
        long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value.ToString(CultureInfo.InvariantCulture)
            : null;

    private static bool SameIsrc(string expected, string? actual) =>
        !string.IsNullOrWhiteSpace(actual) && string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);

    private static string[] AllowedFormats(string? quality) => quality?.ToUpperInvariant() switch
    {
        "MP3_320" or "320" => ["MP3_320", "MP3_128"],
        "MP3_128" or "128" => ["MP3_128"],
        _ => ["FLAC", "MP3_320", "MP3_128"]
    };

    private static HttpRequestException NotFound() =>
        new("No verified Deezer media is available for this track.", null, HttpStatusCode.NotFound);

    private static HttpRequestException Unauthorized() =>
        new("Deezer credentials are unavailable or invalid.", null, HttpStatusCode.Unauthorized);

    private sealed record Session(string LicenseToken, string? CheckForm);
    private sealed record Track(string Id, string? Token, string Title, string Artist, string? Isrc,
        bool? Readable, string? AlternativeId);
    private sealed record PageData(string? FallbackId, string? Isrc, string? TrackToken);

    internal sealed class DeezerDownloadResult
    {
        public string DownloadUrl { get; init; } = string.Empty;
        public string Format { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public string Artist { get; init; } = string.Empty;
        public string TrackId { get; init; } = string.Empty;
    }
}

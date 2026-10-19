using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using allstarr.Core.Operations;
using allstarr.Core.Playback;

namespace allstarr.Services.Scrobbling;

internal static class ScrobbleResponseDiagnostics
{
    internal static async Task<string> ReadBodyAsync(HttpContent content, int limit, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await using var stream = await content.ReadAsStreamAsync(timeout.Token);
            var bytes = new byte[limit];
            var length = 0;
            while (length < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(length), timeout.Token);
                if (read == 0) break;
                length += read;
            }
            return Encoding.UTF8.GetString(bytes, 0, length);
        }
        catch (HttpRequestException) { return string.Empty; }
        catch (IOException) { return string.Empty; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return string.Empty; }
    }

    internal static string? Reason(string body, JsonElement secret)
    {
        var text = body.Trim();
        if (text.Length == 0) return null;
        try
        {
            if (text[0] is '{' or '[' || text == "null")
            {
                using var document = JsonDocument.Parse(text);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;
                text = root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                    ? error.GetString()
                    : root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
                        ? message.GetString() : null;
            }
            else if (text[0] == '<')
            {
                using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
                text = XDocument.Load(reader).Root?.Element("error")?.Value;
            }
        }
        catch (JsonException) { return null; }
        catch (XmlException) { return null; }
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (var name in new[] { "token", "apiKey", "sessionKey", "sharedSecret" })
            if (secret.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
                value.GetString() is { Length: > 0 } credential)
                text = text.Replace(credential, "<redacted>", StringComparison.Ordinal);
        text = new string(text.Select(character => char.IsControl(character) ? ' ' : character).ToArray());
        return SafeOperationalText.Sanitize(text, 200);
    }

    internal static ScopedPlaybackScrobbleResult Rejection(ScopedPlaybackScrobbleResult result,
        string providerId, int status, bool playing, string? reason, ILogger? logger)
    {
        if (result.Outcome is ScopedPlaybackScrobbleOutcome.Delivered or ScopedPlaybackScrobbleOutcome.Ignored) return result;
        if (reason != null)
        {
            var details = JsonSerializer.Deserialize<Dictionary<string, object>>(result.DetailsJson)!;
            details["providerMessage"] = reason;
            result = result with
            {
                SafeMessage = $"{result.SafeMessage?.TrimEnd('.', ' ')}: {reason}",
                DetailsJson = JsonSerializer.Serialize(details)
            };
        }
        logger?.LogWarning("Scrobble rejected by {ProviderId} with HTTP {StatusCode} for {ListenType}: {Reason}",
            providerId, status, playing ? "playing_now" : "single", reason ?? "No readable rejection reason returned.");
        return result;
    }
}

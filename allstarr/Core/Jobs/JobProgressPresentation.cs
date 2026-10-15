using System.Text.Json;
using allstarr.Core.Operations;

namespace allstarr.Core.Jobs;

internal static class JobProgressPresentation
{
    public static string SafeDetails(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return "{}";
            var result = new Dictionary<string, object>();
            foreach (var name in new[] { "stage", "message", "provider", "playlist", "track", "deferralReason" })
                if (document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
                    SafeOperationalText.Sanitize(value.GetString(), 500) is { } text) result[name] = text;
            foreach (var name in new[] { "completed", "total", "throughputPerSecond" })
                if (document.RootElement.TryGetProperty(name, out var value) && value.TryGetDouble(out var number) && double.IsFinite(number) && number >= 0)
                    result[name] = number;
            return JsonSerializer.Serialize(result);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { return "{}"; }
    }
}

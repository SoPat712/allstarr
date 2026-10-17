using System.Globalization;
using System.Text;
using System.Text.Json;

namespace allstarr.Core.Matching;

internal static class TrackMatchReviewSearch
{
    public static string Normalize(string value)
    {
        var text = new StringBuilder(value.Length);
        foreach (var character in value.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
                continue;
            if (character == '&')
                text.Append(" and ");
            else
                text.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ');
        }
        return string.Join(' ', text.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    public static bool Matches(string providerId, string payloadJson, string search)
    {
        if (search.Length == 0) return false;
        var text = new StringBuilder(providerId);
        try
        {
            using var payload = JsonDocument.Parse(payloadJson);
            AppendValues(payload.RootElement, text);
        }
        catch (JsonException)
        {
            text.Append(' ').Append(payloadJson);
        }
        return Normalize(text.ToString()).Contains(search, StringComparison.Ordinal);
    }

    private static void AppendValues(JsonElement value, StringBuilder text)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                    AppendValues(property.Value, text);
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                    AppendValues(item, text);
                break;
            case JsonValueKind.String:
                text.Append(' ').Append(value.GetString());
                break;
        }
    }
}

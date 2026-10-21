using System.Text;
using System.Text.Json;

namespace allstarr.Services.Jellyfin;

internal static class JellyfinQueryResponseRows
{
    public const string NativeRowsKey = "allstarr.jellyfin-query-native-rows";

    public static string Preserve(string body, HttpContext http)
    {
        if (!http.Items.TryGetValue(NativeRowsKey, out var value) || value is not IReadOnlyDictionary<string, string> nativeRows || nativeRows.Count == 0) return body;
        using var document = JsonDocument.Parse(body);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name != "Items" || property.Value.ValueKind != JsonValueKind.Array)
                {
                    property.WriteTo(writer);
                    continue;
                }
                writer.WritePropertyName(property.Name);
                writer.WriteStartArray();
                foreach (var item in property.Value.EnumerateArray())
                {
                    if (item.TryGetProperty("Id", out var idValue) && idValue.ValueKind == JsonValueKind.String &&
                        idValue.GetString() is { } id && nativeRows.TryGetValue(id, out var native))
                        writer.WriteRawValue(native);
                    else
                        item.WriteTo(writer);
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}

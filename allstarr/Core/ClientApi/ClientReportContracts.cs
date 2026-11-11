using System.Text.Json;
using System.Text.Json.Serialization;

namespace allstarr.Core.ClientApi;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateClientReportRequest(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("note")] string Note,
    [property: JsonPropertyName("itemId")] string? ItemId = null,
    [property: JsonPropertyName("playbackRequestId")]
    [property: JsonConverter(typeof(ClientReportPlaybackRequestIdJsonConverter))]
    Guid? PlaybackRequestId = null);

public sealed class ClientReportPlaybackRequestIdJsonConverter : JsonConverter<Guid?>
{
    public override Guid? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("The playbackRequestId field must be a string.");
        }

        var text = reader.GetString();
        if (text is not null && (Guid.TryParseExact(text, "D", out var guid) || Guid.TryParseExact(text, "N", out guid)))
        {
            return guid;
        }

        throw new JsonException("The playbackRequestId field must be a valid GUID in D or N format.");
    }

    public override void Write(Utf8JsonWriter writer, Guid? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
        {
            writer.WriteStringValue(value.Value.ToString("D"));
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}

public sealed record ClientReportReceipt([property: JsonPropertyName("id")] Guid Id);

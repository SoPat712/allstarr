using System.Net;
using System.Text.Json;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Fact]
    public async Task ClientApi_OpenApiRequiresTheLibraryProviderInCapabilities()
    {
        using var factory = ClientApiFactory(_ => throw new InvalidOperationException("Unexpected upstream request"));
        using var client = CreateClientApiClient(factory);
        using var response = await client.GetAsync("/allstarr/v1/openapi.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var schema = document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("ClientCapabilities");
        Assert.Contains(schema.GetProperty("required").EnumerateArray(), value => value.GetString() == "library");
        Assert.Equal("#/components/schemas/ItemDetailsProvider", schema.GetProperty("properties").GetProperty("library").GetProperty("$ref").GetString());
    }

    [Fact]
    public async Task ClientApi_OpenApiNullableReferencesDeclareTypesAndKeepValidation()
    {
        using var factory = ClientApiFactory(_ => throw new InvalidOperationException("Unexpected upstream request"));
        using var client = CreateClientApiClient(factory);
        using var response = await client.GetAsync("/allstarr/v1/openapi.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.StartsWith("3.1.", document.RootElement.GetProperty("openapi").GetString());
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");

        foreach (var (owner, property, target) in new[]
        {
            ("ClientLyricsRevision", "personal", "ClientLyricsStamp"),
            ("ClientLyricsRevision", "household", "ClientLyricsStamp"),
            ("ClientLyricsSelection", "chosen", "ClientLyricsChosen"),
            ("ClientLyricsSources", "chosen", "ClientLyricsChosen"),
            ("ClientPlayback", "provider", "ItemDetailsProvider"),
            ("ClientPlayback", "delivered", "PlaybackDelivered"),
            ("ItemDetails", "provider", "ItemDetailsProvider"),
            ("ItemDetails", "audio", "ItemDetailsAudio"),
            ("ItemDetails", "match", "ItemDetailsMatch")
        })
        {
            var schema = schemas.GetProperty(owner).GetProperty("properties").GetProperty(property);
            Assert.Equal(["null", "object"], ClientApiSchemaTypes(schema).Order().ToArray());
            Assert.False(schema.TryGetProperty("$ref", out _));
            Assert.False(schema.TryGetProperty("allOf", out _));
            var alternatives = schema.GetProperty("anyOf").EnumerateArray().ToArray();
            Assert.Equal(2, alternatives.Length);
            Assert.Contains(alternatives, alternative => alternative.TryGetProperty("type", out var type) && type.GetString() == "null");
            var reference = Assert.Single(alternatives, alternative => alternative.TryGetProperty("$ref", out _));
            Assert.Equal($"#/components/schemas/{target}", reference.GetProperty("$ref").GetString());
            var referencedSchema = schemas.GetProperty(target);
            Assert.Equal(["object"], ClientApiSchemaTypes(referencedSchema));
            Assert.NotEmpty(referencedSchema.GetProperty("properties").EnumerateObject());
        }

        void CheckProperties(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                if (node.TryGetProperty("properties", out var properties))
                    foreach (var property in properties.EnumerateObject())
                        if (property.Value.TryGetProperty("oneOf", out var alternatives))
                            Assert.DoesNotContain(alternatives.EnumerateArray(), alternative =>
                                ClientApiSchemaTypes(alternative).Contains("null"));
                foreach (var property in node.EnumerateObject()) CheckProperties(property.Value);
            }
            else if (node.ValueKind == JsonValueKind.Array)
                foreach (var item in node.EnumerateArray()) CheckProperties(item);
        }

        var source = schemas.GetProperty("ClientLyricsSource").GetProperty("properties");
        foreach (var property in new[] { "title", "artist", "album", "format", "preview" })
            Assert.Equal(new[] { "null", "string" }, ClientApiSchemaTypes(source.GetProperty(property)).Order().ToArray());
        Assert.Equal(new[] { "integer", "null" }, ClientApiSchemaTypes(source.GetProperty("durationMs")).Order().ToArray());
        foreach (var property in new[] { "providerId", "providerName", "sourceId" })
            Assert.Equal(new[] { "string" }, ClientApiSchemaTypes(source.GetProperty(property)));
        CheckProperties(document.RootElement);
    }

    [Fact]
    public async Task ClientApi_OpenApiEnumsDeclareEveryAllowedType()
    {
        using var factory = ClientApiFactory(_ => throw new InvalidOperationException("Unexpected upstream request"));
        using var client = CreateClientApiClient(factory);
        using var response = await client.GetAsync("/allstarr/v1/openapi.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var accountScope = document.RootElement.GetProperty("components").GetProperty("schemas")
            .GetProperty("ClientPlayback").GetProperty("properties").GetProperty("accountScope");
        Assert.Equal(["null", "string"], ClientApiSchemaTypes(accountScope).Order().ToArray());
        Assert.Collection(accountScope.GetProperty("enum").EnumerateArray(),
            value => Assert.Equal("personal", value.GetString()),
            value => Assert.Equal("shared", value.GetString()),
            value => Assert.Equal(JsonValueKind.Null, value.ValueKind));

        var enumCount = 0;
        void CheckEnums(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                if (node.TryGetProperty("enum", out var values))
                {
                    enumCount++;
                    var types = ClientApiSchemaTypes(node);
                    Assert.NotEmpty(types);
                    foreach (var value in values.EnumerateArray())
                        Assert.Contains(value.ValueKind switch
                        {
                            JsonValueKind.String => "string",
                            JsonValueKind.Null => "null",
                            JsonValueKind.Number => value.TryGetInt64(out _) ? "integer" : "number",
                            JsonValueKind.True or JsonValueKind.False => "boolean",
                            _ => throw new InvalidOperationException("Unexpected enum value")
                        }, types);
                }
                foreach (var property in node.EnumerateObject()) CheckEnums(property.Value);
            }
            else if (node.ValueKind == JsonValueKind.Array)
                foreach (var item in node.EnumerateArray()) CheckEnums(item);
        }

        CheckEnums(document.RootElement);
        Assert.True(enumCount > 0);
    }

    private static string[] ClientApiSchemaTypes(JsonElement schema)
    {
        if (!schema.TryGetProperty("type", out var type)) return [];
        return type.ValueKind == JsonValueKind.Array
            ? type.EnumerateArray().Select(value => value.GetString()!).ToArray() : [type.GetString()!];
    }
}

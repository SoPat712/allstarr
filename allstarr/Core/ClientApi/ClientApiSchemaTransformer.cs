using System.Text.Json;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace allstarr.Core.ClientApi;

public sealed class ClientApiSchemaTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        var visited = new HashSet<IOpenApiSchema>(ReferenceEqualityComparer.Instance);
        if (document.Components?.Schemas is { } schemas)
            foreach (var schema in schemas.Values) Normalize(schema, visited);
        return Task.CompletedTask;
    }

    private static void Normalize(IOpenApiSchema input, HashSet<IOpenApiSchema> visited)
    {
        if (input is not OpenApiSchema schema || !visited.Add(schema)) return;

        if (schema.Enum is { Count: > 0 }) schema.Type = EnumTypes(schema);

        // Nullable references are wrapped after schema transformers run, so normalize the completed document.
        if (schema.OneOf is { Count: 2 } alternatives &&
            alternatives.SingleOrDefault(alternative => alternative.Type == JsonSchemaType.Null) is { } nullSchema &&
            alternatives.SingleOrDefault(alternative => alternative is OpenApiSchemaReference) is OpenApiSchemaReference reference)
        {
            if (reference.Target is { Enum.Count: > 0 } target)
            {
                schema.Type = EnumTypes(target) | JsonSchemaType.Null;
                schema.Enum = target.Enum.Select(value => value?.DeepClone()!).ToList();
                if (!schema.Enum.Contains(null!)) schema.Enum.Add(null!);
                schema.OneOf = null;
            }
            else if (reference.Type == JsonSchemaType.Object)
            {
                schema.Type = JsonSchemaType.Object | JsonSchemaType.Null;
                schema.AnyOf = [reference, nullSchema];
                schema.OneOf = null;
            }
        }

        if (schema.Properties is { } properties)
            foreach (var property in properties.Values) Normalize(property, visited);
        if (schema.Items is { } items) Normalize(items, visited);
        if (schema.AdditionalProperties is { } additionalProperties) Normalize(additionalProperties, visited);
        if (schema.AllOf is { } allOf)
            foreach (var alternative in allOf) Normalize(alternative, visited);
        if (schema.AnyOf is { } anyOf)
            foreach (var alternative in anyOf) Normalize(alternative, visited);
        if (schema.OneOf is { } oneOf)
            foreach (var alternative in oneOf) Normalize(alternative, visited);
        if (schema.Not is { } not) Normalize(not, visited);
    }

    private static JsonSchemaType EnumTypes(IOpenApiSchema schema)
    {
        JsonSchemaType types = 0;
        foreach (var value in schema.Enum!)
            types |= value?.GetValueKind() switch
            {
                null or JsonValueKind.Null => JsonSchemaType.Null,
                JsonValueKind.String => JsonSchemaType.String,
                JsonValueKind.Number => schema.Type == JsonSchemaType.Integer ? JsonSchemaType.Integer : JsonSchemaType.Number,
                JsonValueKind.True or JsonValueKind.False => JsonSchemaType.Boolean,
                JsonValueKind.Array => JsonSchemaType.Array,
                JsonValueKind.Object => JsonSchemaType.Object,
                _ => throw new InvalidOperationException("Unexpected enum value")
            };
        return types;
    }
}

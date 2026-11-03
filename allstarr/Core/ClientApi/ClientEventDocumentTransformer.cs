using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace allstarr.Core.ClientApi;

public sealed class ClientEventDocumentTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        if (document.Paths is { } paths)
            foreach (var path in paths.Values)
            {
                if (path.Operations is not { } operations) continue;
                foreach (var operation in operations.Values)
                    if (operation.OperationId == "getEvents" && operation.Responses is { } responses &&
                        responses.TryGetValue("200", out var response) && response is OpenApiResponse stream)
                        stream.Content?.Remove("application/json");
            }
        return Task.CompletedTask;
    }
}

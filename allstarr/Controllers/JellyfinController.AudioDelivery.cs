using System.Text.Json;
using allstarr.Core.Music;

namespace allstarr.Controllers;

public partial class JellyfinController
{
    private async Task<ProviderAudioDeliveryRequest> ReadAudioDeliveryRequestAsync()
    {
        if (!HttpMethods.IsPost(Request.Method) || Request.ContentLength == 0 || Request.ContentType == null)
            return ProviderAudioDeliveryRequests.Jellyfin(Request.Query);
        try
        {
            using var body = await JsonDocument.ParseAsync(Request.Body, cancellationToken: HttpContext.RequestAborted);
            if (body.RootElement.ValueKind != JsonValueKind.Object)
                return new() { Error = "Invalid audio profile." };
            return ProviderAudioDeliveryRequests.Jellyfin(Request.Query, body.RootElement);
        }
        catch (JsonException)
        {
            return new() { Error = "Invalid audio profile." };
        }
    }
}

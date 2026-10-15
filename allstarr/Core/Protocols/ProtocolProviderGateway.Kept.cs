using System.Net;
using allstarr.Core.Capabilities;

namespace allstarr.Core.Protocols;

public sealed partial class ProtocolProviderGateway
{
    private async Task<ProtocolProviderStream?> OpenHouseholdKeptAsync(ProtocolExecutionContext protocol,
        ProviderExternalResourceId track, ProviderAudioQuality quality, Guid? previousFileId, bool resuming, bool headOnly)
    {
        if (keptFiles == null) return null;
        var actor = protocol.RequireActor();
        var kept = await keptFiles.FindBySourceAsync(actor, track, protocol.CancellationToken);
        if (kept == null || resuming && kept.ManagedFileId != previousFileId) return null;
        HttpResponseMessage? response = null;
        try
        {
            response = new(HttpStatusCode.OK);
            if (headOnly) response.Content = new ByteArrayContent([]);
            else
            {
                var file = new FileStream(kept.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
                response.Content = new StreamContent(file);
                if (file.Length != kept.Length)
                {
                    response.Dispose();
                    return null;
                }
            }
            response.Content.Headers.ContentLength = kept.Length;
            response.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(kept.MimeType);
            response.Headers.AcceptRanges.Add("bytes");
            var container = Path.GetExtension(kept.Path).TrimStart('.').ToLowerInvariant();
            var lease = new ProviderStreamLease("household-kept", new Uri("https://kept.invalid/audio"),
                protocol.Deadline, true, true, new(kept.MimeType, string.IsNullOrEmpty(container) ? "bin" : container, "unknown"),
                ProviderStreamRetryBehavior.DoNotRetry);
            return new(response, lease, "allstarr", kept.RecordingId.ToString("D"), IsCached: true,
                EffectiveQuality: quality, SelectionReason: "household-kept")
            { ServingManagedFileId = kept.ManagedFileId };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            response?.Dispose();
            return null;
        }
        catch
        {
            response?.Dispose();
            throw;
        }
    }
}

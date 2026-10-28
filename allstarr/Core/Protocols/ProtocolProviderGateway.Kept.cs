using System.Net;
using allstarr.Core.Capabilities;

namespace allstarr.Core.Protocols;

public sealed partial class ProtocolProviderGateway
{
    private async Task<ProtocolProviderStream?> OpenHouseholdKeptAsync(ProtocolExecutionContext protocol,
        ProviderExternalResourceId track, ProviderAudioQuality quality, bool headOnly)
    {
        if (keptFiles == null || !await AllowsCleanPlaybackAsync(protocol, track)) return null;
        var actor = protocol.RequireActor();
        var opened = await keptFiles.OpenForPlaybackAsync(actor, track, headOnly, protocol.CancellationToken);
        if (opened == null) return null;
        var kept = opened.File;
        HttpResponseMessage? response = null;
        try
        {
            response = new(HttpStatusCode.OK);
            if (headOnly) response.Content = new ByteArrayContent([]);
            else
            {
                response.Content = new StreamContent(opened.Content!);
            }
            response.Content.Headers.ContentLength = kept.Length;
            response.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(kept.MimeType);
            response.Headers.AcceptRanges.Add("bytes");
            var container = Path.GetExtension(kept.Path).TrimStart('.').ToLowerInvariant();
            var lease = new ProviderStreamLease("household-kept", new Uri("https://kept.invalid/audio"),
                protocol.Deadline, true, true, kept.Media ?? new(kept.MimeType, string.IsNullOrEmpty(container) ? "bin" : container, "unknown"),
                ProviderStreamRetryBehavior.DoNotRetry);
            return new(response, lease, "allstarr", kept.RecordingId.ToString("D"), IsCached: true,
                EffectiveQuality: quality, SelectionReason: "household-kept")
            { ServingManagedFileId = kept.ManagedFileId };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            response?.Dispose();
            if (response == null && opened.Content != null) await opened.Content.DisposeAsync();
            return null;
        }
        catch
        {
            response?.Dispose();
            if (response == null && opened.Content != null) await opened.Content.DisposeAsync();
            throw;
        }
    }
}

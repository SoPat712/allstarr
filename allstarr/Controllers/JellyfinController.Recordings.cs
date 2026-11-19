using allstarr.Core.Capabilities;
using allstarr.Services.Local;
using allstarr.Core.Protocols;

namespace allstarr.Controllers;

public partial class JellyfinController
{
    private async Task<string?> ResolveKnownNativeTrackAsync(string publicId)
    {
        if (_libraryIndex == null) return null;
        var native = await _libraryIndex.ResolveKnownTrackAsync(HttpContext.RequireProtocolExecutionContext(), publicId, HttpContext.RequestAborted);
        return native != null && await HttpContext.RequestServices.GetRequiredService<CleanOnlyProtocolContent>().AllowsAsync(HttpContext, native)
            ? native : null;
    }

    // A play of a song only shown so far, from a service that can't play it, names its recording first, so a copy in
    // the library plays instead of another service's.
    private async Task<string?> ResolvePlayableNativeTrackAsync(string publicId)
    {
        if (await ResolveKnownNativeTrackAsync(publicId) is { } native) return native;
        var parsed = LocalLibraryService.ParseExternalResource(publicId);
        if (!parsed.isExternal || parsed.type != "song" || parsed.provider is not { } provider || parsed.externalId is not { } external ||
            HttpContext.RequestServices.GetService<IProviderRegistry>() is not { } registry || !RecordingOnlySongs.IsRecordingOnly(registry, provider))
            return null;
        try
        {
            if (await _providerGateway.ResolveRecordingOnlySongAsync(HttpContext.RequireProtocolExecutionContext(), provider, external) == null)
                return null;
        }
        catch (Exception error) when (error is not OperationCanceledException || !HttpContext.RequestAborted.IsCancellationRequested)
        {
            return null;
        }
        return await ResolveKnownNativeTrackAsync(publicId);
    }

    // An app that asked for a service song names that song as its media source too. The media server reads that value
    // as one of its own ids and fails on anything else, so a request passed on for the library copy names the copy.
    private string NativeQuery(string nativeId)
    {
        if (!Request.QueryString.HasValue) return "";
        return QueryString.Create(Request.Query.SelectMany(pair => pair.Value.Select(value => new KeyValuePair<string, string?>(pair.Key,
            pair.Key.Equals("MediaSourceId", StringComparison.OrdinalIgnoreCase) && !Guid.TryParse(value, out _) ? nativeId : value)))).Value ?? "";
    }
}

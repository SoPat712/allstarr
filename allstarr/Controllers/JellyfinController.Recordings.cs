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
}

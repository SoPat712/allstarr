using allstarr.Core.Protocols;

namespace allstarr.Controllers;

public partial class JellyfinController
{
    private Task<string?> ResolveKnownNativeTrackAsync(string publicId) => _libraryIndex == null
        ? Task.FromResult<string?>(null)
        : _libraryIndex.ResolveKnownTrackAsync(HttpContext.RequireProtocolExecutionContext(), publicId, HttpContext.RequestAborted);
}

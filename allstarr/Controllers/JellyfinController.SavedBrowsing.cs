using allstarr.Core.Protocols;

namespace allstarr.Controllers;

public partial class JellyfinController
{
    // Hearted service items belong to no library, so any music library the listener can read includes them, as browsing
    // without a library does.
    private async Task<bool> IsListenerMusicLibraryAsync(string parent)
    {
        if (parent == _settings.LibraryId) return true;
        if (_libraryAccess == null || HttpContext.GetProtocolExecutionContext() is not { } execution) return false;
        var access = await _libraryAccess.ResolveAsync(execution, HttpContext.RequestAborted);
        return access.Succeeded && access.LibraryIds.Any(id => id == parent ||
            Guid.TryParse(id, out var library) && Guid.TryParse(parent, out var requested) && library == requested);
    }
}

namespace allstarr.Services.Subsonic;

/// <summary>
/// Music-only allowlist for Subsonic methods that Allstarr relays to the backend unchanged.
/// Methods answered by Allstarr itself have their own routes; everything else, including
/// video, podcasts, internet radio, shares, chat and server administration, is refused.
/// </summary>
public static class SubsonicMusicEndpointPolicy
{
    private const string MethodPrefix = "rest/";
    private const string ViewSuffix = ".view";

    public static readonly IReadOnlySet<string> RelayedMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "createBookmark",
        "createPlaylist",
        "deleteBookmark",
        "deletePlaylist",
        "getAlbumList",
        "getArtists",
        "getAvatar",
        "getBookmarks",
        "getLicense",
        "getLyrics",
        "getMusicFolders",
        "getScanStatus",
        "getStarred",
        "getStarred2",
        "getTranscodeStream",
        "getUser",
        "search",
        "tokenInfo"
    };

    /// <summary>Returns the Subsonic method name for a relay path such as <c>rest/getArtists.view</c>.</summary>
    public static string? MethodName(string? endpoint)
    {
        var path = endpoint?.Trim().TrimStart('/');
        if (string.IsNullOrEmpty(path) || !path.StartsWith(MethodPrefix, StringComparison.OrdinalIgnoreCase))
            return null;
        var method = path[MethodPrefix.Length..];
        if (method.EndsWith(ViewSuffix, StringComparison.OrdinalIgnoreCase))
            method = method[..^ViewSuffix.Length];
        return method.Length == 0 || method.Contains('/') ? null : method;
    }

    public static bool CanRelay(string? endpoint) =>
        MethodName(endpoint) is { } method && RelayedMethods.Contains(method);
}

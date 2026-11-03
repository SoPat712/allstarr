namespace allstarr.Core.Identity;

internal static class JellyfinPlaylistSignIn
{
    public static string Authorization(Guid userId, string? token = null) =>
        $"MediaBrowser Client=\"Allstarr\", Device=\"Allstarr playlist editing\", " +
        $"DeviceId=\"allstarr-playlist-{userId:N}\", Version=\"{AppVersion.Version}\"" +
        (token == null ? string.Empty : $", Token=\"{token}\"");
}

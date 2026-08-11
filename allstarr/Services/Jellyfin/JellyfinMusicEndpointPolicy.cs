using Microsoft.AspNetCore.Http;
using System.Globalization;

namespace allstarr.Services.Jellyfin;

public enum JellyfinEndpointAccess
{
    Music,
    ClientControl,
    RequiresMusicItem,
    RequiresPlaylistItem,
    Denied
}

public sealed record JellyfinEndpointDecision(
    JellyfinEndpointAccess Access,
    string Reason)
{
    public bool Allowed => Access != JellyfinEndpointAccess.Denied;
}

/// <summary>
/// Defines the public Jellyfin compatibility surface exposed by Allstarr.
/// The proxy is intentionally music-only; an unclassified route is denied.
/// </summary>
public static class JellyfinMusicEndpointPolicy
{
    public const string DefaultMusicItemTypes = "Audio,MusicAlbum,MusicArtist,Playlist,MusicGenre";

    private static readonly HashSet<string> MusicItemTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Audio",
        "MusicAlbum",
        "MusicArtist",
        "Playlist",
        "MusicGenre"
    };

    private static readonly string[] AlwaysDeniedPrefixes =
    {
        "api/admin",
        "videos",
        "movies",
        "shows",
        "episodes",
        "trailers",
        "persons",
        "livetv",
        "channels",
        "collections",
        "syncplay",
        "plugins",
        "scheduledtasks",
        "startup",
        "branding",
        "notifications",
        "system/restart",
        "system/shutdown",
        "system/configuration",
        "system/logs",
        "system/activitylog",
        "library/refresh",
        "library/virtualfolders",
        "users/new"
    };

    public static JellyfinEndpointDecision Evaluate(HttpRequest request)
    {
        var path = Normalize(request.Path.Value);
        var method = request.Method;

        if (path.Equals("web/configurationpage", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("web/configurationpages", StringComparison.OrdinalIgnoreCase))
        {
            return Deny("Dashboard configuration APIs are outside the client bootstrap surface.");
        }

        if (string.IsNullOrEmpty(path) || path.Equals("web", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("web/", StringComparison.OrdinalIgnoreCase))
        {
            return Control("Jellyfin web bootstrap");
        }

        if (AlwaysDeniedPrefixes.Any(prefix => MatchesPrefix(path, prefix)))
        {
            return Deny("The route is outside Allstarr's music-only Jellyfin surface.");
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 2 &&
            segments[0].Equals("playlists", StringComparison.OrdinalIgnoreCase) &&
            IsSynthesizedPlaylistId(segments[1]) &&
            !SupportsSynthesizedPlaylistRoute(request, segments[1]))
        {
            return Deny("The synthesized playlist route is not implemented by Allstarr.");
        }

        if (segments.Length >= 2 &&
            !segments[0].Equals("playlists", StringComparison.OrdinalIgnoreCase) &&
            IsSynthesizedMusicItemId(segments[1]) &&
            !SupportsSynthesizedItemRoute(request, segments[1]))
        {
            return Deny("The synthesized resource route is not implemented by Allstarr.");
        }

        if (IsClientControlRoute(method, path))
        {
            return Control("Required for authentication or audio-client operation.");
        }

        if (IsDirectMusicRoute(method, path))
        {
            return Music("The route is explicitly music-scoped.");
        }

        if (IsPlaylistItemRoute(method, path))
        {
            return RequiresPlaylist("Updating or deleting a library item is permitted only when it is a playlist.");
        }

        if (IsMusicItemRoute(method, path))
        {
            return RequiresItem("The route is permitted only when the referenced item is music-related.");
        }

        if (IsMusicQueryRoute(method, path, request.Query))
        {
            return Music("The generic route is explicitly constrained to music item types.");
        }

        return Deny("The route is not part of the supported music-only Jellyfin surface.");
    }

    public static bool IsMusicItemType(string? itemType) =>
        !string.IsNullOrWhiteSpace(itemType) && MusicItemTypes.Contains(itemType);

    public static bool IsSynthesizedMusicItemId(string itemId) =>
        itemId.StartsWith("ext-", StringComparison.OrdinalIgnoreCase) ||
        itemId.StartsWith("vplaylist-", StringComparison.OrdinalIgnoreCase) ||
        itemId.StartsWith("allstarr-vpl-", StringComparison.OrdinalIgnoreCase);

    public static bool IsSynthesizedPlaylistId(string itemId) =>
        itemId.StartsWith("vplaylist-", StringComparison.OrdinalIgnoreCase) ||
        itemId.StartsWith("allstarr-vpl-", StringComparison.OrdinalIgnoreCase) ||
        (itemId.StartsWith("ext-", StringComparison.OrdinalIgnoreCase) &&
         itemId.Contains("-playlist-", StringComparison.OrdinalIgnoreCase));

    public static bool SupportsSynthesizedPlaylistRoute(HttpRequest request, string playlistId)
    {
        var segments = Normalize(request.Path.Value)
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 ||
            !segments[0].Equals("playlists", StringComparison.OrdinalIgnoreCase) ||
            !segments[1].Equals(playlistId, StringComparison.OrdinalIgnoreCase))
            return false;

        if (HttpMethods.IsGet(request.Method))
        {
            return segments.Length == 2 ||
                   segments.Length == 3 &&
                   (segments[2].Equals("items", StringComparison.OrdinalIgnoreCase) ||
                    segments[2].Equals("users", StringComparison.OrdinalIgnoreCase) ||
                    segments[2].Equals("instantmix", StringComparison.OrdinalIgnoreCase)) ||
                   segments.Length == 4 &&
                   segments[2].Equals("users", StringComparison.OrdinalIgnoreCase);
        }

        if (HttpMethods.IsPost(request.Method))
        {
            return segments.Length == 2 ||
                   segments.Length == 3 &&
                   segments[2].Equals("items", StringComparison.OrdinalIgnoreCase) ||
                   segments.Length == 4 &&
                   segments[2].Equals("users", StringComparison.OrdinalIgnoreCase) ||
                   segments.Length == 6 &&
                   segments[2].Equals("items", StringComparison.OrdinalIgnoreCase) &&
                   segments[4].Equals("move", StringComparison.OrdinalIgnoreCase);
        }

        return HttpMethods.IsDelete(request.Method) &&
               (segments.Length == 3 &&
                segments[2].Equals("items", StringComparison.OrdinalIgnoreCase) ||
                segments.Length == 4 &&
                segments[2].Equals("users", StringComparison.OrdinalIgnoreCase));
    }

    public static bool SupportsSynthesizedItemRoute(HttpRequest request, string itemId)
    {
        var path = Normalize(request.Path.Value);
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
            return false;
        var directItem = segments[1].Equals(itemId, StringComparison.OrdinalIgnoreCase);
        var userScopedItem = segments.Length == 4 &&
                             segments[0].Equals("users", StringComparison.OrdinalIgnoreCase) &&
                             segments[3].Equals(itemId, StringComparison.OrdinalIgnoreCase);
        if (!directItem && !userScopedItem)
            return false;

        var method = request.Method;
        var isGet = HttpMethods.IsGet(method);
        var isRead = isGet || HttpMethods.IsHead(method);
        var isSong = IsSynthesizedSongId(itemId);

        if (segments[0].Equals("items", StringComparison.OrdinalIgnoreCase))
        {
            if (segments.Length == 2) return isRead;
            if (segments[2].Equals("images", StringComparison.OrdinalIgnoreCase))
                return isRead && SupportsSynthesizedImageRoute(segments);
            if (segments[2].Equals("download", StringComparison.OrdinalIgnoreCase) ||
                segments[2].Equals("file", StringComparison.OrdinalIgnoreCase))
                return isSong && isRead && segments.Length == 3;
            if (segments[2].Equals("playbackinfo", StringComparison.OrdinalIgnoreCase))
                return isSong && (isGet || HttpMethods.IsPost(method)) && segments.Length == 3;
            if (!isGet || segments.Length != 3) return false;
            if (segments[2].Equals("similar", StringComparison.OrdinalIgnoreCase))
                return isSong || IsSynthesizedArtistId(itemId);
            return isSong &&
                   (segments[2].Equals("instantmix", StringComparison.OrdinalIgnoreCase) ||
                    segments[2].Equals("lyrics", StringComparison.OrdinalIgnoreCase));
        }

        if (segments[0].Equals("audio", StringComparison.OrdinalIgnoreCase) && segments.Length == 3)
        {
            return isSong && (segments[2].StartsWith("stream", StringComparison.OrdinalIgnoreCase)
                ? isRead
                : segments[2].Equals("universal", StringComparison.OrdinalIgnoreCase)
                    ? isRead
                    : segments[2].Equals("lyrics", StringComparison.OrdinalIgnoreCase) && isGet);
        }

        if (segments[0].Equals("songs", StringComparison.OrdinalIgnoreCase))
        {
            return isSong && isGet && segments.Length == 3 &&
                   (segments[2].Equals("instantmix", StringComparison.OrdinalIgnoreCase) ||
                    segments[2].Equals("similar", StringComparison.OrdinalIgnoreCase));
        }

        if (segments[0].Equals("albums", StringComparison.OrdinalIgnoreCase))
        {
            return IsSynthesizedAlbumId(itemId) && isGet && segments.Length == 3 &&
                   segments[2].Equals("instantmix", StringComparison.OrdinalIgnoreCase);
        }

        if (segments[0].Equals("artists", StringComparison.OrdinalIgnoreCase))
        {
            return IsSynthesizedArtistId(itemId) && isGet &&
                   (segments.Length == 2 ||
                    segments.Length == 3 &&
                    (segments[2].Equals("instantmix", StringComparison.OrdinalIgnoreCase) ||
                     segments[2].Equals("similar", StringComparison.OrdinalIgnoreCase)));
        }

        if (segments.Length == 4 &&
            segments[0].Equals("users", StringComparison.OrdinalIgnoreCase) &&
            segments[2].Equals("items", StringComparison.OrdinalIgnoreCase))
            return isRead;

        return itemId.StartsWith("ext-", StringComparison.OrdinalIgnoreCase) &&
               (HttpMethods.IsPost(method) || HttpMethods.IsDelete(method)) &&
               ((segments.Length == 4 &&
                 segments[0].Equals("users", StringComparison.OrdinalIgnoreCase) &&
                 segments[2].Equals("favoriteitems", StringComparison.OrdinalIgnoreCase)) ||
                (segments.Length == 2 &&
                 segments[0].Equals("userfavoriteitems", StringComparison.OrdinalIgnoreCase)));
    }

    private static bool IsSynthesizedSongId(string itemId)
    {
        if (!itemId.StartsWith("ext-", StringComparison.OrdinalIgnoreCase)) return false;
        if (itemId.Contains("-song-", StringComparison.OrdinalIgnoreCase)) return true;
        return !itemId.Contains("-album-", StringComparison.OrdinalIgnoreCase) &&
               !itemId.Contains("-artist-", StringComparison.OrdinalIgnoreCase) &&
               !itemId.Contains("-playlist-", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSynthesizedArtistId(string itemId) =>
        itemId.StartsWith("ext-", StringComparison.OrdinalIgnoreCase) &&
        itemId.Contains("-artist-", StringComparison.OrdinalIgnoreCase);

    private static bool IsSynthesizedAlbumId(string itemId) =>
        itemId.StartsWith("ext-", StringComparison.OrdinalIgnoreCase) &&
        itemId.Contains("-album-", StringComparison.OrdinalIgnoreCase);

    private static bool SupportsSynthesizedImageRoute(string[] segments)
    {
        if (segments.Length < 4 ||
            !segments[3].Equals("primary", StringComparison.OrdinalIgnoreCase))
            return false;
        if (segments.Length == 4) return true;
        if (segments.Length == 5) return segments[4] == "0";
        if (segments.Length != 11 ||
            segments[4] != "0" ||
            !new[] { "jpg", "jpeg", "png", "webp" }.Contains(
                segments[6], StringComparer.OrdinalIgnoreCase) ||
            !int.TryParse(segments[7], NumberStyles.None, CultureInfo.InvariantCulture, out var width) ||
            !int.TryParse(segments[8], NumberStyles.None, CultureInfo.InvariantCulture, out var height) ||
            width is < 0 or > 4096 ||
            height is < 0 or > 4096 ||
            !double.TryParse(segments[9], NumberStyles.Float, CultureInfo.InvariantCulture, out var played) ||
            played != 0)
            return false;

        return int.TryParse(
                   segments[10], NumberStyles.Integer, CultureInfo.InvariantCulture, out var unplayed) &&
               unplayed == 0;
    }

    public static bool ContainsOnlyMusicItemTypes(string? rawTypes)
    {
        if (string.IsNullOrWhiteSpace(rawTypes)) return false;

        var types = rawTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return types.Length > 0 && types.All(MusicItemTypes.Contains);
    }

    private static bool IsClientControlRoute(string method, string path)
    {
        if ((HttpMethods.IsGet(method) || HttpMethods.IsHead(method)) &&
            (path.Equals("system/info", StringComparison.OrdinalIgnoreCase) ||
             path.Equals("system/info/public", StringComparison.OrdinalIgnoreCase) ||
             path.Equals("system/endpoint", StringComparison.OrdinalIgnoreCase) ||
             path.Equals("getutctime", StringComparison.OrdinalIgnoreCase) ||
             path.Equals("users/me", StringComparison.OrdinalIgnoreCase) ||
             path.Equals("users/public", StringComparison.OrdinalIgnoreCase) ||
             IsUserProfile(path) ||
             path.Equals("userimage", StringComparison.OrdinalIgnoreCase) ||
             IsUserProfileImage(path)))
        {
            return true;
        }

        if ((HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsPost(method)) &&
            path.Equals("system/ping", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if ((HttpMethods.IsGet(method) || HttpMethods.IsHead(method)) &&
            (path.Equals("socket", StringComparison.OrdinalIgnoreCase) ||
             path.Equals("playback/bitratetest", StringComparison.OrdinalIgnoreCase) ||
             path.Equals("library/mediafolders", StringComparison.OrdinalIgnoreCase) ||
             path.Equals("userviews", StringComparison.OrdinalIgnoreCase) ||
             IsLegacyUserViews(path) ||
             path.Equals("userviews/groupingoptions", StringComparison.OrdinalIgnoreCase) ||
             path.Equals("items/root", StringComparison.OrdinalIgnoreCase) ||
             path.Equals("items/counts", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if ((HttpMethods.IsGet(method) || HttpMethods.IsPost(method)) &&
            MatchesPrefix(path, "displaypreferences")) return true;

        if (HttpMethods.IsPost(method) &&
            (path.Equals("users/authenticatebyname", StringComparison.OrdinalIgnoreCase) ||
             path.Equals("users/authenticatewithquickconnect", StringComparison.OrdinalIgnoreCase) ||
             path.Equals("sessions/logout", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if ((HttpMethods.IsGet(method) || HttpMethods.IsHead(method)) &&
            (path.Equals("quickconnect/enabled", StringComparison.OrdinalIgnoreCase) ||
             path.Equals("quickconnect/connect", StringComparison.OrdinalIgnoreCase)))
            return true;

        if (HttpMethods.IsPost(method) &&
            (path.Equals("quickconnect/initiate", StringComparison.OrdinalIgnoreCase) ||
             path.Equals("quickconnect/authorize", StringComparison.OrdinalIgnoreCase)))
            return true;

        if ((HttpMethods.IsGet(method) || HttpMethods.IsPost(method)) &&
            (path.Equals("sessions/capabilities", StringComparison.OrdinalIgnoreCase) ||
             path.Equals("sessions/capabilities/full", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (IsSessionRemoteControlRoute(method, path))
        {
            return true;
        }

        return HttpMethods.IsPost(method) &&
               (path.Equals("sessions/playing", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("sessions/playing/progress", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("sessions/playing/stopped", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("sessions/playing/ping", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("sessions/viewing", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSessionRemoteControlRoute(string method, string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 1 &&
            segments[0].Equals("sessions", StringComparison.OrdinalIgnoreCase))
        {
            return HttpMethods.IsGet(method);
        }

        if (!HttpMethods.IsPost(method) || segments.Length is < 3 or > 4 ||
            !segments[0].Equals("sessions", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(segments[1]))
        {
            return false;
        }

        if (segments.Length == 3)
        {
            return segments[2].Equals("command", StringComparison.OrdinalIgnoreCase) ||
                   segments[2].Equals("message", StringComparison.OrdinalIgnoreCase) ||
                   segments[2].Equals("playing", StringComparison.OrdinalIgnoreCase) ||
                   segments[2].Equals("viewing", StringComparison.OrdinalIgnoreCase);
        }

        return segments[2].Equals("command", StringComparison.OrdinalIgnoreCase) ||
               segments[2].Equals("playing", StringComparison.OrdinalIgnoreCase) ||
               segments[2].Equals("system", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDirectMusicRoute(string method, string path)
    {
        var isRead = HttpMethods.IsGet(method) || HttpMethods.IsHead(method);
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (isRead && segments.Length > 0 &&
            segments[0].Equals("artists", StringComparison.OrdinalIgnoreCase) &&
            (segments.Length <= 2 ||
             (segments.Length == 3 &&
              (segments[2].Equals("instantmix", StringComparison.OrdinalIgnoreCase) ||
               segments[2].Equals("similar", StringComparison.OrdinalIgnoreCase))) ||
             (segments.Length is 4 or 5 &&
              segments[2].Equals("images", StringComparison.OrdinalIgnoreCase))))
        {
            return true;
        }

        if (isRead && segments.Length == 3 &&
            (segments[0].Equals("albums", StringComparison.OrdinalIgnoreCase) ||
             segments[0].Equals("songs", StringComparison.OrdinalIgnoreCase)) &&
            (segments[2].Equals("instantmix", StringComparison.OrdinalIgnoreCase) ||
             segments[2].Equals("similar", StringComparison.OrdinalIgnoreCase)))
            return true;

        if (isRead && segments.Length > 0 &&
            (segments[0].Equals("genres", StringComparison.OrdinalIgnoreCase) ||
             segments[0].Equals("musicgenres", StringComparison.OrdinalIgnoreCase)) &&
            (segments.Length == 1 &&
             segments[0].Equals("musicgenres", StringComparison.OrdinalIgnoreCase) ||
             segments.Length == 2 ||
             (segments.Length == 3 &&
              segments[2].Equals("instantmix", StringComparison.OrdinalIgnoreCase)) ||
             (segments.Length is 4 or 5 &&
              segments[2].Equals("images", StringComparison.OrdinalIgnoreCase))))
            return true;

        if (isRead && segments.Length == 3 &&
            segments[0].Equals("providers", StringComparison.OrdinalIgnoreCase) &&
            segments[1].Equals("lyrics", StringComparison.OrdinalIgnoreCase))
            return true;

        if (isRead && segments.Length >= 2 &&
            segments[0].Equals("playlists", StringComparison.OrdinalIgnoreCase) &&
            (segments.Length == 2 ||
             (segments.Length == 3 &&
              (segments[2].Equals("items", StringComparison.OrdinalIgnoreCase) ||
               segments[2].Equals("users", StringComparison.OrdinalIgnoreCase) ||
               segments[2].Equals("instantmix", StringComparison.OrdinalIgnoreCase))) ||
             (segments.Length == 4 &&
              segments[2].Equals("users", StringComparison.OrdinalIgnoreCase))))
            return true;

        if (HttpMethods.IsPost(method) && segments.Length == 1 &&
            segments[0].Equals("playlists", StringComparison.OrdinalIgnoreCase))
            return true;

        if ((HttpMethods.IsPost(method) || HttpMethods.IsDelete(method)) &&
            segments.Length >= 2 &&
            segments[0].Equals("playlists", StringComparison.OrdinalIgnoreCase) &&
            ((segments.Length == 2 && HttpMethods.IsPost(method)) ||
             (segments.Length == 3 &&
              segments[2].Equals("items", StringComparison.OrdinalIgnoreCase)) ||
             (segments.Length == 4 &&
              segments[2].Equals("users", StringComparison.OrdinalIgnoreCase)) ||
             (segments.Length == 6 && HttpMethods.IsPost(method) &&
              segments[2].Equals("items", StringComparison.OrdinalIgnoreCase) &&
              segments[4].Equals("move", StringComparison.OrdinalIgnoreCase))))
            return true;

        if (HttpMethods.IsPost(method) &&
            (path.Equals("items/remotesearch/musicalbum", StringComparison.OrdinalIgnoreCase) ||
             path.Equals("items/remotesearch/musicartist", StringComparison.OrdinalIgnoreCase))) return true;

        return (HttpMethods.IsGet(method) || HttpMethods.IsHead(method)) && IsSpecificGenre(path);
    }

    private static bool IsMusicItemRoute(string method, string path)
    {
        if (!(HttpMethods.IsGet(method) || HttpMethods.IsHead(method) ||
              HttpMethods.IsPost(method) || HttpMethods.IsDelete(method)))
        {
            return false;
        }

        var isRead = HttpMethods.IsGet(method) || HttpMethods.IsHead(method);
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2) return false;

        if (segments[0].Equals("audio", StringComparison.OrdinalIgnoreCase)) return isRead;

        if (segments[0].Equals("playingitems", StringComparison.OrdinalIgnoreCase))
        {
            return segments.Length == 2 &&
                   (HttpMethods.IsPost(method) || HttpMethods.IsDelete(method)) ||
                   segments.Length == 3 &&
                   HttpMethods.IsPost(method) &&
                   segments[2].Equals("progress", StringComparison.OrdinalIgnoreCase);
        }

        if (segments[0].Equals("items", StringComparison.OrdinalIgnoreCase) &&
            !ReservedItemsSegment(segments[1]) &&
            HasAllowedItemSuffix(method, segments))
        {
            return true;
        }

        if (segments.Length == 4 &&
            segments[0].Equals("users", StringComparison.OrdinalIgnoreCase) &&
            segments[2].Equals("items", StringComparison.OrdinalIgnoreCase)) return true;

        if (segments.Length == 4 &&
            segments[0].Equals("users", StringComparison.OrdinalIgnoreCase) &&
            segments[2].Equals("favoriteitems", StringComparison.OrdinalIgnoreCase)) return true;

        if (segments[0].Equals("useritems", StringComparison.OrdinalIgnoreCase) &&
            segments[1].Equals("resume", StringComparison.OrdinalIgnoreCase)) return false;

        return segments.Length >= 2 &&
               (segments[0].Equals("userfavoriteitems", StringComparison.OrdinalIgnoreCase) ||
                segments[0].Equals("userplayeditems", StringComparison.OrdinalIgnoreCase) ||
                segments[0].Equals("useritems", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsPlaylistItemRoute(string method, string path)
    {
        if (!HttpMethods.IsPost(method) && !HttpMethods.IsDelete(method)) return false;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 2 &&
               segments[0].Equals("items", StringComparison.OrdinalIgnoreCase) &&
               !ReservedItemsSegment(segments[1]);
    }

    public static string? ReferencedItemId(string? rawPath)
    {
        var path = Normalize(rawPath);
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2) return null;

        if (segments[0].Equals("items", StringComparison.OrdinalIgnoreCase) &&
            !ReservedItemsSegment(segments[1])) return segments[1];
        if (segments[0].Equals("audio", StringComparison.OrdinalIgnoreCase)) return segments[1];
        if (segments[0].Equals("playingitems", StringComparison.OrdinalIgnoreCase)) return segments[1];
        if (segments.Length >= 4 && segments[0].Equals("users", StringComparison.OrdinalIgnoreCase) &&
            segments[2].Equals("items", StringComparison.OrdinalIgnoreCase)) return segments[3];
        if (segments.Length >= 4 && segments[0].Equals("users", StringComparison.OrdinalIgnoreCase) &&
            segments[2].Equals("favoriteitems", StringComparison.OrdinalIgnoreCase)) return segments[3];
        if (segments[0].Equals("useritems", StringComparison.OrdinalIgnoreCase) &&
            segments[1].Equals("resume", StringComparison.OrdinalIgnoreCase)) return null;
        if (segments[0].Equals("userfavoriteitems", StringComparison.OrdinalIgnoreCase) ||
            segments[0].Equals("userplayeditems", StringComparison.OrdinalIgnoreCase) ||
            segments[0].Equals("useritems", StringComparison.OrdinalIgnoreCase)) return segments[1];
        return null;
    }

    private static bool ReservedItemsSegment(string segment) =>
        segment.Equals("latest", StringComparison.OrdinalIgnoreCase) ||
        segment.Equals("suggestions", StringComparison.OrdinalIgnoreCase) ||
        segment.Equals("filters", StringComparison.OrdinalIgnoreCase) ||
        segment.Equals("filters2", StringComparison.OrdinalIgnoreCase) ||
        segment.Equals("counts", StringComparison.OrdinalIgnoreCase) ||
        segment.Equals("root", StringComparison.OrdinalIgnoreCase) ||
        segment.Equals("remotesearch", StringComparison.OrdinalIgnoreCase);

    private static bool HasAllowedItemSuffix(string method, string[] segments)
    {
        var isRead = HttpMethods.IsGet(method) || HttpMethods.IsHead(method);
        if (segments.Length == 2) return isRead;
        var suffix = segments[2];
        if (!isRead)
        {
            return HttpMethods.IsPost(method) &&
                   suffix.Equals("playbackinfo", StringComparison.OrdinalIgnoreCase);
        }

        return suffix.Equals("images", StringComparison.OrdinalIgnoreCase) ||
               suffix.Equals("download", StringComparison.OrdinalIgnoreCase) ||
               suffix.Equals("file", StringComparison.OrdinalIgnoreCase) ||
               suffix.Equals("similar", StringComparison.OrdinalIgnoreCase) ||
               suffix.Equals("collections", StringComparison.OrdinalIgnoreCase) ||
               suffix.Equals("instantmix", StringComparison.OrdinalIgnoreCase) ||
               suffix.Equals("playbackinfo", StringComparison.OrdinalIgnoreCase) ||
               suffix.Equals("lyrics", StringComparison.OrdinalIgnoreCase) ||
               suffix.Equals("themesongs", StringComparison.OrdinalIgnoreCase) ||
               suffix.Equals("ancestors", StringComparison.OrdinalIgnoreCase) ||
               suffix.Equals("remoteimages", StringComparison.OrdinalIgnoreCase) ||
               suffix.Equals("externalidinfos", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMusicQueryRoute(string method, string path, IQueryCollection query)
    {
        if (!(HttpMethods.IsGet(method) || HttpMethods.IsHead(method))) return false;

        var isItems = path.Equals("items", StringComparison.OrdinalIgnoreCase) || IsUserItems(path);
        var isHints = path.Equals("search/hints", StringComparison.OrdinalIgnoreCase) || IsUserSearchHints(path);
        var usesIncludeTypes = path.Equals("items/latest", StringComparison.OrdinalIgnoreCase) ||
                               path.Equals("items/filters", StringComparison.OrdinalIgnoreCase) ||
                               path.Equals("items/filters2", StringComparison.OrdinalIgnoreCase) ||
                               path.Equals("genres", StringComparison.OrdinalIgnoreCase);
        var isResume = path.Equals("useritems/resume", StringComparison.OrdinalIgnoreCase);
        var isSuggestions = path.Equals("items/suggestions", StringComparison.OrdinalIgnoreCase);
        if (!isItems && !isHints && !usesIncludeTypes && !isSuggestions && !isResume) return false;

        if (isItems)
        {
            var requestedTypes = query["IncludeItemTypes"].ToString();
            return string.IsNullOrWhiteSpace(requestedTypes) || ContainsOnlyMusicItemTypes(requestedTypes);
        }
        if (isHints)
        {
            var requestedTypes = query["IncludeItemTypes"].ToString();
            return string.IsNullOrWhiteSpace(requestedTypes) || ContainsOnlyMusicItemTypes(requestedTypes);
        }

        if (usesIncludeTypes || isResume)
        {
            var requestedTypes = query["IncludeItemTypes"].ToString();
            return string.IsNullOrWhiteSpace(requestedTypes) || ContainsOnlyMusicItemTypes(requestedTypes);
        }

        var suggestionTypes = query["Type"].ToString();
        return string.IsNullOrWhiteSpace(suggestionTypes) || ContainsOnlyMusicItemTypes(suggestionTypes);
    }

    private static bool IsUserItems(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 3 &&
               segments[0].Equals("users", StringComparison.OrdinalIgnoreCase) &&
               segments[2].Equals("items", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUserSearchHints(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 4 &&
               segments[0].Equals("users", StringComparison.OrdinalIgnoreCase) &&
               segments[2].Equals("search", StringComparison.OrdinalIgnoreCase) &&
               segments[3].Equals("hints", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUserProfileImage(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 4 &&
               segments[0].Equals("users", StringComparison.OrdinalIgnoreCase) &&
               segments[2].Equals("images", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUserProfile(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 2 &&
               segments[0].Equals("users", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLegacyUserViews(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 3 &&
               segments[0].Equals("users", StringComparison.OrdinalIgnoreCase) &&
               segments[2].Equals("views", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSpecificGenre(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 1 &&
               segments[0].Equals("genres", StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesPrefix(string path, string prefix) =>
        path.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? path) => (path ?? string.Empty).Trim().Trim('/');

    private static JellyfinEndpointDecision Music(string reason) =>
        new(JellyfinEndpointAccess.Music, reason);

    private static JellyfinEndpointDecision Control(string reason) =>
        new(JellyfinEndpointAccess.ClientControl, reason);

    private static JellyfinEndpointDecision RequiresItem(string reason) =>
        new(JellyfinEndpointAccess.RequiresMusicItem, reason);

    private static JellyfinEndpointDecision RequiresPlaylist(string reason) =>
        new(JellyfinEndpointAccess.RequiresPlaylistItem, reason);

    private static JellyfinEndpointDecision Deny(string reason) =>
        new(JellyfinEndpointAccess.Denied, reason);
}

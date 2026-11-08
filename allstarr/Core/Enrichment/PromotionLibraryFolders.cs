using System.Text.Json;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using allstarr.Services.Jellyfin;

namespace allstarr.Core.Enrichment;

public sealed record PromotionLibraryFolderSource(string Name, string BackendPath);
public sealed record PromotionLibraryFolderDiscovery(bool Available, string? Reason, PromotionLibraryFolderSource[] Folders);
public sealed record PromotionLibraryFolder(string Name, string BackendPath, string Path, bool Mapped,
    bool Reachable, bool Writable, string Status, Guid? RootId, long? FreeBytes = null, bool Linked = false);
public sealed record PromotionLibraryFolderList(bool Available, string? Reason, PromotionLibraryFolder[] Folders);

public sealed class PromotionLibraryFolders(JellyfinProxyService jellyfin, PromotionRoots roots)
{
    public async Task<PromotionLibraryFolderDiscovery> DiscoverAsync(AdminAuthSession session, CancellationToken token = default)
    {
        RequireAdministrator(session);
        if (!session.BackendType.Equals("jellyfin", StringComparison.OrdinalIgnoreCase))
            return new(false, "backend-not-supported", []);
        if (string.IsNullOrWhiteSpace(session.JellyfinAccessToken)) return new(false, "backend-unavailable", []);
        try
        {
            var headers = new HeaderDictionary { ["Authorization"] = AuthHeaderHelper.CreateAuthHeader(session.JellyfinAccessToken) };
            var (body, status) = await jellyfin.GetJsonAsync("Library/VirtualFolders", clientHeaders: headers, cancellationToken: token);
            using (body)
            {
                if (status is < 200 or >= 300 || body?.RootElement.ValueKind != JsonValueKind.Array)
                    return new(false, "backend-unavailable", []);
                var folders = new List<PromotionLibraryFolderSource>();
                foreach (var library in body.RootElement.EnumerateArray())
                {
                    if (library.ValueKind != JsonValueKind.Object ||
                        !library.TryGetProperty("CollectionType", out var collection) || collection.ValueKind != JsonValueKind.String ||
                        !string.Equals(collection.GetString(), "music", StringComparison.OrdinalIgnoreCase)) continue;
                    var name = library.TryGetProperty("Name", out var label) && label.ValueKind == JsonValueKind.String
                        ? label.GetString() : null;
                    if (library.TryGetProperty("Locations", out var locations) && locations.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var location in locations.EnumerateArray())
                            if (location.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(location.GetString()))
                                folders.Add(new(name ?? "Music", WithoutTrailingSeparator(location.GetString()!)));
                    }
                    if (library.TryGetProperty("LibraryOptions", out var options) && options.ValueKind == JsonValueKind.Object &&
                        options.TryGetProperty("PathInfos", out var paths) && paths.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var path in paths.EnumerateArray())
                            if (path.ValueKind == JsonValueKind.Object && path.TryGetProperty("Path", out var value) &&
                                value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                                folders.Add(new(name ?? "Music", WithoutTrailingSeparator(value.GetString()!)));
                    }
                }
                var distinct = folders.DistinctBy(folder => folder.BackendPath, StringComparer.Ordinal)
                    .OrderBy(folder => folder.Name, StringComparer.OrdinalIgnoreCase).ThenBy(folder => folder.BackendPath, StringComparer.Ordinal).ToArray();
                return new(distinct.Length > 0, distinct.Length == 0 ? "no-music-folders" : null, distinct);
            }
        }
        catch (Exception error) when (error is HttpRequestException or JsonException || error is OperationCanceledException && !token.IsCancellationRequested)
        { return new(false, "backend-unavailable", []); }
    }

    public async Task<PromotionLibraryFolderList> ReadAsync(AdminAuthSession session, CancellationToken token = default)
    {
        var discovery = await DiscoverAsync(session, token);
        var approved = await roots.ReadAsync(token);
        var folders = discovery.Folders.Select(source =>
        {
            var mapped = approved.FirstOrDefault(root => WithoutTrailingSeparator(root.BackendPath ?? root.Path) == source.BackendPath);
            var followed = System.IO.Path.IsPathFullyQualified(source.BackendPath) ? PromotionRoots.FollowLinks(source.BackendPath) : source.BackendPath;
            var path = mapped?.Path ?? followed;
            var reachable = false;
            var writable = false;
            var status = "unreachable";
            long? free = null;
            if (System.IO.Path.IsPathFullyQualified(path))
            {
                if (!roots.IsWithinAllowedBase(path)) status = "outside-allowed-base";
                else
                {
                    try
                    {
                        PromotionRoots.RejectLinks(path);
                        reachable = Directory.Exists(path);
                        if (reachable)
                        {
                            roots.ValidateDirectory(path);
                            writable = true;
                            free = roots.FreeBytes(path);
                            status = free < roots.MinimumFreeBytes ? "almost-full" : "writable";
                        }
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
                    { status = reachable ? "not-writable" : "unreachable"; }
                }
            }
            return new PromotionLibraryFolder(source.Name, source.BackendPath, path,
                path != source.BackendPath, reachable, writable, status, mapped?.Id, free,
                followed != source.BackendPath && path == followed);
        }).ToArray();
        return new(discovery.Available, discovery.Reason ?? (folders.Any(folder => folder.Writable) ? null : "no-reachable-music-folder"), folders);
    }

    // Jellyfin can list the same folder with and without a trailing slash.
    public static string WithoutTrailingSeparator(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        return trimmed.Length == 0 ? path[..1] : trimmed;
    }

    private static void RequireAdministrator(AdminAuthSession session)
    {
        if (!session.IsAdministrator) throw new UnauthorizedAccessException("Administrator permissions required.");
    }
}

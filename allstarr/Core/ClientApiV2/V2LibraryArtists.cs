using System.Text.Json;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Capabilities;
using allstarr.Services.Common;
using allstarr.Services.Jellyfin;
using allstarr.Services.Local;
using System.Collections.Concurrent;

namespace allstarr.Core.ClientApiV2;

/// <summary>
/// Names service artists and albums by their service, and points a streamed song's artist at the listener's library
/// artist of the same name, so Go to Artist opens the library page. A service artist the listener marked as someone
/// else keeps its own page.
/// </summary>
public sealed class V2LibraryArtists(JellyfinProxyService jellyfin, IProviderRegistry registry, LibraryArtistIndex? links = null)
{
    private static readonly TimeSpan Kept = TimeSpan.FromMinutes(10);
    private static readonly ConcurrentDictionary<(string Backend, string Principal), (DateTimeOffset At, IReadOnlyDictionary<string, string> Names)> Listings = new();

    internal V2Provider? ServiceProvider(string? id, V2IdKind kind)
    {
        if (V2Ids.ServiceSource(id, kind) is not { } source) return null;
        var parsed = LocalLibraryService.ParseExternalResource(source);
        if (!parsed.isExternal || parsed.provider is not { Length: > 0 } provider) return null;
        var name = registry.TryGet(provider, out var descriptor) && descriptor != null ? descriptor.DisplayName : provider;
        var url = $"/allstarr/v2/providers/{Uri.EscapeDataString(provider)}/icon";
        return new(provider, name, url, url + "?variant=glyph");
    }

    internal async Task<IReadOnlyList<V2Track>> LinkAsync(HttpContext http, ProtocolExecutionContext execution, IReadOnlyList<V2Track> tracks)
    {
        if (!tracks.Any(track => track.IsExternal)) return tracks;
        var library = await ByNameAsync(http, execution);
        var owner = execution.Actor?.EffectiveUserId;
        var refused = new Dictionary<string, HashSet<(string, string)>>(StringComparer.Ordinal);
        async Task<V2Ref> Link(V2Ref artist)
        {
            if (artist.LibraryId != null) return artist;
            var key = FuzzyMatcher.NormalizeForMatching(artist.Name);
            if (key.Length == 0 || !library.TryGetValue(key, out var libraryId))
                return artist with { Provider = ServiceProvider(artist.Id, V2IdKind.Artist) };
            if (V2Ids.ServiceSource(artist.Id, V2IdKind.Artist) is { } source && owner is { } user && links != null &&
                LocalLibraryService.ParseExternalResource(source) is { isExternal: true, provider: { } provider, externalId: { } external })
            {
                if (!refused.TryGetValue(key, out var others))
                    refused[key] = others = (await links.ReadPartsAsync(user, artist.Name, execution.CancellationToken)).Fixes
                        .Where(fix => !fix.Same).Select(fix => (fix.ProviderId, fix.ArtistId)).ToHashSet();
                if (others.Contains((provider, external))) return artist with { Provider = ServiceProvider(artist.Id, V2IdKind.Artist) };
            }
            return new V2Ref(V2Ids.Artist(libraryId), artist.Name);
        }
        var result = new List<V2Track>(tracks.Count);
        foreach (var track in tracks)
        {
            if (!track.IsExternal)
            {
                result.Add(track);
                continue;
            }
            var artists = new List<V2Ref>();
            foreach (var artist in track.Artists) artists.Add(await Link(artist));
            var albumArtists = new List<V2Ref>();
            foreach (var artist in track.AlbumArtists) albumArtists.Add(await Link(artist));
            var featuring = new List<V2Ref>();
            foreach (var artist in track.Featuring) featuring.Add(await Link(artist));
            result.Add(track with
            {
                Artists = artists,
                AlbumArtists = albumArtists,
                Featuring = featuring,
                Album = track.Album is { } album ? album with { Provider = ServiceProvider(album.Id, V2IdKind.Album) } : null
            });
        }
        return result;
    }

    // One listing of the listener's library artists, kept briefly; a name two library artists share links to neither.
    private async Task<IReadOnlyDictionary<string, string>> ByNameAsync(HttpContext http, ProtocolExecutionContext execution)
    {
        var key = (execution.BackendInstanceId, execution.VerifiedBackendPrincipalId);
        if (Listings.TryGetValue(key, out var kept) && DateTimeOffset.UtcNow - kept.At < Kept) return kept.Names;
        var named = new Dictionary<string, string?>(StringComparer.Ordinal);
        try
        {
            var (document, status) = await jellyfin.GetJsonAsync("Artists", new()
            {
                ["userId"] = execution.VerifiedBackendPrincipalId,
                ["Recursive"] = "true",
                ["EnableImages"] = "false",
                ["EnableUserData"] = "false"
            }, http.Request.Headers, execution.CancellationToken);
            using (document)
            {
                if (status == 200 && document?.RootElement.TryGetProperty("Items", out var items) == true && items.ValueKind == JsonValueKind.Array)
                    foreach (var item in items.EnumerateArray())
                    {
                        var id = item.TryGetProperty("Id", out var idValue) ? idValue.GetString() : null;
                        var name = item.TryGetProperty("Name", out var nameValue) ? FuzzyMatcher.NormalizeForMatching(nameValue.GetString() ?? "") : "";
                        if (id == null || name.Length == 0 || id.StartsWith("ext-", StringComparison.Ordinal) || !V2Ids.IsValidSource(id, V2IdKind.Artist)) continue;
                        named[name] = named.ContainsKey(name) ? null : id;
                    }
            }
        }
        catch (HttpRequestException) { return new Dictionary<string, string>(); }
        var map = named.Where(item => item.Value != null).ToDictionary(item => item.Key, item => item.Value!, StringComparer.Ordinal);
        Listings[key] = (DateTimeOffset.UtcNow, map);
        return map;
    }
}

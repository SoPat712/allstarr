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
    private readonly ConcurrentDictionary<(string Backend, string Principal), (DateTimeOffset At, IReadOnlyDictionary<string, string> Names)> listings = new();

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
        var refused = new Dictionary<string, HashSet<string>?>(StringComparer.Ordinal);
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
                {
                    try
                    {
                        others = (await links.ReadPartsAsync(user, artist.Name, execution.CancellationToken)).Fixes
                            .Where(fix => !fix.Same).Select(fix => ServiceCatalog.Key(fix.ProviderId, fix.ArtistId)).ToHashSet(StringComparer.Ordinal);
                    }
                    // Without the listener's answers the service artist stays, rather than risk a link they refused.
                    catch (Exception error) when (error is not OperationCanceledException || !execution.CancellationToken.IsCancellationRequested)
                    {
                        others = null;
                    }
                    refused[key] = others;
                }
                if (others == null || others.Contains(ServiceCatalog.Key(provider, external))) return artist with { Provider = ServiceProvider(artist.Id, V2IdKind.Artist) };
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

    internal const string SameNameReason = "Same name. No matched song or shared album yet.";

    /// <summary>
    /// For a service artist: the library artist it is proven to be, or the same-named library artist it could be. A
    /// shared album among the albums the page already loaded is proof; a person's "not the same" leaves both empty.
    /// </summary>
    internal async Task<(V2LibraryArtistRef? Library, IReadOnlyList<V2LibraryArtistRef>? CouldBe)> IdentifyAsync(HttpContext http,
        ProtocolExecutionContext execution, string provider, string artistId, string name, IReadOnlyCollection<Models.Domain.Album>? albums)
    {
        if (links == null || execution.Actor?.EffectiveUserId is not { } owner) return (null, null);
        var key = FuzzyMatcher.NormalizeForMatching(name);
        if (key.Length == 0 || !(await ByNameAsync(http, execution)).TryGetValue(key, out var libraryId)) return (null, null);
        string? by;
        try
        {
            var proof = await links.ReadProofAsync(owner, name, execution.CancellationToken);
            var fix = proof.Fixes.FirstOrDefault(item => ServiceCatalog.Same(item.ProviderId, item.ArtistId, provider, artistId));
            if (fix is { Same: false }) return (null, null);
            by = fix != null ? "fix" : proof.Songs.Any(link => ServiceCatalog.Same(link.ProviderId, link.ArtistId, provider, artistId)) ? "songs"
                : proof.Albums.Any(link => ServiceCatalog.Same(link.ProviderId, link.ArtistId, provider, artistId)) ? "albums" : null;
            if (by == null && albums is { Count: > 0 } &&
                await links.ProveByAlbumsAsync(owner, name, provider, artistId, albums, execution.CancellationToken)) by = "albums";
        }
        // Without the saved ties, say nothing rather than suggest one a person refused.
        catch (Exception error) when (error is not OperationCanceledException || !execution.CancellationToken.IsCancellationRequested)
        {
            return (null, null);
        }
        V2Artwork? artwork = null;
        var shown = name;
        try
        {
            var (body, status) = await jellyfin.GetJsonAsync(
                $"Users/{Uri.EscapeDataString(execution.VerifiedBackendPrincipalId)}/Items/{Uri.EscapeDataString(libraryId)}",
                null, http.Request.Headers, execution.CancellationToken);
            using (body)
                if (status == 200 && body != null)
                {
                    if (body.RootElement.TryGetProperty("Name", out var libraryName) && libraryName.GetString() is { Length: > 0 } value) shown = value;
                    artwork = V2ArtworkUrls.For(libraryId, body.RootElement.TryGetProperty("ImageTags", out var tags) &&
                        tags.ValueKind == JsonValueKind.Object && tags.TryGetProperty("Primary", out var primary) ? primary.GetString() : null);
                }
        }
        catch (Exception error) when (error is not OperationCanceledException || !execution.CancellationToken.IsCancellationRequested) { }
        var library = new V2LibraryArtistRef(V2Ids.Artist(libraryId), shown, artwork);
        return by != null ? (library with { By = by }, null) : (null, [library with { Reason = SameNameReason }]);
    }

    // One listing of the listener's library artists, kept briefly; a name two library artists share links to neither.
    private async Task<IReadOnlyDictionary<string, string>> ByNameAsync(HttpContext http, ProtocolExecutionContext execution)
    {
        var key = (execution.BackendInstanceId, execution.VerifiedBackendPrincipalId);
        if (listings.TryGetValue(key, out var kept) && DateTimeOffset.UtcNow - kept.At < Kept) return kept.Names;
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
        // Linking is a convenience: a listing that fails for any reason leaves songs with their service artists.
        catch (Exception error) when (error is not OperationCanceledException || !execution.CancellationToken.IsCancellationRequested)
        {
            return new Dictionary<string, string>();
        }
        var map = named.Where(item => item.Value != null).ToDictionary(item => item.Key, item => item.Value!, StringComparer.Ordinal);
        listings[key] = (DateTimeOffset.UtcNow, map);
        return map;
    }
}

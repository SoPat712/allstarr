using allstarr.Core.Operations;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Protocols;
using allstarr.Core.Releases;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using allstarr.Services.Jellyfin;
using allstarr.Services.Local;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.ClientApi;

public sealed class ReleaseReader(NewReleasesService releases, IProtocolProviderGateway providers,
    IProviderRegistry registry, JellyfinProxyService jellyfin, IDbContextFactory<AllstarrDbContext> factory,
    IBackendLibraryPermissionSource permissions, IApplicationCache cache, ApplicationCacheRequestCoalescer readCoalescer,
    IBackendLibraryAccessResolver libraryAccess, IPlatformClock clock)
{
    public async Task<ClientReleases> ListAsync(HttpContext http, ProtocolExecutionContext execution)
    {
        using var merging = RequestTiming.Current?.Stage("merge");
        var access = await libraryAccess.ResolveAsync(execution, execution.CancellationToken);
        return (await FeedReadCache.ReadAsync<ClientReleases>(factory, cache, readCoalescer, clock, execution,
            "releases", access, http, async (request, context) =>
                await request!.RequestServices.GetRequiredService<ReleaseReader>().ListCoreAsync(context)))!;
    }

    private async Task<ClientReleases> ListCoreAsync(ProtocolExecutionContext execution)
    {
        var owner = execution.RequireActor().EffectiveUserId!.Value;
        await releases.EnsurePollingAsync(owner, execution.CancellationToken);
        var inbox = await releases.ReadAsync(owner, execution.CancellationToken);
        using var serialization = RequestTiming.Current?.Stage("serialize");
        var follows = inbox.Coverage.Select(item => item.Artist).ToArray();
        return new(inbox.Entries.Select(entry =>
        {
            var source = ReleaseInboxProjection.PrimarySource(entry.Sources);
            var artistId = source.ArtistId ?? follows.FirstOrDefault(artist => artist.ProviderId == source.ProviderId &&
                artist.DisplayName.Equals(source.Artist, StringComparison.OrdinalIgnoreCase))?.ArtistId;
            var library = follows.FirstOrDefault(artist => artist.LibraryArtistId != null &&
                artist.ProviderId == (source.ArtistProviderId ?? source.ProviderId) && artist.ArtistId == artistId);
            return new ClientRelease(entry.Id, entry.Title, entry.Type, entry.ReleaseDate,
                new(library?.LibraryArtistId ?? (artistId == null ? null : ClientCatalogIds.Resource(source.ArtistProviderId ?? source.ProviderId, ProviderResourceKind.Artist, artistId)),
                    library?.DisplayName ?? entry.Artist, library == null ? null : ArtistImage(library.LibraryArtistId, library.LibraryImageTag)),
                ClientCatalogIds.Resource(source.ProviderId, ProviderResourceKind.Album, source.AlbumId), $"/allstarr/v1/releases/{entry.Id}/cover");
        }).ToArray(), follows.Select(artist =>
        {
            var id = artist.LibraryArtistId ?? ClientCatalogIds.Resource(artist.ProviderId, ProviderResourceKind.Artist, artist.ArtistId);
            return new ClientReleaseArtist(id, artist.DisplayName, ArtistImage(id, artist.LibraryImageTag));
        }).ToArray());
    }

    // The picture the artist's own item shows, by the same image path, so apps load it through their usual artwork cache
    // instead of reading each artist first.
    private static string? ArtistImage(string? id, string? tag = null) => id == null ? null : id.StartsWith("ext-", StringComparison.Ordinal)
        ? $"/Items/{Uri.EscapeDataString(id)}/Images/Primary?tag={Uri.EscapeDataString(JellyfinItemArtwork.PrimaryImageTag(id, false))}"
        : $"/Items/{Uri.EscapeDataString(id)}/Images/Primary" + (string.IsNullOrWhiteSpace(tag) ? "" : $"?tag={Uri.EscapeDataString(tag)}");

    public async Task<ReleaseSource?> CoverAsync(ProtocolExecutionContext execution, string releaseId)
    {
        using var matching = RequestTiming.Current?.Stage("match");
        var inbox = await releases.ReadAsync(execution.RequireActor().EffectiveUserId!.Value, execution.CancellationToken);
        var entry = inbox.Entries.FirstOrDefault(item => item.Id == releaseId);
        return entry == null ? null : ReleaseInboxProjection.PrimarySource(entry.Sources);
    }

    public async Task<ClientArtistFollow?> FollowAsync(HttpContext http, ProtocolExecutionContext execution, string artistId, bool followed)
    {
        var owner = execution.RequireActor().EffectiveUserId!.Value;
        var artist = await ResolveArtistAsync(http, execution, artistId, followed);
        if (artist == null) return null;
        if (followed) await releases.FollowAsync(owner, artist, execution.CancellationToken);
        else await releases.UnfollowAsync(owner, artist.ProviderId, artist.ArtistId, execution.CancellationToken, artist.LibraryArtistId);
        var visibleId = artist.LibraryArtistId ?? artistId;
        return new(followed, new(visibleId, artist.DisplayName, ArtistImage(visibleId, artist.LibraryImageTag)));
    }

    private async Task<FollowedReleaseArtist?> ResolveArtistAsync(HttpContext http, ProtocolExecutionContext execution, string artistId, bool followed)
    {
        if (artistId.Length > 500 || artistId.Any(char.IsControl)) return null;
        var owner = execution.RequireActor().EffectiveUserId!.Value;
        var follows = await releases.ListAsync(owner, execution.CancellationToken);
        var parsed = LocalLibraryService.ParseExternalResource(artistId);
        if (parsed.isExternal)
        {
            if (parsed.type != "artist" || !registry.TryGet(parsed.provider!, out var descriptor)) return null;
            var provider = descriptor!.Id;
            if (follows.FirstOrDefault(item => item.LibraryArtistId == null && registry.ResolveProviderId(item.ProviderId) == provider && item.ArtistId == parsed.externalId) is { } existing) return existing;
            var artist = await providers.GetArtistAsync(execution, provider, parsed.externalId!);
            return string.IsNullOrWhiteSpace(artist?.Name) ? null : new(provider, parsed.externalId!, artist.Name);
        }
        if (artistId.StartsWith("ext-", StringComparison.Ordinal)) return null;
        var (body, status) = await jellyfin.GetJsonAsync(
            $"Users/{Uri.EscapeDataString(execution.VerifiedBackendPrincipalId)}/Items/{Uri.EscapeDataString(artistId)}",
            clientHeaders: http.Request.Headers, cancellationToken: execution.CancellationToken);
        using var native = body;
        if (status is 400 or 401 or 403 or 404) return null;
        if (status != 200 || body == null) throw new HttpRequestException("Library artist details are unavailable.");
        var root = body.RootElement;
        if (!root.TryGetProperty("Id", out var id) || !SameNativeId(id.GetString(), artistId) ||
            !root.TryGetProperty("Type", out var type) || type.GetString() != "MusicArtist" ||
            !root.TryGetProperty("Name", out var name) || string.IsNullOrWhiteSpace(name.GetString())) return null;
        var nativeId = id.GetString()!;
        var nativeName = name.GetString()!;
        var tag = root.TryGetProperty("ImageTags", out var tags) && tags.TryGetProperty("Primary", out var primary) ? primary.GetString() : null;
        var mbid = root.TryGetProperty("ProviderIds", out var ids) && ids.ValueKind == JsonValueKind.Object ? ids.EnumerateObject()
            .FirstOrDefault(item => item.Name.Equals("MusicBrainzArtist", StringComparison.OrdinalIgnoreCase)).Value : default;
        var libraryMbid = mbid.ValueKind == JsonValueKind.String ? mbid.GetString() : null;
        var libraryFollow = follows.FirstOrDefault(item => SameNativeId(item.LibraryArtistId, nativeId));
        var identifierChanged = !string.Equals(libraryFollow?.LibraryMusicBrainzArtistId, libraryMbid, StringComparison.OrdinalIgnoreCase);
        if (libraryFollow?.ListenerConfirmed == true)
            return libraryFollow with { LibraryArtistId = nativeId, DisplayName = nativeName, LibraryImageTag = tag, LibraryMusicBrainzArtistId = libraryMbid };
        if (followed && await releases.ResolveIndexedLibraryArtistAsync(owner, nativeName, execution.CancellationToken, libraryMbid) is { } indexed)
            return indexed with { LibraryArtistId = nativeId, DisplayName = nativeName, LibraryImageTag = tag, LibraryMusicBrainzArtistId = libraryMbid };
        if (libraryFollow != null && !identifierChanged)
            return libraryFollow with { LibraryArtistId = nativeId, DisplayName = nativeName, LibraryImageTag = tag };
        var unresolved = new FollowedReleaseArtist("jellyfin", nativeId, nativeName, MusicBrainzArtistId: libraryMbid,
            LibraryArtistId: nativeId, LibraryImageTag: tag, ResolutionReason: libraryMbid == null ? null :
                "The library artist ID is not supported by this artist's songs. Choose the right artist.", LibraryMusicBrainzArtistId: libraryMbid);
        if (!followed) return unresolved;
        try
        {
            var (catalog, catalogStatus) = await jellyfin.GetJsonAsync(
                $"Users/{Uri.EscapeDataString(execution.VerifiedBackendPrincipalId)}/Items",
                new() { ["ArtistIds"] = nativeId, ["IncludeItemTypes"] = "MusicAlbum,Audio", ["Recursive"] = "true", ["Limit"] = "200" },
                http.Request.Headers, execution.CancellationToken);
            using (catalog)
            {
                var albums = new List<string>();
                var songs = new List<string>();
                if (catalogStatus == 200 && catalog?.RootElement.TryGetProperty("Items", out var items) == true)
                    foreach (var item in items.EnumerateArray().Take(200))
                    {
                        if (!item.TryGetProperty("Name", out var title) || string.IsNullOrWhiteSpace(title.GetString()) ||
                            !item.TryGetProperty("Type", out var kind)) continue;
                        if (kind.GetString() == "MusicAlbum") albums.Add(title.GetString()!);
                        else if (kind.GetString() == "Audio") songs.Add(title.GetString()!);
                    }
                var service = await releases.ResolveLibraryArtistAsync(owner, nativeName,
                    libraryMbid, albums, songs, execution.CancellationToken);
                return service == null ? unresolved : service with
                {
                    DisplayName = nativeName,
                    LibraryArtistId = nativeId,
                    LibraryImageTag = tag,
                    LibraryMusicBrainzArtistId = libraryMbid
                };
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException) { return unresolved; }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTimeOffset> FavoritesChecked = new();
    private static readonly SemaphoreSlim FavoritesGate = new(1, 1);

    public async Task FollowFavoriteArtistsAsync(Guid owner, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == owner && item.Enabled &&
            item.BackendType == "jellyfin", token);
        if (user == null) return;
        var execution = new ProtocolExecutionContext(ProtocolKind.Jellyfin, user.BackendInstanceId, user.BackendPrincipalId,
            new AllstarrPrincipal(owner, user.BackendType, user.BackendInstanceId, user.BackendPrincipalId, user.DisplayName, user.IsAdmin),
            "release-favorites", DateTimeOffset.UtcNow.AddMinutes(10), token)
        { UseStoredBackendCredentials = true };
        using var authentication = await permissions.CreateRequestAsync(execution, token);
        if (authentication == null) return;
        var http = new DefaultHttpContext();
        foreach (var header in authentication.Headers) http.Request.Headers[header.Key] = header.Value.ToArray();
        await FollowFavoriteArtistsAsync(http, execution);
    }

    // The scheduled release check discovers favorites from other apps and repairs legacy library follows a few at a time.
    // Continuations check at most every ten minutes.
    public async Task FollowFavoriteArtistsAsync(HttpContext http, ProtocolExecutionContext execution)
    {
        if (execution.Protocol != ProtocolKind.Jellyfin) return;
        await FavoritesGate.WaitAsync(execution.CancellationToken);
        try
        {
            var owner = execution.RequireActor().EffectiveUserId!.Value;
            var now = DateTimeOffset.UtcNow;
            if (FavoritesChecked.TryGetValue(owner, out var last) && now - last < TimeSpan.FromMinutes(10)) return;
            FavoritesChecked[owner] = now;
            try
            {
                foreach (var library in (await releases.ListAsync(owner, execution.CancellationToken)).Where(item => item.LibraryArtistId != null))
                {
                    try
                    {
                        var refreshed = await ResolveArtistAsync(http, execution, library.LibraryArtistId!, true);
                        if (refreshed != null && (refreshed.DisplayName != library.DisplayName || refreshed.LibraryImageTag != library.LibraryImageTag ||
                            refreshed.LibraryMusicBrainzArtistId != library.LibraryMusicBrainzArtistId))
                            await releases.FollowAsync(owner, refreshed, execution.CancellationToken);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException) { }
                }
                await releases.FollowFavoriteArtistsAsync(owner, execution.CancellationToken);
                var legacy = (await releases.ListAsync(owner, execution.CancellationToken))
                    .Where(item => item.LibraryArtistId == null && item.IsDirectFollow == null).ToArray();
                var repaired = 0;
                foreach (var follow in legacy)
                {
                    if (repaired == 10) break;
                    var (matches, matchStatus) = await jellyfin.GetJsonAsync(
                        $"Users/{Uri.EscapeDataString(execution.VerifiedBackendPrincipalId)}/Items",
                        new() { ["SearchTerm"] = follow.DisplayName, ["IncludeItemTypes"] = "MusicArtist", ["Recursive"] = "true", ["Limit"] = "200" },
                        http.Request.Headers, execution.CancellationToken);
                    using (matches)
                    {
                        if (matchStatus != 200 || matches?.RootElement.TryGetProperty("Items", out var items) != true) continue;
                        foreach (var item in items.EnumerateArray())
                        {
                            if (!item.TryGetProperty("Type", out var kind) || kind.GetString() != "MusicArtist" ||
                                !item.TryGetProperty("Name", out var name) || string.IsNullOrWhiteSpace(name.GetString()) ||
                                FuzzyMatcher.NormalizeForMatching(name.GetString()!) != FuzzyMatcher.NormalizeForMatching(follow.DisplayName) ||
                                !item.TryGetProperty("Id", out var id) || string.IsNullOrWhiteSpace(id.GetString())) continue;
                            var artist = await ResolveArtistAsync(http, execution, id.GetString()!, true);
                            if (artist?.LibraryArtistId == null) continue;
                            await releases.FollowAsync(owner, artist, execution.CancellationToken, follow);
                            repaired++;
                            break;
                        }
                    }
                }
                var (body, status) = await jellyfin.GetJsonAsync(
                    $"Users/{Uri.EscapeDataString(execution.VerifiedBackendPrincipalId)}/Items",
                    new() { ["IsFavorite"] = "true", ["IncludeItemTypes"] = "MusicArtist", ["Recursive"] = "true", ["Limit"] = "200" },
                    http.Request.Headers, execution.CancellationToken);
                using (body)
                {
                    if (status != 200 || body?.RootElement.TryGetProperty("Items", out var items) != true) return;
                    var follows = await releases.ListAsync(owner, execution.CancellationToken);
                    var added = 0;
                    foreach (var item in items.EnumerateArray())
                    {
                        if (added == 10) break;
                        if (!item.TryGetProperty("Id", out var id) || id.GetString() is not { } artistId ||
                            !item.TryGetProperty("Name", out var name) || string.IsNullOrWhiteSpace(name.GetString()) ||
                            follows.Any(follow => SameNativeId(follow.LibraryArtistId, artistId) ||
                                follow.LibraryArtistId == null && $"ext-{follow.ProviderId}-artist-{follow.ArtistId}" == artistId)) continue;
                        if (await FollowAsync(http, execution, artistId, true) != null) added++;
                    }
                }
            }
            // Following favourites is a convenience; the releases list never fails because of it.
            catch (Exception exception) when (exception is not OperationCanceledException) { }
        }
        finally { FavoritesGate.Release(); }
    }

    private static bool SameNativeId(string? first, string second) => first == second ||
        Guid.TryParse(first, out var firstId) && Guid.TryParse(second, out var secondId) && firstId == secondId;
}

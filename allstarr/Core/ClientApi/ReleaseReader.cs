using allstarr.Core.Capabilities;
using allstarr.Core.Protocols;
using allstarr.Core.Releases;
using allstarr.Services.Jellyfin;
using allstarr.Services.Local;

namespace allstarr.Core.ClientApi;

public sealed class ReleaseReader(NewReleasesService releases, IProtocolProviderGateway providers,
    IProviderRegistry registry, JellyfinProxyService jellyfin)
{
    public async Task<ClientReleases> ListAsync(HttpContext http, ProtocolExecutionContext execution)
    {
        var owner = execution.RequireActor().EffectiveUserId!.Value;
        await FollowFavoriteArtistsAsync(http, execution);
        var inbox = await releases.ReadAsync(owner, execution.CancellationToken);
        var follows = await releases.ListAsync(owner, execution.CancellationToken);
        return new(inbox.Entries.Select(entry =>
        {
            var source = ReleaseInboxProjection.PrimarySource(entry.Sources);
            var artistId = source.ArtistId ?? follows.FirstOrDefault(artist => artist.ProviderId == source.ProviderId &&
                artist.DisplayName.Equals(source.Artist, StringComparison.OrdinalIgnoreCase))?.ArtistId;
            return new ClientRelease(entry.Id, entry.Title, entry.Type, entry.ReleaseDate,
                new(artistId == null ? null : ClientCatalogIds.Resource(source.ArtistProviderId ?? source.ProviderId, ProviderResourceKind.Artist, artistId), entry.Artist),
                ClientCatalogIds.Resource(source.ProviderId, ProviderResourceKind.Album, source.AlbumId), $"/allstarr/v1/releases/{entry.Id}/cover");
        }).ToArray(), follows.Select(artist =>
        {
            var id = ClientCatalogIds.Resource(artist.ProviderId, ProviderResourceKind.Artist, artist.ArtistId);
            return new ClientReleaseArtist(id, artist.DisplayName, ArtistImage(id));
        }).ToArray());
    }

    // The picture the artist's own item shows, by the same image path, so apps load it through their usual artwork cache
    // instead of reading each artist first.
    private static string? ArtistImage(string? id) => id == null ? null : id.StartsWith("ext-", StringComparison.Ordinal)
        ? $"/Items/{Uri.EscapeDataString(id)}/Images/Primary?tag={Uri.EscapeDataString(JellyfinItemArtwork.PrimaryImageTag(id, false))}"
        : $"/Items/{Uri.EscapeDataString(id)}/Images/Primary";

    public async Task<ReleaseSource?> CoverAsync(ProtocolExecutionContext execution, string releaseId)
    {
        var inbox = await releases.ReadAsync(execution.RequireActor().EffectiveUserId!.Value, execution.CancellationToken);
        var entry = inbox.Entries.FirstOrDefault(item => item.Id == releaseId);
        return entry == null ? null : ReleaseInboxProjection.PrimarySource(entry.Sources);
    }

    public async Task<ClientArtistFollow?> FollowAsync(HttpContext http, ProtocolExecutionContext execution, string artistId, bool followed)
    {
        var owner = execution.RequireActor().EffectiveUserId!.Value;
        var artist = await ResolveArtistAsync(http, execution, artistId);
        if (artist == null) return null;
        if (followed) await releases.FollowAsync(owner, artist, execution.CancellationToken);
        else await releases.UnfollowAsync(owner, artist.ProviderId, artist.ArtistId, execution.CancellationToken);
        return new(followed, new(artistId, artist.DisplayName, ArtistImage(artistId)));
    }

    private async Task<FollowedReleaseArtist?> ResolveArtistAsync(HttpContext http, ProtocolExecutionContext execution, string artistId)
    {
        if (artistId.Length > 500 || artistId.Any(char.IsControl)) return null;
        var owner = execution.RequireActor().EffectiveUserId!.Value;
        var follows = await releases.ListAsync(owner, execution.CancellationToken);
        var parsed = LocalLibraryService.ParseExternalResource(artistId);
        if (parsed.isExternal)
        {
            if (parsed.type != "artist" || !registry.TryGet(parsed.provider!, out var descriptor)) return null;
            var provider = descriptor!.Id;
            if (follows.FirstOrDefault(item => registry.ResolveProviderId(item.ProviderId) == provider && item.ArtistId == parsed.externalId) is { } existing) return existing;
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
        return follows.FirstOrDefault(item => item.DisplayName.Equals(name.GetString(), StringComparison.OrdinalIgnoreCase)) ??
            await releases.ResolveArtistByNameAsync(owner, name.GetString()!, execution.CancellationToken);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTimeOffset> FavoritesChecked = new();

    // Artists favourited before favouriting meant following, or in an app that doesn't tell Allstarr, are followed when
    // the listener next opens releases. Checked at most every ten minutes; only new follows are added, a few at a time.
    private async Task FollowFavoriteArtistsAsync(HttpContext http, ProtocolExecutionContext execution)
    {
        var owner = execution.RequireActor().EffectiveUserId!.Value;
        var now = DateTimeOffset.UtcNow;
        if (FavoritesChecked.TryGetValue(owner, out var last) && now - last < TimeSpan.FromMinutes(10)) return;
        FavoritesChecked[owner] = now;
        try
        {
            await releases.FollowFavoriteArtistsAsync(owner, execution.CancellationToken);
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
                        follows.Any(follow => follow.DisplayName.Equals(name.GetString(), StringComparison.OrdinalIgnoreCase) ||
                            $"ext-{follow.ProviderId}-artist-{follow.ArtistId}" == artistId)) continue;
                    if (await FollowAsync(http, execution, artistId, true) != null) added++;
                }
            }
        }
        // Following favourites is a convenience; the releases list never fails because of it.
        catch (Exception exception) when (exception is not OperationCanceledException) { }
    }

    private static bool SameNativeId(string? first, string second) => first == second ||
        Guid.TryParse(first, out var firstId) && Guid.TryParse(second, out var secondId) && firstId == secondId;
}

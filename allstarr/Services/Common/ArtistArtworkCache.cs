using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;

namespace allstarr.Services.Common;

internal static class ArtistArtworkCache
{
    private static readonly SemaphoreSlim Writes = new(1, 1);
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Key(string provider, string kind, string value) =>
        CacheKeyBuilder.BuildProtocolMetadataKey(allstarr.Core.Providers.AppleMusicKit.AppleMusicClient.NormalizeProviderId(provider), "artist-artwork", kind, value);
    private static string Scope(ProtocolExecutionContext? protocol) =>
        protocol?.Actor?.EffectiveUserId is { } user ? $"{protocol.Protocol}:{protocol.BackendInstanceId}:{user}" : "public";
    private sealed record ImageHash(string Hash, List<string> Names);
    internal static string ImageTag(Artist artist) => artist.Id + "-art-v4-" + Hash(artist.ImageUrl ?? "")[..12];

    internal static async Task PrepareSearchAsync(IApplicationCache? cache, ProtocolExecutionContext protocol,
        IEnumerable<Dictionary<string, object?>> nativeArtists, IReadOnlyList<Artist> artists,
        IReadOnlyList<Album>? albums = null, IReadOnlyList<Song>? songs = null)
    {
        foreach (var artist in artists)
            PopulateTopResults(artist, albums, songs);

        if (cache?.IsEnabled != true)
        {
            foreach (var artist in artists)
                if (KnownPlaceholder(artist.ImageUrl)) artist.ImageUrl = null;
            return;
        }
        foreach (var native in nativeArtists)
        {
            var row = JsonSerializer.SerializeToElement(native);
            if (!row.TryGetProperty("Id", out var id) || !row.TryGetProperty("Name", out var name) ||
                !row.TryGetProperty("ImageTags", out var tags) || tags.ValueKind != JsonValueKind.Object || !tags.TryGetProperty("Primary", out var tag) ||
                string.IsNullOrWhiteSpace(tag.GetString())) continue;
            var ids = row.TryGetProperty("ProviderIds", out var evidence)
                ? evidence.Deserialize<Dictionary<string, string>>() ?? [] : [];
            await RememberAsync(cache, protocol, new Artist
            {
                Id = id.GetString()!,
                Name = name.GetString()!,
                IsLocal = true,
                ProviderIds = ids,
                ImageUrl = $"/Items/{Uri.EscapeDataString(id.GetString()!)}/Images/Primary?tag={Uri.EscapeDataString(tag.GetString()!)}"
            });
        }
        foreach (var artist in artists) await RememberAsync(cache, protocol, artist);
        foreach (var artist in artists) artist.ImageUrl = await ResolveAsync(cache, protocol, artist);
        foreach (var artist in artists)
        {
            if (artist.ExternalProvider is { } provider && artist.ExternalId is { } id &&
                artist.ImageUrl?.StartsWith("/Items/", StringComparison.Ordinal) != true)
                await cache.SetAsync(Key(provider, "artist", id), artist, TimeSpan.FromHours(24));
        }
    }

    internal static void PopulateTopResults(Artist artist, IReadOnlyList<Album>? albums, IReadOnlyList<Song>? songs)
    {
        if (albums != null && albums.Count > 0 && artist.TopAlbumTitles.Count == 0)
        {
            artist.TopAlbumTitles = albums
                .Where(album => IsCredited(artist, album))
                .Select(album => album.Title)
                .Where(title => !string.IsNullOrWhiteSpace(title))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .ToList();
        }
        if (songs != null && songs.Count > 0 && artist.TopTrackTitles.Count == 0)
        {
            artist.TopTrackTitles = songs
                .Where(song => IsCredited(artist, song))
                .Select(song => song.Title)
                .Where(title => !string.IsNullOrWhiteSpace(title))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .ToList();
        }
    }

    private static bool IsCredited(Artist artist, Album album)
    {
        if (!string.IsNullOrWhiteSpace(artist.ExternalProvider) && !string.IsNullOrWhiteSpace(album.ExternalProvider) &&
            !string.Equals(allstarr.Core.Providers.AppleMusicKit.AppleMusicClient.NormalizeProviderId(artist.ExternalProvider),
                allstarr.Core.Providers.AppleMusicKit.AppleMusicClient.NormalizeProviderId(album.ExternalProvider), StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(artist.ExternalId) &&
            (album.ArtistId == artist.ExternalId || album.Artists.Any(a => a.ExternalId == artist.ExternalId || a.Id == artist.Id)))
            return true;

        if (string.IsNullOrWhiteSpace(artist.Name)) return false;
        var normArtistName = FuzzyMatcher.NormalizeForMatching(artist.Name);
        if (!string.IsNullOrWhiteSpace(album.Artist) && FuzzyMatcher.NormalizeForMatching(album.Artist) == normArtistName)
            return true;

        return album.Artists.Any(a => !string.IsNullOrWhiteSpace(a.Name) && FuzzyMatcher.NormalizeForMatching(a.Name) == normArtistName);
    }

    private static bool IsCredited(Artist artist, Song song)
    {
        if (!string.IsNullOrWhiteSpace(artist.ExternalProvider) && !string.IsNullOrWhiteSpace(song.ExternalProvider) &&
            !string.Equals(allstarr.Core.Providers.AppleMusicKit.AppleMusicClient.NormalizeProviderId(artist.ExternalProvider),
                allstarr.Core.Providers.AppleMusicKit.AppleMusicClient.NormalizeProviderId(song.ExternalProvider), StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(artist.ExternalId) &&
            (song.ArtistId == artist.ExternalId || song.ArtistIds.Contains(artist.ExternalId) ||
             song.AlbumArtists.Any(a => a.ExternalId == artist.ExternalId || a.Id == artist.Id)))
            return true;

        if (string.IsNullOrWhiteSpace(artist.Name)) return false;
        var normArtistName = FuzzyMatcher.NormalizeForMatching(artist.Name);
        if (!string.IsNullOrWhiteSpace(song.Artist) && FuzzyMatcher.NormalizeForMatching(song.Artist) == normArtistName)
            return true;

        if (!string.IsNullOrWhiteSpace(song.AlbumArtist) && FuzzyMatcher.NormalizeForMatching(song.AlbumArtist) == normArtistName)
            return true;

        if (song.Artists.Any(a => !string.IsNullOrWhiteSpace(a) && FuzzyMatcher.NormalizeForMatching(a) == normArtistName))
            return true;

        return song.AlbumArtists.Any(a => !string.IsNullOrWhiteSpace(a.Name) && FuzzyMatcher.NormalizeForMatching(a.Name) == normArtistName);
    }

    internal static async Task RememberAsync(IApplicationCache? cache, ProtocolExecutionContext? protocol, Artist artist)
    {
        if (cache?.IsEnabled != true) return;
        await Writes.WaitAsync();
        try
        {
            if (artist.ExternalProvider is { } provider && artist.ExternalId is { } id &&
                artist.ImageUrl?.StartsWith("/Items/", StringComparison.Ordinal) != true)
                await cache.SetAsync(Key(provider, "artist", id), artist, TimeSpan.FromHours(24));
            if (string.IsNullOrWhiteSpace(artist.Name)) return;
            var key = Key("allstarr", "photos", Scope(protocol) + ":" + FuzzyMatcher.NormalizeForMatching(artist.Name));
            var photos = await cache.GetAsync<List<Artist>>(key) ?? [];
            photos.RemoveAll(item => item.Id == artist.Id);
            photos.Add(artist);
            await cache.SetAsync(key, photos.TakeLast(8).ToList(), Retention);
        }
        finally { Writes.Release(); }
    }

    internal static async Task<string?> ResolveAsync(IApplicationCache? cache, ProtocolExecutionContext? protocol, Artist artist)
    {
        if (!await IsPlaceholderAsync(cache, artist.ExternalProvider ?? "jellyfin", artist.ImageUrl)) return artist.ImageUrl;
        if (cache?.IsEnabled != true) return null;
        var photos = await cache.GetAsync<List<Artist>>(Key("allstarr", "photos",
            Scope(protocol) + ":" + FuzzyMatcher.NormalizeForMatching(artist.Name))) ?? [];
        foreach (var photo in photos.OrderByDescending(item => item.IsLocal))
            if (SameIdentity(artist, photo) && !await IsPlaceholderAsync(cache, photo.ExternalProvider ?? "jellyfin", photo.ImageUrl))
                return photo.ImageUrl;
        return null;
    }

    internal static async Task<string?> ResolveResourceAsync(IApplicationCache? cache, ProtocolExecutionContext? protocol,
        string provider, string id, string? url)
    {
        if (cache?.IsEnabled == true && await cache.GetAsync<Artist>(Key(provider, "artist", id)) is { } artist)
            return await ResolveAsync(cache, protocol, artist);
        return await IsPlaceholderAsync(cache, provider, url) ? null : url;
    }

    internal static Task<Artist?> ReadResourceAsync(IApplicationCache cache, string provider, string id) =>
        cache.GetAsync<Artist>(Key(provider, "artist", id));

    internal static async Task<bool> ObserveAsync(IApplicationCache? cache, string provider, string id, string url, byte[] bytes)
    {
        if (KnownPlaceholder(url)) return false;
        if (cache?.IsEnabled != true) return true;
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        await Writes.WaitAsync();
        try
        {
            await cache.SetStringAsync(Key(provider, "url", url), sha, Retention);
            var key = Key(provider, "hashes", "artists");
            var hashes = await cache.GetAsync<List<ImageHash>>(key) ?? [];
            var observed = hashes.FirstOrDefault(item => item.Hash == sha);
            hashes.RemoveAll(item => item.Hash == sha);
            var names = observed?.Names ?? [];
            if (names.Count < 3 && await cache.GetAsync<Artist>(Key(provider, "artist", id)) is { Name.Length: > 0 } artist)
            {
                var name = Hash(FuzzyMatcher.NormalizeForMatching(artist.Name));
                if (!names.Contains(name)) names.Add(name);
            }
            hashes.Add(new(sha, names));
            // Keep learned defaults ahead of observations, with a small bound for each.
            await cache.SetAsync(key, hashes.Where(item => item.Names.Count >= 3).TakeLast(16)
                .Concat(hashes.Where(item => item.Names.Count < 3).TakeLast(64)).ToList(), Retention);
            return names.Count < 3;
        }
        finally { Writes.Release(); }
    }

    internal static async Task<bool> IsPlaceholderAsync(IApplicationCache? cache, string provider, string? url)
    {
        if (KnownPlaceholder(url)) return true;
        if (cache?.IsEnabled != true) return false;
        var sha = await cache.GetStringAsync(Key(provider, "url", url!));
        if (sha == null) return false;
        var hashes = await cache.GetAsync<List<ImageHash>>(Key(provider, "hashes", "artists"));
        return hashes?.Any(item => item.Hash == sha && item.Names.Count >= 3) == true;
    }

    private static bool KnownPlaceholder(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.StartsWith("/api/admin/providers/", StringComparison.Ordinal) ||
            url.StartsWith("/images/providers/", StringComparison.Ordinal)) return true;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        var path = uri.AbsolutePath.ToLowerInvariant();
        return new[] { "default_artist", "default-artist", "artist_default", "artist-default", "artist_placeholder",
            "artist-placeholder", "/artist/default.", "/artists/default.", "/no_image.", "/no-image.", "/placeholder." }
            .Any(path.Contains);
    }

    private static bool SameIdentity(Artist left, Artist right)
    {
        if (left.Id == right.Id || FuzzyMatcher.NormalizeForMatching(left.Name) != FuzzyMatcher.NormalizeForMatching(right.Name)) return false;
        static string NormalizeId(string key) => key.ToLowerInvariant() is "musicbrainzartist" or "musicbrainz" ? "musicbrainz" : key.ToLowerInvariant();
        var a = left.ProviderIds.GroupBy(item => NormalizeId(item.Key)).ToDictionary(group => group.Key, group => group.First().Value, StringComparer.OrdinalIgnoreCase);
        var b = right.ProviderIds.GroupBy(item => NormalizeId(item.Key)).ToDictionary(group => group.Key, group => group.First().Value, StringComparer.OrdinalIgnoreCase);
        if (left.ExternalProvider is { } lp && left.ExternalId is { } li) a.TryAdd(lp, li);
        if (right.ExternalProvider is { } rp && right.ExternalId is { } ri) b.TryAdd(rp, ri);
        if (a.TryGetValue("musicbrainz", out var am) && b.TryGetValue("musicbrainz", out var bm) &&
            Guid.TryParse(am, out var ag) && Guid.TryParse(bm, out var bg)) return ag == bg;
        if (a.Any(item => item.Key != "musicbrainz" && !string.IsNullOrWhiteSpace(item.Value) && b.TryGetValue(item.Key, out var value) && value == item.Value))
            return true;

        if (right.IsLocal) return false;

        var leftTitles = left.TopAlbumTitles.Concat(left.TopTrackTitles)
            .Select(FuzzyMatcher.NormalizeAlbumForMatching)
            .Where(t => !string.IsNullOrWhiteSpace(t));
        var rightTitles = right.TopAlbumTitles.Concat(right.TopTrackTitles)
            .Select(FuzzyMatcher.NormalizeAlbumForMatching)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToHashSet(StringComparer.Ordinal);

        return leftTitles.Any(rightTitles.Contains);
    }
}

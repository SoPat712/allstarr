using System.Collections.Concurrent;
using System.Text.Json;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Matching;

public sealed partial class LibraryArtistIndex
{
    private const int CandidateChecks = 6;
    private static readonly TimeSpan CheckedFor = TimeSpan.FromHours(6);
    // Titles many acts share prove nothing about which act it is.
    private static readonly HashSet<string> CommonTitles = new[]
    {
        "Greatest Hits", "The Greatest Hits", "Best Of", "The Best Of", "The Very Best Of", "The Best", "Live", "Unplugged", "Acoustic",
        "Demos", "Demo", "EP", "Singles", "The Singles", "Remixes", "The Remixes", "Remixed", "Essentials", "The Essentials",
        "Anthology", "The Anthology", "Collection", "The Collection", "The Hits", "Hits", "Instrumentals", "B-Sides", "Rarities",
        "Untitled", "Self Titled", "Volume 1", "Volume 2", "Vol. 1", "Vol. 2", "Gold", "Platinum", "Classics", "Mixtape",
        "Christmas", "Covers", "Sessions", "Love", "Home", "Intro"
    }.Select(FuzzyMatcher.NormalizeForMatching).ToHashSet(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> albumChecks = new(StringComparer.Ordinal);

    /// <summary>
    /// The library album that proves a same-named service artist is the library's. The album must be credited to the
    /// artist on both sides and have a title of its own: not the artist's name, and not one many acts use.
    /// </summary>
    internal static string? SharedAlbum(string artistName, IEnumerable<string> libraryAlbums, IEnumerable<Album> serviceAlbums)
    {
        var artistKey = FuzzyMatcher.NormalizeForMatching(artistName);
        if (artistKey.Length == 0) return null;
        bool Telling(string title) => FuzzyMatcher.NormalizeForMatching(FuzzyMatcher.StripDecorators(title)) is { Length: >= 3 } key &&
            key != artistKey && !CommonTitles.Contains(key);
        var owned = libraryAlbums.Where(title => !string.IsNullOrWhiteSpace(title) && Telling(title)).Distinct(StringComparer.Ordinal).ToArray();
        if (owned.Length == 0) return null;
        foreach (var album in serviceAlbums)
        {
            if (string.IsNullOrWhiteSpace(album.Title) || !Telling(album.Title) ||
                !string.IsNullOrWhiteSpace(album.Artist) && !MusicCatalogMerge.LeadArtistMatches(album.Artist, artistKey)) continue;
            if (owned.FirstOrDefault(title => MusicCatalogMerge.IsSameAlbum(album.Title, title)) is { } shared) return shared;
        }
        return null;
    }

    /// <summary>
    /// Ties a same-named service artist to the library artist when they share an album, and says whether they are tied
    /// now. A person's "not the same" stands; names alone never tie.
    /// </summary>
    public async Task<bool> ProveByAlbumsAsync(Guid owner, string name, string providerId, string artistId,
        IReadOnlyCollection<Album> serviceAlbums, CancellationToken token = default)
    {
        if (!ValidName(name) || string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(artistId) || artistId.Length > 300 ||
            !Usable(artistId)) return false;
        var access = await libraryAccess.ResolveUserAsync(owner, token);
        if (access.Context == null || !access.Access.Succeeded) return false;
        var provider = NormalizeProvider(providerId);
        var artist = ServiceCatalog.Key(provider, artistId);
        await Gate.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var fixes = (await FixesAsync(db, owner, access.Context, [name], token)).GetValueOrDefault(name) ?? [];
            if (fixes.FirstOrDefault(fix => ServiceCatalog.Key(fix.ProviderId, fix.ArtistId) == artist) is { } answer) return answer.Same;
            var key = Key(access.Context, name);
            var previous = await LatestAsync(db, owner, AlbumKind, key, token);
            var saved = ReadAlbumLinks(previous?.PayloadJson).ToList();
            if (saved.Any(link => ServiceCatalog.Key(link.ProviderId, link.ArtistId) == artist)) return true;
            var tracks = await TracksAsync(db, access, name, token);
            if (tracks.Length == 0) return false;
            var artistKey = FuzzyMatcher.NormalizeForMatching(name);
            // Only albums led by the artist count, so a guest spot on someone else's album proves nothing.
            var shared = SharedAlbum(name, tracks.Where(track => !string.IsNullOrWhiteSpace(track.Album) &&
                MusicCatalogMerge.LeadArtistMatches(string.IsNullOrWhiteSpace(track.AlbumArtist) ? track.Artist : track.AlbumArtist, artistKey))
                .Select(track => track.Album!), serviceAlbums);
            if (shared == null) return false;
            saved.Add(new(provider, artistId, shared));
            await SaveAsync(db, access.Context, AlbumKind, key, saved.ToArray(), "album", previous, token);
            return true;
        }
        finally { Gate.Release(); }
    }

    /// <summary>
    /// Checks same-named service artists a search or page just showed against the library's albums, a few at a time and
    /// each at most once every few hours, so the next search lists a proven one as the library artist.
    /// </summary>
    public async Task ProveCandidatesAsync(Guid owner, IReadOnlyCollection<string> libraryNames, IReadOnlyCollection<Artist> serviceArtists,
        CancellationToken token = default)
    {
        var names = libraryNames.Where(ValidName).GroupBy(FuzzyMatcher.NormalizeForMatching, StringComparer.Ordinal)
            .Where(group => group.Key.Length > 0).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var candidates = serviceArtists.Where(artist => !string.IsNullOrWhiteSpace(artist.ExternalProvider) &&
                !string.IsNullOrWhiteSpace(artist.ExternalId) && artist.ExternalId.Length <= 300 && Usable(artist.ExternalId) &&
                names.ContainsKey(FuzzyMatcher.NormalizeForMatching(artist.Name)) && emptyArtists?.IsEmpty(artist) != true)
            .DistinctBy(artist => ServiceCatalog.Key(NormalizeProvider(artist.ExternalProvider!), artist.ExternalId!), StringComparer.Ordinal).ToArray();
        if (candidates.Length == 0) return;
        var access = await libraryAccess.ResolveUserAsync(owner, token);
        if (access.Context == null || !access.Access.Succeeded) return;
        var saved = await ReadSavedAsync(owner, names.Values, token);
        var now = clock.UtcNow;
        foreach (var stale in albumChecks.Where(item => item.Value <= now).Select(item => item.Key).ToArray()) albumChecks.TryRemove(stale, out _);
        var context = new ProtocolExecutionContext(access.Context.Protocol, access.Context.BackendInstanceId,
            access.Context.VerifiedBackendPrincipalId, access.Context.Principal, "library-artist-link", now.AddMinutes(1), token)
        { IsReadOnly = true, UseStoredBackendCredentials = true };
        var checks = 0;
        foreach (var candidate in candidates.OrderByDescending(artist => artist.AlbumCount ?? 0))
        {
            var name = names[FuzzyMatcher.NormalizeForMatching(candidate.Name)];
            var provider = NormalizeProvider(candidate.ExternalProvider!);
            var artist = ServiceCatalog.Key(provider, candidate.ExternalId!);
            if ((saved.GetValueOrDefault(name) ?? []).Any(link => ServiceCatalog.Key(link.ProviderId, link.ArtistId) == artist)) continue;
            if (!albumChecks.TryAdd($"{owner:N}|{artist}", now + CheckedFor)) continue;
            if (++checks > CandidateChecks) break;
            token.ThrowIfCancellationRequested();
            List<Album> albums;
            try { albums = await providers.GetArtistAlbumsAsync(context, provider, candidate.ExternalId!); }
            catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
            {
                // A service that failed is asked again on the next search.
                albumChecks.TryRemove($"{owner:N}|{artist}", out _);
                continue;
            }
            await ProveByAlbumsAsync(owner, name, provider, candidate.ExternalId!, albums, token);
        }
    }

    /// <summary>
    /// Asks the listener's services who answers to a library artist's name, records them as candidates and checks their
    /// albums. Each name is looked up at most once every few hours.
    /// </summary>
    public async Task DiscoverAsync(Guid owner, string name, CancellationToken token = default)
    {
        if (!ValidName(name)) return;
        var now = clock.UtcNow;
        var memo = $"{owner:N}|name|{FuzzyMatcher.NormalizeForMatching(name)}";
        if (albumChecks.TryGetValue(memo, out var until) && until > now) return;
        albumChecks[memo] = now + CheckedFor;
        try
        {
            var access = await libraryAccess.ResolveUserAsync(owner, token);
            if (access.Context == null || !access.Access.Succeeded) return;
            var context = new ProtocolExecutionContext(access.Context.Protocol, access.Context.BackendInstanceId,
                access.Context.VerifiedBackendPrincipalId, access.Context.Principal, "library-artist-link", now.AddMinutes(1), token)
            { IsReadOnly = true, UseStoredBackendCredentials = true };
            var key = FuzzyMatcher.NormalizeForMatching(name);
            var found = (await providers.SearchAsync(context, name, 0, 0, 20)).Artists.Where(artist =>
                !string.IsNullOrWhiteSpace(artist.ExternalProvider) && !string.IsNullOrWhiteSpace(artist.ExternalId) &&
                FuzzyMatcher.NormalizeForMatching(artist.Name) == key).ToArray();
            await RecordCandidatesAsync(owner, [name], found, token);
            await ProveCandidatesAsync(owner, [name], found, token);
        }
        catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
        {
            // A lookup that failed is tried again on the next visit.
            albumChecks.TryRemove(memo, out _);
        }
    }

    private static async Task<Dictionary<string, IReadOnlyList<LibraryArtistAlbumLink>>> AlbumLinksAsync(AllstarrDbContext db, Guid owner,
        ProtocolExecutionContext context, IReadOnlyCollection<string> names, CancellationToken token)
    {
        var result = new Dictionary<string, IReadOnlyList<LibraryArtistAlbumLink>>(StringComparer.Ordinal);
        var keys = names.ToDictionary(name => name, name => Key(context, name), StringComparer.Ordinal);
        var hashes = keys.Values.Distinct(StringComparer.Ordinal).ToArray();
        var saved = (await db.ExternalMetadataSnapshots.AsNoTracking().Where(item => item.OwnerUserId == owner &&
                item.ProviderId == "allstarr" && item.ResourceKind == AlbumKind && hashes.Contains(item.ExternalIdHash))
            .Select(item => new { item.ExternalIdHash, item.SnapshotVersion, item.PayloadJson }).ToArrayAsync(token))
            .GroupBy(item => item.ExternalIdHash, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.MaxBy(item => item.SnapshotVersion)!.PayloadJson, StringComparer.Ordinal);
        foreach (var (name, key) in keys)
            if (saved.TryGetValue(key, out var payload) && ReadAlbumLinks(payload) is { Count: > 0 } links) result[name] = links;
        return result;
    }

    private static IReadOnlyList<LibraryArtistAlbumLink> ReadAlbumLinks(string? payload)
    {
        try { return (JsonSerializer.Deserialize<LibraryArtistAlbumLink[]>(payload ?? "[]") ?? []).Where(link => Usable(link.ArtistId)).ToArray(); }
        catch (JsonException) { return []; }
    }
}

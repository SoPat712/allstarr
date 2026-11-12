using allstarr.Core.Operations;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Providers.AppleMusicKit;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using allstarr.Models.Domain;
using allstarr.Models.Search;
using allstarr.Models.Subsonic;
using allstarr.Services.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace allstarr.Core.Music;

public enum MusicItemKind
{
    Song,
    Album,
    Artist,
    Playlist
}

/// <summary>
/// A backend row kept exactly as the backend sent it, with the few fields search needs to rank it.
/// </summary>
public sealed record NativeMusicItem<TPayload>(
    MusicItemKind Kind,
    string Id,
    string Name,
    string? ArtistText,
    TPayload Payload)
{
    public string? AlbumText { get; init; }
    public string? AlbumArtistText { get; init; }
    public double? DurationSeconds { get; init; }
    public Album? AlbumMetadata { get; init; }
    public int? ExplicitContentLyrics { get; init; }
}

/// <summary>One merged search row: a native backend payload or a typed provider item.</summary>
public sealed record MusicSearchEntry<TPayload>
{
    public string? TasteReason { get; init; }
    public NativeMusicItem<TPayload>? Native { get; init; }
    public Song? Song { get; init; }
    public Album? Album { get; init; }
    public Artist? Artist { get; init; }
    public ExternalPlaylist? Playlist { get; init; }
}

/// <summary>
/// How many provider rows to request, and which slice of them joins the merge.
/// </summary>
public sealed record MusicSearchWindow(int Fetch, int Skip = 0, int? Take = null)
{
    public static readonly MusicSearchWindow None = new(0);

    internal IEnumerable<T> Apply<T>(IEnumerable<T> items)
    {
        var skipped = items.Skip(Skip);
        return Take.HasValue ? skipped.Take(Take.Value) : skipped;
    }
}

public sealed record MusicSearchRequest(
    string Query,
    MusicSearchWindow Songs,
    MusicSearchWindow Albums,
    MusicSearchWindow Artists,
    MusicSearchWindow Playlists,
    bool IncludeProviders = true,
    bool HideLinkedProviderArtists = false)
{
    public bool LimitProviderWait { get; init; }
}

public sealed record MusicSearchResult<TPayload>(
    IReadOnlyList<MusicSearchEntry<TPayload>> Songs,
    IReadOnlyList<MusicSearchEntry<TPayload>> Albums,
    IReadOnlyList<MusicSearchEntry<TPayload>> Artists);

/// <summary>
/// The backend half of a search, implemented by each protocol dialect.
/// A failed backend search returns no rows rather than failing the whole search.
/// </summary>
public interface INativeMusicSearch<TPayload>
{
    Task<IReadOnlyList<NativeMusicItem<TPayload>>> SearchAsync(CancellationToken cancellationToken);

    Task<NativeMusicItem<TPayload>?> GetSongAsync(string id, CancellationToken cancellationToken);

    async Task<IReadOnlyList<NativeMusicItem<TPayload>>> GetSongsAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        using var slots = new SemaphoreSlim(4);
        var rows = await Task.WhenAll(ids.Select(async id =>
        {
            using (RequestTiming.Current?.Stage("lock")) await slots.WaitAsync(cancellationToken);
            try
            {
                var row = await GetSongAsync(id, cancellationToken);
                return row?.Id == id ? row : null;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested) { return null; }
            finally { slots.Release(); }
        }));
        return rows.OfType<NativeMusicItem<TPayload>>().ToArray();
    }
}

/// <summary>
/// Integrated search shared by every protocol: backend and provider rows are fetched together,
/// provider songs the listener already owns are replaced by the native copy, and each kind is
/// ranked by relevance with native rows winning ties.
/// </summary>
public sealed partial class MusicSearchService(
    IProtocolProviderGateway providers,
    ILibraryIndexService? libraryIndex = null,
    SavedLibraryReader? savedLibrary = null,
    ILogger<MusicSearchService>? logger = null,
    TimeProvider? timeProvider = null, IRuntimeSettingsChangeSignal? settings = null,
    IDbContextFactory<AllstarrDbContext>? factory = null, ILibraryArtistLinks? artistLinks = null,
    EmptyServiceArtists? emptyArtists = null)
{
    private const int LinkedArtistLookups = 50;

    private readonly Dictionary<string, OwnedSearchLookup> ownedSearchLookups = new(StringComparer.Ordinal);
    private sealed record OwnedSearchLookup(DateTimeOffset ExpiresAt, Lazy<Task<IReadOnlyDictionary<string, string>>> Work);

    /// <summary>Two songs can only be the same recording when this matches, which makes it a cheap way to narrow down comparisons.</summary>
    internal static string RecordingKey(Song song) =>
        FuzzyMatcher.NormalizeForMatching(FuzzyMatcher.SplitCredits(song.Artist).FirstOrDefault() ?? "") + "\u001f" +
        FuzzyMatcher.NormalizeForMatching(FuzzyMatcher.StripDecorators(song.Title));

    internal static bool MetadataRecordingsMatch(Song left, Song right, bool allowSparse = false)
    {
        var leftDuration = RecordingDuration(left);
        var rightDuration = RecordingDuration(right);
        if (left.ExternalProvider?.Contains("artistgrid", StringComparison.OrdinalIgnoreCase) == true ||
            right.ExternalProvider?.Contains("artistgrid", StringComparison.OrdinalIgnoreCase) == true ||
            !HasOrdinaryRecordingMetadata(left) || !HasOrdinaryRecordingMetadata(right) ||
            !allowSparse && (leftDuration is not > 0 || rightDuration is not > 0) ||
            leftDuration is > 0 && rightDuration is > 0 && Math.Abs(leftDuration.Value - rightDuration.Value) > 2 ||
            RecordingVersion(left) != RecordingVersion(right)) return false;
        if (RecordingExplicitness(left) is { } firstExplicit && RecordingExplicitness(right) is { } secondExplicit &&
            firstExplicit != secondExplicit) return false;
        var artist = FuzzyMatcher.NormalizeForMatching(FuzzyMatcher.SplitCredits(left.Artist).FirstOrDefault() ?? "");
        var otherArtist = FuzzyMatcher.NormalizeForMatching(FuzzyMatcher.SplitCredits(right.Artist).FirstOrDefault() ?? "");
        var title = FuzzyMatcher.NormalizeForMatching(FuzzyMatcher.StripDecorators(left.Title));
        var otherTitle = FuzzyMatcher.NormalizeForMatching(FuzzyMatcher.StripDecorators(right.Title));
        if (artist.Length == 0 || title.Length == 0 || artist != otherArtist || title != otherTitle) return false;
        var album = FuzzyMatcher.NormalizeForMatching(left.Album);
        var otherAlbum = FuzzyMatcher.NormalizeForMatching(right.Album);
        // Releases can assign different ISRCs to the same ordinary recording. Require matching
        // album evidence as well when the identifiers disagree.
        if (NormalizeIdentifier(left.Isrc) is { } firstIsrc && NormalizeIdentifier(right.Isrc) is { } secondIsrc &&
            firstIsrc != secondIsrc) return leftDuration is > 0 && rightDuration is > 0 && album.Length > 0 && album == otherAlbum;
        return album.Length == 0 || otherAlbum.Length == 0 || album == otherAlbum;
    }

    internal static IReadOnlyList<List<Song>> PartitionMetadataRecordings(IEnumerable<Song> songs,
        Func<Song, Song, bool>? compatibleEvidence = null)
    {
        var rows = songs.ToArray();
        var groups = new List<List<Song>>();
        var keys = rows.ToDictionary(song => song, RecordingKey, ReferenceEqualityComparer.Instance as IEqualityComparer<Song>);
        var groupsByKey = new Dictionary<string, List<List<Song>>>(StringComparer.Ordinal);
        List<List<Song>> GroupsFor(Song song)
        {
            if (!groupsByKey.TryGetValue(keys[song], out var known)) groupsByKey[keys[song]] = known = [];
            return known;
        }
        void Start(Song song)
        {
            List<Song> group = [song];
            groups.Add(group);
            GroupsFor(song).Add(group);
        }
        bool Compatible(Song left, Song right, bool sparse) =>
            (allstarr.Core.Providers.AppleMusicKit.AppleMusicClient.NormalizeProviderId(left.ExternalProvider ?? "") !=
             allstarr.Core.Providers.AppleMusicKit.AppleMusicClient.NormalizeProviderId(right.ExternalProvider ?? "") ||
             sparse && RecordingDuration(left) is not > 0 && RecordingDuration(right) is not > 0) &&
            MetadataRecordingsMatch(left, right, sparse) && compatibleEvidence?.Invoke(left, right) != false;
        foreach (var song in rows.Where(song => RecordingDuration(song) is > 0))
        {
            var group = GroupsFor(song).FirstOrDefault(members => members.All(member => Compatible(member, song, false)));
            if (group == null) Start(song);
            else group.Add(song);
        }
        foreach (var song in rows.Where(song => RecordingDuration(song) is not > 0))
        {
            var group = GroupsFor(song).FirstOrDefault(members => members.Any(member => RecordingDuration(member) is > 0) &&
                members.All(member => Compatible(member, song, true)));
            if (group == null) Start(song);
            else group.Add(song);
        }
        return groups;
    }

    private static string? NormalizeIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = new string(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        return normalized.Length == 12 ? normalized : null;
    }

    private static double? RecordingDuration(Song song) => song.PreciseDuration?.TotalSeconds ?? song.Duration;

    private static bool? RecordingExplicitness(Song song) => song.ExplicitContentLyrics switch
    {
        1 => true,
        0 or 3 => false,
        _ => null
    };

    private static string RecordingVersion(Song song) => string.Join("|",
        FuzzyMatcher.SemanticVersionTags(string.Join(" ", song.Title, song.Album)).Order(StringComparer.Ordinal));

    private static bool HasOrdinaryRecordingMetadata(Song song) => !FuzzyMatcher.IsContinuousMixExcerpt(song.Title, song.Album) &&
        !FuzzyMatcher.HasCoverRecordingLabel(string.Join(" ", song.Title, song.Artist, song.Album)) &&
        FuzzyMatcher.BackingRecordingTags(string.Join(" ", song.Title, song.Artist, song.Album)).Count == 0 &&
        FuzzyMatcher.SpecialEditionTags(string.Join(" ", song.Title, song.Album)).Count == 0;

    public async Task<MusicSearchResult<TPayload>> SearchAsync<TPayload>(
        ProtocolExecutionContext context,
        MusicSearchRequest request,
        INativeMusicSearch<TPayload> native,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(native);

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        long Elapsed() => (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        async Task<T> Timed<T>(Task<T> work, long[] finished, int slot, string name)
        {
            using var phase = RequestTiming.Current?.Stage(name);
            try { return await work; }
            finally { finished[slot] = Elapsed(); }
        }
        var phases = new long[3];
        var nativeTask = Timed(native.SearchAsync(cancellationToken), phases, 0, "library");
        var providerTask = request.IncludeProviders
            ? Task.Run(() => providers.SearchAsync(
                context,
                request.Query,
                request.Songs.Fetch,
                request.Albums.Fetch,
                request.Artists.Fetch))
            : Task.FromResult(new SearchResult());
        providerTask = Timed(providerTask, phases, 1, "sources");
        var playlistTask = request.IncludeProviders && request.Playlists.Fetch > 0
            ? Task.Run(() => providers.SearchPlaylistsAsync(context, request.Query, request.Playlists.Fetch))
            : Task.FromResult(new List<ExternalPlaylist>());
        playlistTask = Timed(playlistTask, phases, 2, "sources");
        var nativeRows = await nativeTask;
        using var providerWindow = request.LimitProviderWait
            ? new CancellationTokenSource(TimeSpan.FromSeconds(3), timeProvider ?? TimeProvider.System) : null;
        using var providerCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, context.CancellationToken, providerWindow?.Token ?? default);
        var workToken = providerCancellation.Token;
        try { return await ComposeAsync().WaitAsync(workToken); }
        catch (OperationCanceledException) when (providerWindow?.IsCancellationRequested == true &&
            !cancellationToken.IsCancellationRequested && !context.CancellationToken.IsCancellationRequested)
        {
            var ready = providerTask.IsCompletedSuccessfully ? providerTask.Result : new SearchResult();
            var readyPlaylists = playlistTask.IsCompletedSuccessfully ? playlistTask.Result : [];
            logger?.LogInformation("Search provider wait budget expired after native search");
            return Merge(request.Query, nativeRows.Where(item => item.Kind == MusicItemKind.Artist ||
                item.Kind == MusicItemKind.Album && context.Policy?.Preferences.ExplicitFilter != "CleanOnly" ||
                context.Policy?.Includes(item.ExplicitContentLyrics) != false),
                request.Songs.Apply(ready.Songs), request.Albums.Apply(MusicAlbumReleases.Collapse(ready.Albums)),
                request.Artists.Apply(ready.Artists), request.Playlists.Apply(readyPlaylists), null);
        }

        async Task<MusicSearchResult<TPayload>> ComposeAsync()
        {
            await Task.WhenAll(providerTask, playlistTask).WaitAsync(workToken);
            var gathered = Elapsed();
            long savedAt = gathered;

            var nativeSongs = nativeRows.Where(item => item.Kind == MusicItemKind.Song &&
                context.Policy?.Includes(item.ExplicitContentLyrics) != false).ToList();
            var nativeAlbums = nativeRows.Where(item => item.Kind == MusicItemKind.Album &&
                (context.Policy?.Preferences.ExplicitFilter != "CleanOnly" || context.Policy.Includes(item.ExplicitContentLyrics))).ToList();
            var nativeArtists = nativeRows.Where(item => item.Kind == MusicItemKind.Artist).ToList();
            var provided = await providerTask;
            var songs = request.Songs.Apply(provided.Songs).ToList();
            var albums = provided.Albums.ToList();
            var artists = request.Artists.Apply(provided.Artists).ToList();
            var playlists = request.Playlists.Apply(await playlistTask).ToList();

            var originalSongs = songs;
            var originalAlbums = albums;
            songs = songs.Select(song => song.Copy()).ToList();
            albums = albums.Select(album => album.Copy()).ToList();
            using var enrichmentWindow = request.LimitProviderWait
                ? new CancellationTokenSource(EnrichmentBudget(Elapsed()), timeProvider ?? TimeProvider.System) : null;
            using var enrichmentStop = CancellationTokenSource.CreateLinkedTokenSource(workToken, enrichmentWindow?.Token ?? default);
            var enrichmentToken = enrichmentStop.Token;
            try
            {
                using (RequestTiming.Current?.Stage("queue"))
                    return await Task.Run(EnrichAsync).WaitAsync(enrichmentToken);
            }
            catch (OperationCanceledException) when (enrichmentWindow?.IsCancellationRequested == true && !workToken.IsCancellationRequested)
            {
                logger?.LogInformation("Search enrichment budget expired; keeping {Count} streaming songs", originalSongs.Count);
                return Merge(request.Query, nativeRows.Where(item => item.Kind == MusicItemKind.Artist ||
                    item.Kind == MusicItemKind.Album && context.Policy?.Preferences.ExplicitFilter != "CleanOnly" ||
                    context.Policy?.Includes(item.ExplicitContentLyrics) != false), originalSongs,
                    request.Albums.Apply(MusicAlbumReleases.Collapse(originalAlbums)), artists, playlists, null);
            }

            async Task<MusicSearchResult<TPayload>> EnrichAsync()
            {
                if (savedLibrary != null)
                {
                    try
                    {
                        using (RequestTiming.Current?.Stage("saved"))
                            await savedLibrary.PreferSavedIdsAsync(context, songs, albums, enrichmentToken);
                    }
                    catch (Exception) when (!enrichmentToken.IsCancellationRequested) { }
                    songs = songs.DistinctBy(song => song.Id, StringComparer.Ordinal).ToList();
                }
                albums = request.Albums.Apply(MusicAlbumReleases.Collapse(albums)).ToList();
                savedAt = Elapsed();
                using (RequestTiming.Current?.Stage("owned"))
                    await PreferOwnedSongsAsync(context, songs, nativeSongs, native, enrichmentToken);
                // Service artists that open to nothing stay out; same-named ones are checked for next time.
                ProbeArtists(context, artists, nativeArtists.Select(item => item.Name));
                artists = artists.Where(artist => !IsEmptyArtist(artist)).ToList();
                IReadOnlySet<string>? linkedArtists = null;
                if (request.HideLinkedProviderArtists)
                    using (RequestTiming.Current?.Stage("artists"))
                        linkedArtists = await LinkedProviderArtistsAsync(context, nativeArtists.Select(item => item.Name).ToArray(),
                            artists, enrichmentToken);
                logger?.LogInformation("Search timing: library {Library} ms, sources {Sources} ms, playlists {Playlists} ms, saved songs {Saved} ms, owned songs {Owned} ms, total {Total} ms",
                    phases[0], phases[1], phases[2], savedAt - gathered, Elapsed() - savedAt, Elapsed());
                return Merge(
                    request.Query,
                    nativeSongs.Concat(nativeAlbums).Concat(nativeArtists),
                    songs,
                    albums,
                    artists,
                    playlists,
                    linkedArtists);
            }
        }
    }

    /// <summary>
    /// Service artists that matched songs prove to be one of these library artists. Search hides only those: a
    /// same-named artist without links stays visible and gets song matching queued, so a name alone never hides one.
    /// </summary>
    public async Task<IReadOnlySet<string>> LinkedProviderArtistsAsync(ProtocolExecutionContext context,
        IReadOnlyCollection<string> libraryArtists, IReadOnlyCollection<Artist> providerArtists, CancellationToken cancellationToken)
    {
        var serviceNames = providerArtists.Where(artist => ArtistKey(artist) != null)
            .Select(artist => FuzzyMatcher.NormalizeForMatching(artist.Name)).ToHashSet(StringComparer.Ordinal);
        if (serviceNames.Count == 0) return new HashSet<string>(StringComparer.Ordinal);
        var saved = await SavedArtistLinksAsync(context, libraryArtists, cancellationToken);
        foreach (var name in libraryArtists.Where(name => !saved.ContainsKey(name) &&
                     serviceNames.Contains(FuzzyMatcher.NormalizeForMatching(name))).Distinct(StringComparer.Ordinal))
            await QueueArtistLinkAsync(context, name, cancellationToken);
        return LinkedKeys(saved);
    }

    /// <summary>Saved song links for each library artist that has any, keyed by the name as given.</summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<LibraryArtistLink>>> SavedArtistLinksAsync(
        ProtocolExecutionContext context, IReadOnlyCollection<string> libraryArtists, CancellationToken cancellationToken)
    {
        var none = new Dictionary<string, IReadOnlyList<LibraryArtistLink>>(StringComparer.Ordinal);
        if (artistLinks == null || context.Actor?.EffectiveUserId is not { } owner) return none;
        var names = libraryArtists.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.Ordinal)
            .Take(LinkedArtistLookups).ToArray();
        if (names.Length == 0) return none;
        // Without links every service artist stays visible.
        try
        {
            return (await artistLinks.ReadSavedAsync(owner, names, cancellationToken))
                .Where(item => item.Value.Count > 0).ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        }
        catch (Exception error) when (error is not OperationCanceledException) { return none; }
    }

    /// <summary>Queues background song matching for a library artist, at most once a day.</summary>
    public async Task QueueArtistLinkAsync(ProtocolExecutionContext context, string libraryArtist, CancellationToken cancellationToken)
    {
        if (artistLinks == null || context.Actor?.EffectiveUserId is not { } owner) return;
        try { await artistLinks.RequestAsync(owner, libraryArtist, cancellationToken); }
        catch (Exception error) when (error is not OperationCanceledException) { }
    }

    public static IReadOnlySet<string> LinkedKeys(IReadOnlyDictionary<string, IReadOnlyList<LibraryArtistLink>> saved) =>
        saved.Values.SelectMany(links => links).Select(link => ArtistKey(link.ProviderId, link.ArtistId)).ToHashSet(StringComparer.Ordinal);

    public bool IsEmptyArtist(Artist artist) => emptyArtists?.IsEmpty(artist) == true;

    // The checks finish in the background; a later search sees what they found.
    public void ProbeArtists(ProtocolExecutionContext context, IEnumerable<Artist> artists, IEnumerable<string>? libraryNames = null) =>
        _ = emptyArtists?.Probe(providers, context, artists, libraryNames);

    public static string ArtistKey(string provider, string artistId) =>
        AppleMusicClient.NormalizeProviderId(provider) + ":" + artistId;

    public static string? ArtistKey(Artist artist) =>
        string.IsNullOrEmpty(artist.ExternalProvider) || string.IsNullOrEmpty(artist.ExternalId)
            ? null : ArtistKey(artist.ExternalProvider, artist.ExternalId);

    public static bool IsLinked(Artist artist, IReadOnlySet<string>? linkedProviderArtists) =>
        linkedProviderArtists != null && ArtistKey(artist) is { } key && linkedProviderArtists.Contains(key);

    /// <summary>Ranks native and provider rows of each kind; native rows win ties.</summary>
    public static MusicSearchResult<TPayload> Merge<TPayload>(
        string query,
        IEnumerable<NativeMusicItem<TPayload>> nativeRows,
        IEnumerable<Song> songs,
        IEnumerable<Album> albums,
        IEnumerable<Artist> artists,
        IEnumerable<ExternalPlaylist> playlists,
        IReadOnlySet<string>? linkedProviderArtists)
    {
        using var merging = RequestTiming.Current?.Stage("merge");
        var native = nativeRows.ToList();
        var providerSongs = songs.ToList();
        var nativeArtists = native.Where(item => item.Kind == MusicItemKind.Artist).ToList();
        var nativeAlbums = native.Where(item => item.Kind == MusicItemKind.Album).ToList();
        var nativeAlbumIds = nativeAlbums.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var nativeAlbumMetadata = nativeAlbums.Select(item => item.AlbumMetadata ?? new Album
        { Id = item.Id, Title = item.Name, Artist = item.ArtistText ?? "", IsLocal = true }).ToArray();
        var ownedAlbumNames = nativeAlbumMetadata.ToLookup(MusicAlbumReleases.FamilyKey, StringComparer.Ordinal);
        var unownedAlbums = albums.Where(album => !ownedAlbumNames[MusicAlbumReleases.FamilyKey(album)]
            .Any(owned => MusicAlbumReleases.Compatible(owned, album)));
        var mergedAlbums = MusicAlbumReleases.Collapse(unownedAlbums.Concat(nativeAlbums.Select(item => item.AlbumMetadata).OfType<Album>()))
            .Where(album => !album.IsLocal || !nativeAlbumIds.Contains(album.Id));
        return new MusicSearchResult<TPayload>(
            RankSongs(query, native.Where(item => item.Kind == MusicItemKind.Song)
                .Select(item => new MusicSearchEntry<TPayload> { Native = item })
                .Concat(providerSongs.Select(song => new MusicSearchEntry<TPayload> { Song = song }))),
            Rank(query, nativeAlbums, mergedAlbums.Select(album => (
                    new MusicSearchEntry<TPayload> { Album = album },
                    SearchRelevanceRanker.ScoreAlbum(query, album)))
                .Concat(playlists.Select(playlist => (
                    new MusicSearchEntry<TPayload> { Playlist = playlist },
                    SearchRelevanceRanker.Score(query, playlist.Name, playlist.CuratorName))))),
            // Beside the library artist, each name has one streaming artist.
            Rank(query, nativeArtists, OneServiceArtistPerName(artists.Where(artist => !IsLinked(artist, linkedProviderArtists)))
                .Select(artist => (
                    new MusicSearchEntry<TPayload> { Artist = artist },
                    SearchRelevanceRanker.Score(query, artist.Name)))));
    }

    /// <summary>Ranks all kinds together for clients with one combined result limit.</summary>
    public static IEnumerable<MusicSearchEntry<TPayload>> RankTogether<TPayload>(
        string query, MusicSearchResult<TPayload> result)
    {
        var knownDurationTitles = KnownDurationTitles(result.Songs);
        return result.Artists.Concat(result.Albums).Concat(result.Songs)
            .OrderByDescending(entry => entry switch
            {
                { Native.Kind: MusicItemKind.Song } or { Song: not null } => SongScore(query, entry, knownDurationTitles),
                { Native: { } native } => SearchRelevanceRanker.Score(query, native.Name, native.ArtistText),
                { Album: { } album } => SearchRelevanceRanker.ScoreAlbum(query, album),
                { Artist: { } artist } => SearchRelevanceRanker.Score(query, artist.Name),
                { Playlist: { } playlist } => SearchRelevanceRanker.Score(query, playlist.Name, playlist.CuratorName),
                _ => 0
            })
            .ThenBy(entry => entry.Native == null);
    }

    private static List<MusicSearchEntry<TPayload>> RankSongs<TPayload>(
        string query, IEnumerable<MusicSearchEntry<TPayload>> entries)
    {
        var rows = entries.ToList();
        var knownDurationTitles = KnownDurationTitles(rows);
        return rows.OrderByDescending(entry => SongScore(query, entry, knownDurationTitles))
            .ThenBy(entry => entry.Native == null).ToList();
    }

    private static HashSet<string> KnownDurationTitles<TPayload>(IEnumerable<MusicSearchEntry<TPayload>> entries) =>
        entries.Where(entry => entry.Native?.DurationSeconds is > 0 || entry.Song?.Duration is > 0)
            .Select(entry => RecordingTitle(entry))
            .ToHashSet(StringComparer.Ordinal);

    private static string RecordingTitle<TPayload>(MusicSearchEntry<TPayload> entry) =>
        SearchRelevanceRanker.RecordingTitle(entry.Native?.Name ?? entry.Song!.Title,
            entry.Native?.ArtistText ?? entry.Song?.Artist, entry.Native?.AlbumArtistText ?? entry.Song?.AlbumArtist);

    private static double SongScore<TPayload>(string query, MusicSearchEntry<TPayload> entry,
        IReadOnlySet<string> knownDurationTitles)
    {
        var knownDuration = knownDurationTitles.Contains(RecordingTitle(entry));
        return entry.Native is { } native
            ? SearchRelevanceRanker.ScoreSong(query, native.Name, native.ArtistText, native.AlbumText,
                native.AlbumArtistText, native.DurationSeconds, provenRecording: true,
                hasKnownDurationVersion: knownDuration)
            : SearchRelevanceRanker.ScoreSong(query, entry.Song!, knownDuration);
    }

    // The library check gets what is left of a two-second search, never under 300 ms, so quick services leave it room
    // and a slow one can't stretch the answer much past the provider cap.
    internal static TimeSpan EnrichmentBudget(long elapsedMilliseconds) =>
        TimeSpan.FromMilliseconds(Math.Max(300, 2_000 - elapsedMilliseconds));

    public async Task<IReadOnlyList<MusicSearchEntry<TPayload>>> ProjectSearchSongsAsync<TPayload>(
        ProtocolExecutionContext context, IReadOnlyList<Song> songs, INativeMusicSearch<TPayload> native,
        CancellationToken cancellationToken = default)
    {
        using var window = new CancellationTokenSource(TimeSpan.FromSeconds(1), timeProvider ?? TimeProvider.System);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.CancellationToken, window.Token);
        try { return await Task.Run(PrepareAsync).WaitAsync(stop.Token); }
        catch (OperationCanceledException) when (window.IsCancellationRequested && !cancellationToken.IsCancellationRequested &&
            !context.CancellationToken.IsCancellationRequested)
        { return songs.Select(song => new MusicSearchEntry<TPayload> { Song = song }).ToArray(); }

        async Task<IReadOnlyList<MusicSearchEntry<TPayload>>> PrepareAsync()
        {
            var copies = songs.Select(song => song.Copy()).ToArray();
            if (savedLibrary != null) await savedLibrary.PreferSavedIdsAsync(context, copies, [], stop.Token);
            return await ProjectSongsAsync(context, copies, native, stop.Token, cacheSearch: true);
        }
    }

    /// <summary>Projects a provider mix through the same accessible native recording choices as search.</summary>
    public async Task<IReadOnlyList<MusicSearchEntry<TPayload>>> ProjectSongsAsync<TPayload>(
        ProtocolExecutionContext context, IReadOnlyList<Song> songs, INativeMusicSearch<TPayload> native,
        CancellationToken cancellationToken = default, bool cacheSearch = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyDictionary<string, NativeMusicItem<TPayload>> owned;
        try
        {
            owned = await ResolveOwnedSongsAsync(context, songs, [], native, cancellationToken, cacheSearch);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            owned = new Dictionary<string, NativeMusicItem<TPayload>>();
        }
        var result = new List<MusicSearchEntry<TPayload>>();
        var seenItems = new HashSet<string>(StringComparer.Ordinal);
        var seenRecordings = new HashSet<Guid>();
        foreach (var song in songs)
        {
            owned.TryGetValue(song.Id, out var copy);
            if (!seenItems.Add(copy == null ? $"provider:{song.Id}" : $"native:{copy.Id}")) continue;
            if (song.CanonicalRecordingId is { } recording && recording != Guid.Empty &&
                !seenRecordings.Add(recording)) continue;
            result.Add(copy == null ? new() { Song = song } : new() { Native = copy });
        }
        return result;
    }

    private async Task PreferOwnedSongsAsync<TPayload>(
        ProtocolExecutionContext context,
        List<Song> songs,
        List<NativeMusicItem<TPayload>> nativeSongs,
        INativeMusicSearch<TPayload> native,
        CancellationToken cancellationToken)
    {
        var owned = await ResolveOwnedSongsAsync(context, songs, nativeSongs, native, cancellationToken, cacheSearch: true);
        var existing = nativeSongs.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var song in songs)
            if (owned.TryGetValue(song.Id, out var copy) && existing.Add(copy.Id)) nativeSongs.Add(copy);
        songs.RemoveAll(song => owned.ContainsKey(song.Id));
    }

    private async Task<IReadOnlyDictionary<string, NativeMusicItem<TPayload>>> ResolveOwnedSongsAsync<TPayload>(
        ProtocolExecutionContext context, IReadOnlyList<Song> songs,
        IReadOnlyList<NativeMusicItem<TPayload>> existing,
        INativeMusicSearch<TPayload> native, CancellationToken cancellationToken, bool cacheSearch = false)
    {
        if (libraryIndex == null || songs.Count == 0) return new Dictionary<string, NativeMusicItem<TPayload>>();
        var lookups = songs.Select(song =>
                new LibraryRecordingLookup(song.Id, song.CanonicalRecordingId, song.Isrc, song.ExternalProvider, song.ExternalId))
            .ToArray();
        var matches = cacheSearch
            ? await FindOwnedSearchTracksAsync(context, lookups, cancellationToken)
            : await libraryIndex.FindKnownRecordingTracksAsync(context, lookups, cancellationToken);
        var ids = matches.Values.Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var present = existing.Where(item => ids.Contains(item.Id)).ToArray();
        var missing = ids.Except(present.Select(item => item.Id), StringComparer.Ordinal).ToArray();
        IReadOnlyList<NativeMusicItem<TPayload>> fetched = [];
        try
        {
            if (missing.Length > 0) fetched = await native.GetSongsAsync(missing, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested) { }
        fetched = present.Concat(fetched.Where(item => ids.Contains(item.Id))).DistinctBy(item => item.Id).ToArray();
        var byId = fetched.OfType<NativeMusicItem<TPayload>>()
            .Where(item => context.Policy?.Includes(item.ExplicitContentLyrics) != false)
            .ToDictionary(item => item.Id, StringComparer.Ordinal);
        return matches.Where(item => byId.ContainsKey(item.Value))
            .ToDictionary(item => item.Key, item => byId[item.Value], StringComparer.Ordinal);
    }

    private async Task<IReadOnlyDictionary<string, string>> FindOwnedSearchTracksAsync(
        ProtocolExecutionContext context, LibraryRecordingLookup[] lookups, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        context.CancellationToken.ThrowIfCancellationRequested();
        var clock = timeProvider ?? TimeProvider.System;
        long? libraryRevision = null;
        if (factory != null)
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            libraryRevision = db.LibraryReadRevision;
        }
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            context.Protocol,
            context.BackendInstanceId,
            context.VerifiedBackendPrincipalId,
            context.Actor,
            context.Policy,
            Settings = settings?.Version,
            Library = libraryRevision,
            Lookups = lookups
        }))));
        OwnedSearchLookup cached;
        bool shared;
        lock (ownedSearchLookups)
        {
            if (!ownedSearchLookups.TryGetValue(key, out cached!) || cached.ExpiresAt <= clock.GetUtcNow())
            {
                if (ownedSearchLookups.Count >= 256)
                    ownedSearchLookups.Remove(ownedSearchLookups.MinBy(item => item.Value.ExpiresAt).Key);
                cached = new(clock.GetUtcNow().AddMinutes(10), new(async () =>
                    await libraryIndex!.FindKnownRecordingTracksAsync(context, lookups, CancellationToken.None)));
                ownedSearchLookups[key] = cached;
            }
            shared = cached.Work.IsValueCreated;
        }
        using var waiting = shared ? RequestTiming.Current?.Stage("lock") : null;
        try { return await cached.Work.Value.WaitAsync(cancellationToken).WaitAsync(context.CancellationToken); }
        catch
        {
            if (cached.Work.IsValueCreated && (cached.Work.Value.IsFaulted || cached.Work.Value.IsCanceled))
                lock (ownedSearchLookups)
                    if (ownedSearchLookups.GetValueOrDefault(key) == cached) ownedSearchLookups.Remove(key);
            throw;
        }
    }

    private static List<MusicSearchEntry<TPayload>> Rank<TPayload>(
        string query,
        IEnumerable<NativeMusicItem<TPayload>> native,
        IEnumerable<(MusicSearchEntry<TPayload> Entry, double Score)> provided) =>
        SearchRelevanceRanker.Merge(
                native.Select(item => (
                    Entry: new MusicSearchEntry<TPayload> { Native = item },
                    Score: SearchRelevanceRanker.Score(query, item.Name, item.ArtistText))),
                provided,
                item => item.Score)
            .Select(item => item.Entry)
            .ToList();
}

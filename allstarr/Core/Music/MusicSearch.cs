using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Models.Search;
using allstarr.Models.Subsonic;
using allstarr.Services.Common;

namespace allstarr.Core.Music;

public enum MusicItemKind
{
    Song,
    Album,
    Artist
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
}

/// <summary>One merged search row: a native backend payload or a typed provider item.</summary>
public sealed record MusicSearchEntry<TPayload>
{
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
    bool HideProviderArtistsNamedLikeNative = false);

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
}

/// <summary>
/// Integrated search shared by every protocol: backend and provider rows are fetched together,
/// provider songs the listener already owns are replaced by the native copy, and each kind is
/// ranked by relevance with native rows winning ties.
/// </summary>
public sealed class MusicSearchService(
    IProtocolProviderGateway providers,
    ILibraryIndexService? libraryIndex = null,
    SavedLibraryReader? savedLibrary = null)
{
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
        bool Compatible(Song left, Song right, bool sparse) =>
            (allstarr.Core.Providers.AppleMusicKit.AppleMusicClient.NormalizeProviderId(left.ExternalProvider ?? "") !=
             allstarr.Core.Providers.AppleMusicKit.AppleMusicClient.NormalizeProviderId(right.ExternalProvider ?? "") ||
             sparse && RecordingDuration(left) is not > 0 && RecordingDuration(right) is not > 0) &&
            MetadataRecordingsMatch(left, right, sparse) && compatibleEvidence?.Invoke(left, right) != false;
        foreach (var song in rows.Where(song => RecordingDuration(song) is > 0))
        {
            var group = groups.FirstOrDefault(members => members.All(member => Compatible(member, song, false)));
            if (group == null) groups.Add([song]);
            else group.Add(song);
        }
        foreach (var song in rows.Where(song => RecordingDuration(song) is not > 0))
        {
            var group = groups.FirstOrDefault(members => members.Any(member => RecordingDuration(member) is > 0) &&
                members.All(member => Compatible(member, song, true)));
            if (group == null) groups.Add([song]);
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

        var nativeTask = native.SearchAsync(cancellationToken);
        var providerTask = request.IncludeProviders
            ? providers.SearchAsync(
                context,
                request.Query,
                request.Songs.Fetch,
                request.Albums.Fetch,
                request.Artists.Fetch)
            : Task.FromResult(new SearchResult());
        var playlistTask = request.IncludeProviders && request.Playlists.Fetch > 0
            ? providers.SearchPlaylistsAsync(context, request.Query, request.Playlists.Fetch)
            : Task.FromResult(new List<ExternalPlaylist>());
        await Task.WhenAll(nativeTask, providerTask, playlistTask);

        var nativeRows = await nativeTask;
        var nativeSongs = nativeRows.Where(item => item.Kind == MusicItemKind.Song).ToList();
        var nativeAlbums = nativeRows.Where(item => item.Kind == MusicItemKind.Album).ToList();
        var nativeArtists = nativeRows.Where(item => item.Kind == MusicItemKind.Artist).ToList();
        var provided = await providerTask;
        var songs = request.Songs.Apply(provided.Songs).ToList();
        var albums = provided.Albums.ToList();
        var artists = request.Artists.Apply(provided.Artists).ToList();
        var playlists = request.Playlists.Apply(await playlistTask).ToList();

        if (savedLibrary != null)
        {
            try
            {
                await savedLibrary.PreferSavedIdsAsync(context, songs, albums, cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested && !context.CancellationToken.IsCancellationRequested) { }
            songs = songs.DistinctBy(song => song.Id, StringComparer.Ordinal).ToList();
        }
        albums = request.Albums.Apply(MusicAlbumReleases.Collapse(albums)).ToList();
        await PreferOwnedSongsAsync(context, songs, nativeSongs, native, cancellationToken);
        return Merge(
            request.Query,
            nativeSongs.Concat(nativeAlbums).Concat(nativeArtists),
            songs,
            albums,
            artists,
            playlists,
            request.HideProviderArtistsNamedLikeNative);
    }

    /// <summary>Ranks native and provider rows of each kind; native rows win ties.</summary>
    public static MusicSearchResult<TPayload> Merge<TPayload>(
        string query,
        IEnumerable<NativeMusicItem<TPayload>> nativeRows,
        IEnumerable<Song> songs,
        IEnumerable<Album> albums,
        IEnumerable<Artist> artists,
        IEnumerable<ExternalPlaylist> playlists,
        bool hideProviderArtistsNamedLikeNative)
    {
        var native = nativeRows.ToList();
        var nativeArtists = native.Where(item => item.Kind == MusicItemKind.Artist).ToList();
        var nativeAlbums = native.Where(item => item.Kind == MusicItemKind.Album).ToList();
        var nativeAlbumIds = nativeAlbums.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var mergedAlbums = MusicAlbumReleases.Collapse(albums.Concat(nativeAlbums.Select(item => item.AlbumMetadata).OfType<Album>()))
            .Where(album => !album.IsLocal || !nativeAlbumIds.Contains(album.Id));
        var nativeArtistNames = hideProviderArtistsNamedLikeNative
            ? nativeArtists.Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        return new MusicSearchResult<TPayload>(
            RankSongs(query, native.Where(item => item.Kind == MusicItemKind.Song)
                .Select(item => new MusicSearchEntry<TPayload> { Native = item })
                .Concat(songs.Select(song => new MusicSearchEntry<TPayload> { Song = song }))),
            Rank(query, nativeAlbums, mergedAlbums.Select(album => (
                    new MusicSearchEntry<TPayload> { Album = album },
                    SearchRelevanceRanker.Score(query, album.Title, album.Artist)))
                .Concat(playlists.Select(playlist => (
                    new MusicSearchEntry<TPayload> { Playlist = playlist },
                    SearchRelevanceRanker.Score(query, playlist.Name, playlist.CuratorName))))),
            Rank(query, nativeArtists, artists
                .Where(artist => !nativeArtistNames.Contains(artist.Name))
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
                { Album: { } album } => SearchRelevanceRanker.Score(query, album.Title, album.Artist),
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

    /// <summary>Projects a provider mix through the same accessible native recording choices as search.</summary>
    public async Task<IReadOnlyList<MusicSearchEntry<TPayload>>> ProjectSongsAsync<TPayload>(
        ProtocolExecutionContext context, IReadOnlyList<Song> songs, INativeMusicSearch<TPayload> native,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyDictionary<string, NativeMusicItem<TPayload>> owned;
        try
        {
            owned = await ResolveOwnedSongsAsync(context, songs, [], native, cancellationToken);
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
        var owned = await ResolveOwnedSongsAsync(context, songs, nativeSongs, native, cancellationToken);
        var existing = nativeSongs.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var song in songs)
            if (owned.TryGetValue(song.Id, out var copy) && existing.Add(copy.Id)) nativeSongs.Add(copy);
        songs.RemoveAll(song => owned.ContainsKey(song.Id));
    }

    private async Task<IReadOnlyDictionary<string, NativeMusicItem<TPayload>>> ResolveOwnedSongsAsync<TPayload>(
        ProtocolExecutionContext context, IReadOnlyList<Song> songs,
        IReadOnlyList<NativeMusicItem<TPayload>> existing,
        INativeMusicSearch<TPayload> native, CancellationToken cancellationToken)
    {
        if (libraryIndex == null || songs.Count == 0) return new Dictionary<string, NativeMusicItem<TPayload>>();
        var matches = await libraryIndex.FindKnownRecordingTracksAsync(context, songs.Select(song =>
                new LibraryRecordingLookup(song.Id, song.CanonicalRecordingId, song.Isrc, song.ExternalProvider, song.ExternalId))
            .ToArray(), cancellationToken);
        using var slots = new SemaphoreSlim(4);
        var fetched = await Task.WhenAll(matches.Values.Distinct(StringComparer.Ordinal).Select(async id =>
        {
            var present = existing.FirstOrDefault(item => item.Id == id);
            if (present != null) return present;
            await slots.WaitAsync(cancellationToken);
            try
            {
                var copy = await native.GetSongAsync(id, cancellationToken);
                return copy?.Id == id ? copy : null;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            finally { slots.Release(); }
        }));
        var byId = fetched.OfType<NativeMusicItem<TPayload>>().ToDictionary(item => item.Id, StringComparer.Ordinal);
        return matches.Where(item => byId.ContainsKey(item.Value))
            .ToDictionary(item => item.Key, item => byId[item.Value], StringComparer.Ordinal);
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

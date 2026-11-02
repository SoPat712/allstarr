using System.Globalization;
using allstarr.Core.Enrichment;
using allstarr.Core.Favorites;
using allstarr.Core.Intelligence;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Services.Common;
using allstarr.Services.Local;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Music;

public sealed partial class PersonalMixService
{
    private sealed class GenerationInputs
    {
        internal int Reads, Failures, Offered, InboxEntries;
        internal string Kind = "on-repeat";
        internal HashSet<string>? ReadKinds;
        internal bool ReusePicks;
        internal string? TagChartStatus;
        internal HashSet<string> Filled = [];
        internal HashSet<string> FreshSongs = [];
        internal HashSet<string> ReleaseArtists = new(StringComparer.OrdinalIgnoreCase);
        internal Dictionary<string, string[]> CachedTags = [];
        internal Dictionary<string, int> RecipeReads = [];
        // Finding your own top songs at a service is shared groundwork, so it has its own allowance. Charged to whichever
        // mix came first, a long history used that mix's whole allowance before it could look for anything new.
        internal const string HistoryLookups = "history-lookups";
        internal bool CanRead => (Kind == HistoryLookups ? ReadKinds is not { Count: 0 } : ReadKinds == null || ReadKinds.Contains(Kind)) &&
            Reads < MixRecipes.GenerationReads && RecipeReads.GetValueOrDefault(Kind) < (Kind == HistoryLookups ? MixRecipes.HistoryLookupReads : MixRecipes.RecipeReads);
        internal string[] Playable = [];
        internal Dictionary<string, int> Excluded = [];
        internal Dictionary<string, DateTimeOffset> ReleaseDates = [];
        internal Dictionary<string, HashSet<string>> ChartTags = [];
        internal Dictionary<string, (string? Seed, string? Tag, string Source, string? Release)> Evidence = [];
        internal void Exclude(string name) => Excluded[name] = Excluded.GetValueOrDefault(name) + 1;
        internal async Task<T?> ReadAsync<T>(ProtocolExecutionContext context, Func<ProtocolExecutionContext, Task<T>> read,
            CancellationToken token) where T : class?
        {
            if (!CanRead) return default;
            Reads++;
            RecipeReads[Kind] = RecipeReads.GetValueOrDefault(Kind) + 1;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var bounded = new ProtocolExecutionContext(context.Protocol, context.BackendInstanceId, context.VerifiedBackendPrincipalId,
                context.Principal, context.CorrelationId, DateTimeOffset.UtcNow.AddSeconds(5), timeout.Token, context.Client)
            { Policy = context.Policy, IsReadOnly = false };
            try { return await read(bounded).WaitAsync(timeout.Token); }
            catch (Exception) when (!token.IsCancellationRequested) { Failures++; return default; }
        }
    }

    private async Task<(IReadOnlyList<MixCandidate> Candidates, string? SeedName)> CandidatesAsync(AllstarrDbContext db, UserRecord user,
        ListeningEventRecord[] events, CancellationToken token, MixStartRequest? start = null, GenerationInputs? inputs = null, IReadOnlyList<MixFillSong>? saved = null)
    {
        var context = await ContextAsync(user, token);
        var access = await libraryAccess.ResolveAsync(context, token);
        if (start != null) return await StartCandidatesAsync(db, context, access, user, events, start, token);
        var known = events.Select(item => item.LibraryTrackId).OfType<Guid>().Distinct().ToArray();
        var query = LibraryTrackAccess.Query(db, context, access);
        var library = await query.OrderByDescending(item => known.Contains(item.Id))
            .ThenBy(item => item.BackendItemId).Take(600).ToArrayAsync(token);
        var rows = new List<(Song Song, string Source, DateTimeOffset? Release)>();
        var hearts = (await new FavoriteViewReader(factory).ReadAsync(context, token)).Where(item => item.Display.Kind == "song").Take(300).ToArray();
        rows.AddRange(hearts.Select(item => (item.Song(), "heart", (DateTimeOffset?)null)));
        rows.AddRange(library.Select(track => (LibrarySong(track), "library", (DateTimeOffset?)null)));
        inputs ??= new();
        var historyRecordings = events.Select(play => play.CanonicalRecordingId).OfType<Guid>().ToHashSet();
        var historyReferences = events.Select(play => play.TrackReference).ToHashSet();
        rows.AddRange((saved ?? []).Where(row => row.Filled || row.Source != "history" || historyReferences.Contains(row.Song.Id) ||
            row.Song.CanonicalRecordingId is { } id && historyRecordings.Contains(id)).Select(row => (row.Song, row.Source, row.Release)));
        var recent = RecentHistory(events);
        inputs.Playable = (await providers.GetPlayableProviderOrderAsync(context.Actor!, token) ?? []).Where(provider => context.Policy?.IsAutomatic(provider) != false).ToArray();
        var recipeKind = inputs.Kind;
        inputs.Kind = GenerationInputs.HistoryLookups;
        foreach (var group in events.Where(item => item.State == ListeningEventState.Completed)
            .GroupBy(item => item.CanonicalRecordingId?.ToString("D") ?? item.TrackReference)
            .OrderByDescending(group => group.Count(play => recent.Contains(play.Id))).ThenByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal).Take(PersonalMixRules.TopSongs))
        {
            var play = group.OrderByDescending(item => item.ListenedAt).First();
            if (rows.Any(item => item.Song.Id == play.TrackReference || play.CanonicalRecordingId != null &&
                item.Song.CanonicalRecordingId == play.CanonicalRecordingId)) continue;
            var parsed = LocalLibraryService.ParseExternalResource(play.TrackReference);
            Song? song = play.CanonicalRecordingId is { } recording
                ? await providers.GetStoredSongAsync(context, "allstarr", recording.ToString("D")) : null;
            if (song == null && parsed.isExternal && parsed.type == "song")
            {
                song = await providers.GetStoredSongAsync(context, parsed.provider!, parsed.externalId!);
                if (song == null)
                {
                    song = await inputs.ReadAsync(context, c => providers.GetSongAsync(c, parsed.provider!, parsed.externalId!), token);
                    if (song != null) inputs.FreshSongs.Add(song.Id);
                }
            }
            else if (song == null && play.LibraryTrackId == null && !string.IsNullOrWhiteSpace(play.Title) && !string.IsNullOrWhiteSpace(play.Artist))
            {
                var seed = new Song
                {
                    Title = play.Title,
                    Artist = play.Artist,
                    Album = play.Album ?? "",
                    Duration = (int?)(play.DurationMilliseconds / 1000),
                    Isrc = play.Isrc
                };
                foreach (var provider in inputs.Playable)
                {
                    var found = await inputs.ReadAsync(context, c => providers.SearchAutomaticProviderSongsAsync(c, provider, play.Title + " " + play.Artist, 3), token);
                    if (found?.IsSuccess == true) song = found.RequireValue().FirstOrDefault(candidate => MusicSearchService.MetadataRecordingsMatch(seed, candidate));
                    else if (found != null) inputs.Failures++;
                    if (song != null) { inputs.FreshSongs.Add(song.Id); break; }
                }
            }
            if (song != null) { song.CanonicalRecordingId ??= play.CanonicalRecordingId; rows.Add((song, "history", null)); }
        }
        inputs.Kind = recipeKind;
        return (await RankCandidatesAsync(rows, context, user, events, hearts, token, inputs), null);
    }

    private async Task<IReadOnlyList<MixCandidate>> FillAsync(UserRecord user, IReadOnlyList<MixCandidate> own,
        ListeningEventRecord[] events, GenerationInputs inputs, string[] kinds, PersonalMixDocument document,
        IReadOnlyDictionary<string, allstarr.Core.AudioMuse.AudioMuseScores> features, CancellationToken token)
    {
        var inbox = kinds.Contains("release-radar") ? await releases.ReadAsync(user.Id, token) : null;
        inputs.InboxEntries = inbox?.Entries.Count ?? 0;
        var followed = kinds.Contains("release-radar") ? await releases.ListAsync(user.Id, token) : [];
        inputs.ReleaseArtists = followed.Select(artist => artist.DisplayName).Concat(events.Where(play => play.State == ListeningEventState.Completed)
            .GroupBy(play => play.Artist, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() >= MixRecipes.FrequentPlays)
            .Select(group => group.Key).OfType<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (inputs.Playable.Length == 0) return own;
        var context = await ContextAsync(user, token);
        var all = own.ToList();
        var recordings = new RecordingIndex();
        foreach (var row in own) recordings.Add(row.Song);
        var playedArtists = events.Where(item => item.State == ListeningEventState.Completed && !string.IsNullOrWhiteSpace(item.Artist))
            .GroupBy(item => item.Artist!, StringComparer.OrdinalIgnoreCase).OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal).ToArray();
        var playedNames = playedArtists.Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var relatedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selections = new Dictionary<string, IReadOnlyList<MixCandidate>>();
        var kept = inputs.ReusePicks ? document.Mixes["discover-weekly"].Songs.SelectMany(item => PersonalMixRules.IdentityKeys(item.Song)).ToHashSet() : [];
        var previous = document.DiscoverPicks.Where(item => item.Value >= clock.UtcNow.AddDays(-14) && !kept.Contains(item.Key)).Select(item => item.Key).ToArray();
        void ApplyEvidence()
        {
            selections.Clear();
            for (var i = 0; i < all.Count; i++)
            {
                var row = all[i];
                if (!inputs.Evidence.TryGetValue(row.Song.Id, out var evidence)) continue;
                var labels = row.Tags.ToDictionary();
                if (inputs.ChartTags.TryGetValue(row.Song.Id, out var chartTags)) foreach (var tag in chartTags) labels[tag] = 1;
                all[i] = row with
                {
                    RelatedTo = evidence.Seed,
                    EvidenceTag = evidence.Tag,
                    EvidenceSource = evidence.Source,
                    Tags = labels,
                    ReleasedAt = inputs.ReleaseDates.GetValueOrDefault(row.Song.Id, row.ReleasedAt ?? default),
                    ReleaseKey = evidence.Release,
                    ChartTags = inputs.ChartTags.GetValueOrDefault(row.Song.Id)?.ToArray()
                };
            }
        }
        IReadOnlyList<MixCandidate> Selected(string kind) => selections.TryGetValue(kind, out var cached) ? cached : selections[kind] = PersonalMixRules.Select(kind,
            RecipePool(kind, document.Mixes[kind].CoverTag, all, features, all), clock.UtcNow, previous);
        var shortKinds = kinds.Where(kind => Selected(kind).Count < MixRecipes.For(kind).Target)
            .OrderBy(kind => Selected(kind).Count >= (PersonalMixMoods.Kinds.Contains(kind) ? 25 : 1)).ToArray();
        foreach (var kind in shortKinds)
        {
            if (kind.StartsWith("daily-", StringComparison.Ordinal) && document.Mixes[kind].CoverTag == null) continue;
            inputs.Kind = kind;
            var recipe = MixRecipes.For(kind);
            bool NeedsSongs() => inputs.CanRead && Selected(kind).Count < recipe.Target;
            async Task<List<Song>> Search(string provider, string query, int count = 100)
            {
                var found = await inputs.ReadAsync(context, c => providers.SearchAutomaticProviderSongsAsync(c, provider, query, count), token);
                if (found?.IsSuccess == true) return found.RequireValue().ToList();
                if (found != null) inputs.Failures++;
                return [];
            }
            async Task Add(IEnumerable<Song> songs, string source, string? seed = null, string? tag = null,
                DateTimeOffset? release = null, string? releaseKey = null)
            {
                var selected = Selected(kind);
                var rows = new List<(Song Song, string Source, DateTimeOffset? Release)>();
                foreach (var song in songs)
                {
                    // Discard results this recipe cannot use before recording comparisons or ranking.
                    if (context.Policy?.Includes(song) == false || selected.Count(row => string.Equals(row.Song.Artist, song.Artist, StringComparison.OrdinalIgnoreCase)) >= recipe.ArtistLimit) continue;
                    if (kind.StartsWith("daily-", StringComparison.Ordinal) && !playedNames.Contains(song.Artist)) continue;
                    var native = recordings.Find(song);
                    var id = native?.Id ?? song.Id;
                    inputs.FreshSongs.Add(id);
                    var prior = inputs.Evidence.GetValueOrDefault(id);
                    inputs.Evidence[id] = (seed ?? prior.Seed, tag ?? prior.Tag, seed != null || prior.Source == null ? source : prior.Source, releaseKey ?? prior.Release);
                    if (release != null) inputs.ReleaseDates[id] = release.Value;
                    if (tag != null)
                    {
                        if (!inputs.ChartTags.TryGetValue(id, out var tags)) inputs.ChartTags[id] = tags = [];
                        tags.Add(tag);
                    }
                    if (native != null) continue;
                    inputs.Filled.Add(song.Id);
                    rows.Add((song, source, release));
                    recordings.Add(song);
                }
                if (rows.Count > 0) all.AddRange(await RankCandidatesAsync(rows, context, user, events, [], token, inputs));
                ApplyEvidence();
            }
            async Task FindRelated()
            {
                foreach (var seed in playedArtists.Take(MixRecipes.TopArtists))
                {
                    if (!NeedsSongs()) break;
                    var reached = false;
                    foreach (var provider in inputs.Playable)
                    {
                        if (!NeedsSongs()) break;
                        var found = await inputs.ReadAsync(context, c => providers.SearchAsync(c, seed.Key, 0, 0, 5, provider), token);
                        var artist = found?.Artists.FirstOrDefault(item => FuzzyMatcher.NormalizeForMatching(item.Name) == FuzzyMatcher.NormalizeForMatching(seed.Key));
                        if (artist?.ExternalId == null) continue;
                        if (kind != "discover-weekly" && NeedsSongs())
                            await Add((await Search(provider, artist.Name)).Where(song => string.Equals(song.Artist, artist.Name, StringComparison.OrdinalIgnoreCase)), provider);
                        foreach (var related in await inputs.ReadAsync(context, c => providers.GetSimilarArtistsAsync(c, provider, artist.ExternalId, 50), token) ?? [])
                        {
                            relatedNames.Add(related.Name);
                            if (related.ExternalId == null || !NeedsSongs() ||
                                kind.StartsWith("daily-", StringComparison.Ordinal) && !playedNames.Contains(related.Name)) continue;
                            var songs = (await Search(related.ExternalProvider ?? provider, related.Name))
                                .Where(song => string.Equals(song.Artist, related.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
                            await Add(songs, provider, seed.Key);
                            reached |= songs.Length > 0;
                        }
                        if (reached) break;
                    }
                    var track = own.Where(row => string.Equals(row.Song.Artist, seed.Key, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(row => row.Plays).ThenBy(row => row.Song.Id, StringComparer.Ordinal).FirstOrDefault();
                    if (!reached && NeedsSongs() && track != null && similarTracks != null)
                        foreach (var suggestion in await inputs.ReadAsync(context, c => similarTracks.FindAsync(c, seed.Key, track.Song.Title, 10, c.CancellationToken), token) ?? [])
                            foreach (var provider in inputs.Playable)
                            {
                                if (!NeedsSongs()) break;
                                var song = (await Search(provider, suggestion.Artist + " " + suggestion.Title, 3))
                                    .FirstOrDefault(song => MusicSearchService.MetadataRecordingsMatch(new Song { Title = suggestion.Title, Artist = suggestion.Artist }, song, allowSparse: true));
                                if (song != null) { relatedNames.Add(song.Artist); await Add([song], "Last.fm", seed.Key); break; }
                            }
                }
            }
            if (kind == "discover-weekly") await FindRelated();
            var styles = kind.StartsWith("daily-", StringComparison.Ordinal) ? new[] { document.Mixes[kind].CoverTag }.OfType<string>()
                : PersonalMixMoods.Kinds.Contains(kind) ? PersonalMixMoods.Wanted(kind) : [];
            foreach (var tag in styles)
            {
                if (!NeedsSongs() || tagSources == null) break;
                if (!await tagSources.HasTagChartKeyAsync(context, token))
                {
                    inputs.TagChartStatus = "No usable Last.fm key; tag charts skipped without reads.";
                    break;
                }
                var chart = await inputs.ReadAsync(context, c => tagSources.TagTopTracksAsync(tag, c.CancellationToken, c), token) ?? [];
                foreach (var artist in chart.GroupBy(track => track.Artist, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(group => playedNames.Contains(group.Key) ? 0 : relatedNames.Contains(group.Key) ? 1 : 2))
                {
                    if (!NeedsSongs()) break;
                    if (kind.StartsWith("daily-", StringComparison.Ordinal) && !playedNames.Contains(artist.Key) ||
                        Selected(kind).Count(row => string.Equals(row.Song.Artist, artist.Key, StringComparison.OrdinalIgnoreCase)) >= recipe.ArtistLimit) continue;
                    foreach (var provider in inputs.Playable)
                    {
                        if (!NeedsSongs()) break;
                        var found = await Search(provider, artist.Key);
                        await Add(found.Where(song => artist.Any(track => MusicSearchService.MetadataRecordingsMatch(
                            new Song { Title = track.Title, Artist = track.Artist }, song, allowSparse: true))), "Last.fm", tag: tag);
                        if (found.Count > 0) break;
                    }
                }
            }
            if (NeedsSongs() && (kind.StartsWith("daily-", StringComparison.Ordinal) || PersonalMixMoods.Kinds.Contains(kind))) await FindRelated();
            if (kind != "release-radar") continue;
            var releaseArtists = followed.Select(artist => (Name: artist.DisplayName, Provider: (string?)artist.ProviderId, Id: (string?)artist.ArtistId))
                .Concat(playedArtists.Where(group => group.Count() >= recipe.MinimumPlays).Select(group => (Name: group.Key, Provider: (string?)null, Id: (string?)null)))
                .DistinctBy(artist => artist.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var seed in releaseArtists)
                foreach (var provider in seed.Provider == null ? inputs.Playable : inputs.Playable.Where(provider => provider == seed.Provider))
                {
                    if (!NeedsSongs()) break;
                    var found = seed.Id == null ? await Search(provider, seed.Name) : [];
                    var artistSongs = found.Where(song => string.Equals(song.Artist, seed.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
                    foreach (var song in artistSongs)
                        if (ReleaseDate(song.ReleaseDate) is { } at && at >= clock.UtcNow.AddDays(-recipe.WindowDays) && song.AlbumId != null)
                            await Add([song], provider, release: at, releaseKey: provider + ":" + song.AlbumId);
                    if (NeedsSongs() && (artistSongs.Length == 0 || artistSongs.Any(song => ReleaseDate(song.ReleaseDate) == null)))
                    {
                        var artistId = seed.Id ?? artistSongs.SelectMany(song => song.ArtistIds)
                            .Select(LocalLibraryService.ParseExternalResource).Where(id => id.isExternal && id.type == "artist" && id.provider == provider)
                            .Select(id => id.externalId).FirstOrDefault();
                        if (artistId == null)
                        {
                            var artists = await inputs.ReadAsync(context, c => providers.SearchAsync(c, seed.Name, 0, 0, 5, provider), token);
                            artistId = artists?.Artists.FirstOrDefault(artist => string.Equals(artist.Name, seed.Name, StringComparison.OrdinalIgnoreCase))?.ExternalId;
                        }
                        if (artistId != null)
                            foreach (var album in (await inputs.ReadAsync(context, c => providers.GetArtistAlbumsAsync(c, provider, artistId), token) ?? [])
                                .OrderByDescending(album => album.ReleaseDate))
                            {
                                if (!NeedsSongs()) break;
                                if (ReleaseDate(album.ReleaseDate) is not { } at || at < clock.UtcNow.AddDays(-recipe.WindowDays)) continue;
                                var id = album.ExternalId ?? LocalLibraryService.ParseExternalResource(album.Id).externalId;
                                if (id == null) continue;
                                var release = await inputs.ReadAsync(context, c => providers.GetAlbumAsync(c, provider, id), token);
                                await Add((release?.Songs ?? []).Where(song => string.Equals(song.Artist, seed.Name, StringComparison.OrdinalIgnoreCase))
                                    .OrderBy(song => song.Track ?? int.MaxValue).ThenBy(song => song.Id, StringComparer.Ordinal).Take(1),
                                    provider, release: at, releaseKey: provider + ":" + id);
                            }
                    }
                    if (found.Count > 0 || seed.Id != null) break;
                }
            foreach (var entry in inbox!.Entries.OrderByDescending(item => item.ReleaseDate))
                foreach (var source in entry.Sources)
                {
                    if (!NeedsSongs()) break;
                    if (ReleaseDate(entry.ReleaseDate) is not { } at || at < clock.UtcNow.AddDays(-recipe.WindowDays)) continue;
                    var found = await inputs.ReadAsync(context, c => providers.GetAlbumAsync(c, source.ProviderId, source.AlbumId), token);
                    await Add((found?.Songs ?? []).OrderBy(song => song.Track ?? int.MaxValue).ThenBy(song => song.Id, StringComparer.Ordinal),
                        source.ProviderId, release: at, releaseKey: source.ProviderId + ":" + source.AlbumId);
                }
        }
        return all;
    }

    internal sealed class RecordingIndex
    {
        private readonly Dictionary<string, List<(Song Song, int Position)>> buckets = [];
        private int count;
        private static IEnumerable<string> Keys(Song song)
        {
            yield return "id:" + song.Id;
            if (song.CanonicalRecordingId is { } id && id != Guid.Empty) yield return "recording:" + id;
            var isrc = song.Isrc?.Replace("-", "", StringComparison.Ordinal).Trim().ToUpperInvariant();
            if (isrc?.Length == 12) yield return "isrc:" + isrc;
            yield return "title:" + FuzzyMatcher.NormalizeForMatching(FuzzyMatcher.StripDecorators(song.Title));
        }
        internal void Add(Song song)
        {
            foreach (var key in Keys(song))
            {
                if (!buckets.TryGetValue(key, out var bucket)) buckets[key] = bucket = [];
                bucket.Add((song, count));
            }
            count++;
        }
        internal Song? Find(Song song) => Keys(song).SelectMany(key => buckets.GetValueOrDefault(key) ?? [])
            .DistinctBy(row => row.Position).OrderBy(row => row.Position)
            .FirstOrDefault(row => MusicMixService.SameRadioRecording(song, row.Song)).Song;
    }

    private async Task<IReadOnlyList<MixCandidate>> RankCandidatesAsync(List<(Song Song, string Source, DateTimeOffset? Release)> rows,
        ProtocolExecutionContext context, UserRecord user, ListeningEventRecord[] events, FavoriteViewItem[] hearts, CancellationToken token, GenerationInputs? inputs = null, bool recount = false)
    {
        if (inputs != null && !recount) inputs.Offered += rows.Count;
        var unique = new List<(Song Song, string Source, DateTimeOffset? Release)>();
        var recordings = new RecordingIndex();
        foreach (var row in rows.OrderByDescending(item => item.Release).ThenByDescending(item => item.Song.IsLocal).ThenBy(item => item.Song.Id, StringComparer.Ordinal))
            if (unique.Count < 1000 && recordings.Find(row.Song) == null) { unique.Add(row); recordings.Add(row.Song); }
            else if (!recount) inputs?.Exclude(unique.Count >= 1000 ? "Candidate limit" : "Duplicate recordings");
        var songs = unique.ToArray();
        var profiles = await tags.GetTags(songs.Select(item => item.Song.CanonicalRecordingId).OfType<Guid>().Distinct().ToArray(), token);
        var scores = await taste.ScoreManyAsync(user.Id, songs.Select(item => item.Song).ToArray(), token);
        var listening = events.SelectMany(play => PersonalMixRules.IdentityKeys(new Song
        {
            Id = play.TrackReference,
            CanonicalRecordingId = play.CanonicalRecordingId,
            Title = play.Title ?? "",
            Artist = play.Artist ?? "",
            Isrc = play.Isrc
        })
            .Select(key => (Key: key, Play: play))).GroupBy(item => item.Key)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Play).ToArray());
        var recentIds = RecentHistory(events);
        var nudges = (await taste.GetAsync(user.Id, token)).Nudges;
        var result = new List<MixCandidate>();
        var failed = false;
        foreach (var row in songs)
        {
            var score = scores[row.Song];
            string? exclusion = string.IsNullOrWhiteSpace(row.Song.Id) || string.IsNullOrWhiteSpace(row.Song.Title) || string.IsNullOrWhiteSpace(row.Song.Artist)
                ? "Missing song details" : context.Policy?.Includes(row.Song) == false ? "Listening policy" : score.Blocked ? "Never" : null;
            if (exclusion != null) { inputs?.Exclude(exclusion); continue; }
            try
            {
                var playable = row.Song.IsLocal || (inputs != null ? inputs.Playable.Contains(row.Song.ExternalProvider)
                    : await providers.CanPlaySongAsync(context, row.Song));
                if (!playable) { inputs?.Exclude("No playable route"); continue; }
            }
            catch (Exception) when (!token.IsCancellationRequested) { failed = true; inputs?.Exclude("No playable route"); continue; }
            var matched = PersonalMixRules.IdentityKeys(row.Song).SelectMany(key => listening.GetValueOrDefault(key) ?? [])
                .DistinctBy(play => play.Id).ToArray();
            var played = matched.Where(play => play.State == ListeningEventState.Completed).ToArray();
            var skipped = matched.Count(play => play.State == ListeningEventState.Skipped && play.UpdatedAt >= clock.UtcNow.AddDays(-PersonalMixRules.SkipDays) &&
                play.UpdatedAt <= clock.UtcNow && play.PositionTicks is >= 0 && (play.PositionTicks < TimeSpan.FromSeconds(30).Ticks ||
                play.DurationMilliseconds > 0 && play.PositionTicks / 10000d < play.DurationMilliseconds * .25)) >= 3;
            if (skipped) { inputs?.Exclude("Three early skips"); continue; }
            var evidence = inputs?.Evidence.GetValueOrDefault(row.Song.Id) ?? default;
            var features = row.Song.CanonicalRecordingId is { } id && profiles.TryGetValue(id, out var profile)
                ? profile.Tags.Where(item => item.Score > 0 && (inputs?.Evidence.ContainsKey(row.Song.Id) != true || item.Evidence.Any(tag => tag.Source == "lastfm-track" || tag.Source == "provider" && tag.IsGenre))).ToDictionary(item => item.Tag, item => item.Score) : new Dictionary<string, double>();
            foreach (var tag in inputs?.CachedTags.GetValueOrDefault(row.Song.Id) ?? []) features.TryAdd(tag, 1);
            foreach (var genre in row.Song.Genres.Append(row.Song.Genre).OfType<string>())
                if (SharedTagNormalization.Canonical(genre, []) is { } canonical) features.TryAdd(canonical, 1);
            var lastFmTags = row.Song.CanonicalRecordingId is { } recording && profiles.TryGetValue(recording, out var savedTags)
                ? savedTags.Tags.Where(tag => tag.Evidence.Any(item => item.Source == "lastfm-track")).Select(tag => tag.Tag).ToList() : [];
            if (inputs?.ChartTags.TryGetValue(row.Song.Id, out var chartTags) == true)
                foreach (var tag in chartTags) { features[tag] = 1; lastFmTags.Add(tag); }
            if (nudges.Any(nudge => nudge.Mode == "never" && nudge.Kind == "tag" && features.ContainsKey(nudge.Name)))
            { inputs?.Exclude("Never"); continue; }
            var less = nudges.Any(nudge => nudge.Mode == "less" && (nudge.Kind == "song" && nudge.Name == PersonalMixRules.Key(row.Song) ||
                nudge.Kind == "artist" && string.Equals(nudge.Name, row.Song.Artist, StringComparison.OrdinalIgnoreCase) ||
                nudge.Kind == "tag" && features.ContainsKey(nudge.Name)));
            result.Add(new(inputs == null ? row.Song.WithTasteReason(row.Song.TasteReason ?? score.Reasons.FirstOrDefault()) : row.Song, features, played.Length, played.Count(play => recentIds.Contains(play.Id)),
                matched.Select(play => (DateTimeOffset?)(play.ListenedAt ?? play.UpdatedAt)).DefaultIfEmpty().Max(),
                false, row.Source, inputs == null ? score.Score : 0, row.Release, matched.Length > 0, hearts.Any(item => item.ItemId == row.Song.Id),
                played.Count(play => play.ListenedAt >= clock.UtcNow.AddDays(-MixRecipes.For("on-repeat").FallbackDays)), inputs?.Filled.Contains(row.Song.Id) == true,
                evidence.Seed, evidence.Tag, evidence.Source, evidence.Release, less, lastFmTags.Distinct().ToArray()));
        }
        if (inputs == null && result.Count == 0 && failed) throw new InvalidOperationException("Mix sources are unavailable. Try again later.");
        return result;
    }

    private DateTimeOffset? ReleaseDate(string? value) => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
        DateTimeStyles.None, out var date) && date <= DateOnly.FromDateTime(clock.UtcNow.UtcDateTime)
        ? new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null;
}

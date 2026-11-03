using System.Globalization;
using allstarr.Core.Capabilities;
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
    private sealed record HistoryRouteCheck(Song? Song);

    private sealed class GenerationInputs
    {
        internal int Reads, Failures, Offered, InboxEntries;
        internal MixGenerationDiagnostics Diagnostics = new();
        internal string Stage = "initial";
        internal HashSet<string> EligibleReleases = [];
        internal string Kind = "on-repeat";
        internal HashSet<string>? ReadKinds;
        internal bool ReusePicks;
        internal string? TagChartStatus;
        internal HashSet<string> Filled = [];
        internal HashSet<string> FreshSongs = [];
        internal Dictionary<string, HashSet<string>> HistoryReferences = [];
        internal HashSet<string> ReleaseArtists = new(StringComparer.OrdinalIgnoreCase);
        internal Dictionary<string, string[]> CachedTags = [];
        internal Dictionary<string, int> RecipeReads = [];
        // Finding your own top songs at a service is shared groundwork, so it has its own allowance. Charged to whichever
        // mix came first, a long history used that mix's whole allowance before it could look for anything new.
        internal const string HistoryLookups = "history-lookups", InboxLookups = "release-inbox", PlaybackChecks = "playback-checks", InboxPlaybackChecks = "release-playback-checks";
        internal int InboxLimit;
        internal int SharedReads => Reads - RecipeReads.GetValueOrDefault(InboxLookups) - RecipeReads.GetValueOrDefault(InboxPlaybackChecks);
        internal bool CanRead => (Kind == HistoryLookups ? ReadKinds is not { Count: 0 } : Kind == PlaybackChecks ? Playable.Length > 0
            : ReadKinds == null || ReadKinds.Contains(Kind == InboxLookups || Kind == InboxPlaybackChecks ? "release-radar" : Kind)) &&
            (Kind == InboxLookups || Kind == InboxPlaybackChecks || SharedReads < MixRecipes.GenerationReads) && RecipeReads.GetValueOrDefault(Kind) < (Kind switch
            {
                HistoryLookups => MixRecipes.HistoryLookupReads,
                InboxLookups => InboxLimit,
                InboxPlaybackChecks => InboxLimit * 2,
                PlaybackChecks => MixRecipes.PlaybackCheckReads,
                _ => MixRecipes.RecipeReads
            });
        internal string[] Playable = [];
        internal Dictionary<string, int> Excluded = [];
        internal Dictionary<string, DateTimeOffset> ReleaseDates = [];
        internal Dictionary<string, HashSet<string>> ChartTags = [];
        internal Dictionary<string, (string? Seed, string? Tag, string Source, string? Release)> Evidence = [];
        internal void Exclude(string name, string? songId = null)
        {
            Excluded[name] = Excluded.GetValueOrDefault(name) + 1;
            Diagnostics.Count(Stage + ".excluded." + name);
            if (Diagnostics.CurrentEntry is { } current) current.Count(name);
            else if (Stage is "final" or "revalidate" && songId != null && Evidence.TryGetValue(songId, out var evidence))
                foreach (var entry in Diagnostics.Inbox.Where(entry => evidence.Release != null && entry.Keys.Contains(evidence.Release)))
                    entry.Count(Stage + "." + name);
        }
        internal void Failure(string provider, string kind)
        {
            Failures++;
            Diagnostics.Failure(provider, kind);
            Diagnostics.CurrentEntry?.Count("Provider " + kind);
        }
        internal async Task<T?> ReadAsync<T>(ProtocolExecutionContext context, Func<ProtocolExecutionContext, Task<T>> read,
            CancellationToken token, string provider = "unknown") where T : class?
        {
            if (!CanRead) { Diagnostics.Count(Kind + ".allowance exhausted"); return default; }
            using var phase = Diagnostics.Phase("provider");
            Diagnostics.Count(Kind + ".reads");
            Reads++;
            Diagnostics.Reads++;
            RecipeReads[Kind] = RecipeReads.GetValueOrDefault(Kind) + 1;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var bounded = new ProtocolExecutionContext(context.Protocol, context.BackendInstanceId, context.VerifiedBackendPrincipalId,
                context.Principal, context.CorrelationId, DateTimeOffset.UtcNow.AddSeconds(5), timeout.Token, context.Client)
            { Policy = context.Policy, IsReadOnly = false };
            try
            {
                var result = await read(bounded).WaitAsync(timeout.Token);
                if (result is ProviderOutcome<IReadOnlyList<Song>> { IsSuccess: false } outcome) Failure(provider, outcome.Error!.Kind.ToString());
                if (result == null) Diagnostics.Count(Kind + ".empty response");
                return result;
            }
            catch (Exception error) when (!token.IsCancellationRequested) { Failure(provider, MixGenerationDiagnostics.ErrorKind(error)); return default; }
        }
    }

    private async Task<(IReadOnlyList<MixCandidate> Candidates, string? SeedName)> CandidatesAsync(AllstarrDbContext db, UserRecord user,
        ListeningEventRecord[] events, CancellationToken token, MixStartRequest? start = null, GenerationInputs? inputs = null, IReadOnlyList<MixFillSong>? saved = null,
        SeedMixDiagnostics? diagnostics = null)
    {
        ProtocolExecutionContext context;
        BackendLibraryAccess access;
        using (diagnostics?.Phase("access"))
        {
            context = await ContextAsync(user, token);
            access = await libraryAccess.ResolveAsync(context, token);
        }
        if (start != null) return await StartCandidatesAsync(db, context, access, user, events, start, token, diagnostics);
        var known = events.Select(item => item.LibraryTrackId).OfType<Guid>().Distinct().ToArray();
        var historyRecordings = events.Select(play => play.CanonicalRecordingId).OfType<Guid>().ToHashSet();
        var query = LibraryTrackAccess.Query(db, context, access);
        var library = await query.OrderByDescending(item => known.Contains(item.Id) ||
                item.CanonicalRecordingId != null && historyRecordings.Contains(item.CanonicalRecordingId.Value))
            .ThenBy(item => item.BackendItemId).Take(600).ToArrayAsync(token);
        var rows = new List<(Song Song, string Source, DateTimeOffset? Release)>();
        var hearts = (await new FavoriteViewReader(factory).ReadAsync(context, token)).Where(item => item.Display.Kind == "song").Take(300).ToArray();
        rows.AddRange(hearts.Select(item =>
        {
            var song = item.Song();
            song.Genres = item.Display.Genres.ToList();
            return (song, "heart", (DateTimeOffset?)null);
        }));
        rows.AddRange(library.Select(track => (LibrarySong(track), "library", (DateTimeOffset?)null)));
        inputs ??= new();
        inputs.Diagnostics.Count("library", library.Length);
        inputs.Diagnostics.Count("hearts", hearts.Length);
        inputs.Diagnostics.Count("saved", (saved ?? []).Count);
        var historyReferences = events.Select(play => play.TrackReference).ToHashSet();
        rows.AddRange((saved ?? []).Where(row => row.Filled || row.Source != "history" || historyReferences.Contains(row.Song.Id) ||
            row.Song.CanonicalRecordingId is { } id && historyRecordings.Contains(id)).Select(row => (row.Song, row.Source, row.Release)));
        var recent = RecentHistory(events);
        inputs.Playable = (await providers.GetPlayableProviderOrderAsync(context.Actor!, token) ?? []).Where(provider => context.Policy?.IsAutomatic(provider) != false).ToArray();
        var identities = await db.ProviderTrackIdentities.AsNoTracking().Where(item => historyRecordings.Contains(item.CanonicalRecordingId) &&
            item.ResourceKind == ProviderResourceKind.Track &&
            db.CanonicalRecordings.Any(recording => recording.Id == item.CanonicalRecordingId && recording.MergedIntoId == null) &&
            (item.Scope == ProviderIdentityScope.Catalog || db.ProviderAccounts.Any(account => account.Id == item.ProviderAccountId &&
                account.Enabled && (account.OwnerUserId == user.Id || account.OwnerUserId == null)))).ToArrayAsync(token);
        var routes = identities.Where(CatalogIdentityPolicy.IsPlaybackEligible).Where(item => inputs.Playable.Contains(item.ProviderId))
            .GroupBy(item => item.CanonicalRecordingId).ToDictionary(group => group.Key,
                group => CatalogIdentityPolicy.OrderForPlayback(group).OrderBy(item => Array.IndexOf(inputs.Playable, item.ProviderId)).ToArray());
        async Task<HistoryRouteCheck?> ResolvedHistorySong(IGrouping<string, ListeningEventRecord> group)
        {
            var plays = group.Where(play => play.CanonicalRecordingId != null).OrderByDescending(play => play.ListenedAt).ToArray();
            var attempted = new HashSet<string>();
            foreach (var play in plays.DistinctBy(play => play.TrackReference))
            {
                var parsed = LocalLibraryService.ParseExternalResource(play.TrackReference);
                if (parsed.isExternal && parsed.type == "song" && inputs.Playable.Contains(parsed.provider))
                {
                    attempted.Add(play.TrackReference);
                    if (await Route(play, parsed.provider!, parsed.externalId!) is { } song) return new(song);
                }
            }
            var latest = plays.FirstOrDefault();
            foreach (var identity in latest?.CanonicalRecordingId is { } recording ? routes.GetValueOrDefault(recording) ?? [] : [])
            {
                if (!attempted.Add($"ext-{identity.ProviderId}-song-{identity.ExternalId}")) continue;
                if (await Route(latest!, identity.ProviderId, identity.ExternalId) is { } song) return new(song);
            }
            return attempted.Count > 0 ? new((Song?)null) : null;

            async Task<Song?> Route(ListeningEventRecord play, string provider, string id)
            {
                var stored = await providers.GetStoredSongAsync(context, provider, id);
                if (stored == null)
                {
                    var kind = inputs.Kind;
                    inputs.Kind = GenerationInputs.PlaybackChecks;
                    HistoryRouteCheck? check;
                    try
                    {
                        check = await inputs.ReadAsync(context, async c =>
                        {
                            try { return new HistoryRouteCheck(await providers.GetSongAsync(c, provider, id)); }
                            catch (FileNotFoundException) { return new((Song?)null); }
                            catch (HttpRequestException error) when (error.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone)
                            { return new((Song?)null); }
                        }, token, provider);
                    }
                    finally { inputs.Kind = kind; }
                    if (check is { Song: null }) return null;
                    stored = check?.Song;
                    if (stored != null) inputs.FreshSongs.Add($"ext-{provider}-song-{id}");
                }
                // The saved reference is the selected playback route; cached canonical metadata can prefer a different source.
                var song = stored?.WithTasteReason(stored.TasteReason) ?? new Song
                {
                    Title = MusicLyrics.StripPresentationLabels(play.Title),
                    Artist = MusicLyrics.StripPresentationLabels(play.Artist),
                    Album = MusicLyrics.StripPresentationLabels(play.Album),
                    AlbumArtist = play.AlbumArtist,
                    Duration = (int?)(play.DurationMilliseconds / 1000),
                    Isrc = play.Isrc
                };
                song.Id = $"ext-{provider}-song-{id}";
                song.IsLocal = false;
                song.ExternalProvider = provider;
                song.ExternalId = id;
                song.CanonicalRecordingId = play.CanonicalRecordingId;
                return song;
            }
        }
        var matchScope = new TrackMatchScope(user.Id, user.BackendInstanceId, null, 2, 1, access.LibraryIds.ToHashSet());
        var historyMatcher = new TrackMatchDecisionEngine().WithLocalPriorityWindow(context.Policy?.LocalPreferenceWindow ?? .07,
            context.Policy?.Preferences.ExplicitFilter == "CleanOnly" ? ProviderExplicitContentPolicy.CleanOnly : ProviderExplicitContentPolicy.Allow);
        async Task<Song?> ResolveHistorySong(ListeningEventRecord play)
        {
            if (play.LibraryTrackId != null || string.IsNullOrWhiteSpace(play.Title) || string.IsNullOrWhiteSpace(play.Artist)) return null;
            var seed = new ExternalTrackMatchSnapshot(play.Id.ToString("D"), "history", play.TrackReference,
                MusicLyrics.StripPresentationLabels(play.Title), MusicLyrics.StripPresentationLabels(play.Artist),
                MusicLyrics.StripPresentationLabels(play.Album), play.AlbumArtist, play.DurationMilliseconds, play.Isrc,
                play.RecordingMusicBrainzId, FuzzyMatcher.HasCleanRecordingLabel(play.Title) ? false : null);
            static bool Accepted(TrackMatchDecision decision) => decision.State == TrackMatchReviewState.Accepted && !decision.RequiresReview;
            foreach (var provider in inputs.Playable)
            {
                var found = await inputs.ReadAsync(context, c => providers.SearchAutomaticProviderSongsAsync(c, provider, seed.Title + " " + seed.Artist, 3), token, provider);
                if (found?.IsSuccess == true)
                {
                    var matches = found.RequireValue();
                    inputs.Diagnostics.Count("history.search results", matches.Count);
                    var candidates = matches.Where(song => !song.IsLocal && !string.IsNullOrWhiteSpace(song.ExternalProvider) &&
                            !string.IsNullOrWhiteSpace(song.ExternalId)).DistinctBy(song => (song.ExternalProvider, song.ExternalId))
                        .Select(song => TrackMatchPlayableSearch.ToCandidate(song, matchScope)).ToArray();
                    var matching = candidates.Where(candidate => Accepted(historyMatcher.Decide(matchScope, seed, [candidate], providerPriority: inputs.Playable))).ToArray();
                    var matchingIds = matching.Select(candidate => candidate.LibraryTrackId).ToHashSet();
                    var decision = historyMatcher.Decide(matchScope, seed, candidates, providerPriority: inputs.Playable);
                    var selected = Accepted(decision) ? candidates.FirstOrDefault(candidate => candidate.LibraryTrackId == decision.SelectedLibraryTrackId) : null;
                    var song = selected == null ? null : matches.First(candidate => candidate.ExternalProvider == selected.ExternalProvider && candidate.ExternalId == selected.BackendItemId);
                    inputs.Diagnostics.Count("history.metadata mismatch", matches.Count(candidate => string.IsNullOrWhiteSpace(candidate.ExternalProvider) ||
                        string.IsNullOrWhiteSpace(candidate.ExternalId) || !matchingIds.Contains(TrackMatchPlayableSearch.CandidateId(candidate.ExternalProvider, candidate.ExternalId))));
                    if (song != null)
                    {
                        inputs.Diagnostics.Count("history.metadata matched");
                        if (!inputs.HistoryReferences.TryGetValue(song.Id, out var references)) inputs.HistoryReferences[song.Id] = references = [];
                        references.Add(play.TrackReference);
                        inputs.FreshSongs.Add(song.Id);
                        return song;
                    }
                    if (matching.Length > 0) inputs.Diagnostics.Count("history.metadata needs review");
                }
            }
            return null;
        }
        var recipeKind = inputs.Kind;
        var unresolvedRoutes = new List<(ListeningEventRecord Play, Song Song)>();
        inputs.Kind = GenerationInputs.HistoryLookups;
        var history = events.Where(item => item.State == ListeningEventState.Completed)
            .GroupBy(item => item.CanonicalRecordingId?.ToString("D") ?? item.TrackReference).ToArray();
        var rotation = history.OrderByDescending(group => group.Count(play => recent.Contains(play.Id)))
            .ThenByDescending(group => group.Count(play => play.ListenedAt >= clock.UtcNow.AddDays(-MixRecipes.RotationDays)))
            .ThenByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal).Take(PersonalMixRules.TopSongs);
        var lost = history.Where(group => group.Count() >= MixRecipes.LostPlays &&
                group.Max(play => play.ListenedAt ?? play.UpdatedAt) <= clock.UtcNow.AddMonths(-MixRecipes.LostMonths))
            .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal).Take(PersonalMixRules.TopSongs);
        var metadataProviders = providers.GetProviderOrder(allstarr.Core.Capabilities.ProviderCapabilityKind.Metadata) ?? [];
        foreach (var group in rotation.Concat(lost).DistinctBy(group => group.Key))
        {
            var play = group.OrderByDescending(item => item.ListenedAt).First();
            var existing = rows.Where(item => item.Song.Id == play.TrackReference || play.CanonicalRecordingId != null &&
                item.Song.CanonicalRecordingId == play.CanonicalRecordingId).Select(item => item.Song).ToArray();
            if (existing.Length > 0)
            {
                inputs.Diagnostics.Count("history.existing row");
                var imported = existing.Any(song => song.IsLocal) ? null : await ResolvedHistorySong(group);
                if (imported is { Song: null })
                {
                    inputs.Diagnostics.Count("history.unavailable resolved route");
                    rows.RemoveAll(row => existing.Contains(row.Song));
                    continue;
                }
                if (imported?.Song is { } resolvedSong && !existing.Any(song => song.Id == resolvedSong.Id)) rows.Add((resolvedSong, "history", null));
                if (group.Any(item => item.CanonicalRecordingId != null && item.SourceKind is "import" or "listenbrainz-api") &&
                    (imported != null || existing.Any(song => song.IsLocal || inputs.Playable.Contains(song.ExternalProvider))))
                    inputs.Diagnostics.Count("history.resolved from history import");
                if (imported != null) continue;
                unresolvedRoutes.AddRange(existing.Where(song => !song.IsLocal && !inputs.Playable.Contains(song.ExternalProvider)).Select(song => (play, song)));
                continue;
            }
            inputs.Diagnostics.Count("history.lookup");
            var parsed = LocalLibraryService.ParseExternalResource(play.TrackReference);
            var resolvedHistory = await ResolvedHistorySong(group);
            if (resolvedHistory is { Song: null }) { inputs.Diagnostics.Count("history.unavailable resolved route"); continue; }
            Song? song = resolvedHistory?.Song;
            if (song == null && play.CanonicalRecordingId is { } recording)
                song = await providers.GetStoredSongAsync(context, "allstarr", recording.ToString("D"));
            if (song == null && parsed.isExternal && parsed.type == "song")
            {
                song = await providers.GetStoredSongAsync(context, parsed.provider!, parsed.externalId!);
                if (song == null && (inputs.Playable.Contains(parsed.provider) || metadataProviders.Contains(parsed.provider)))
                {
                    song = await inputs.ReadAsync(context, c => providers.GetSongAsync(c, parsed.provider!, parsed.externalId!), token, parsed.provider!);
                    if (song != null) inputs.FreshSongs.Add(song.Id);
                }
            }
            if (song != null) inputs.Diagnostics.Count("history.stored or original route");
            if (song != null && group.Any(item => item.CanonicalRecordingId != null && item.SourceKind is "import" or "listenbrainz-api") &&
                (song.IsLocal || inputs.Playable.Contains(song.ExternalProvider))) inputs.Diagnostics.Count("history.resolved from history import");
            if (song == null) song = await ResolveHistorySong(play);
            else if (!song.IsLocal && !inputs.Playable.Contains(song.ExternalProvider)) unresolvedRoutes.Add((play, song));
            if (song != null) { song.CanonicalRecordingId ??= play.CanonicalRecordingId; rows.Add((song, "history", null)); }
            else inputs.Diagnostics.Count("history.unresolved");
        }
        inputs.Kind = recipeKind;
        var unavailableRoutes = new List<Song>();
        var ranked = (await RankCandidatesAsync(rows, context, user, events, hearts, token, inputs, unavailableRoutes: unavailableRoutes)).ToList();
        inputs.Kind = GenerationInputs.HistoryLookups;
        var resolved = new List<(Song Song, string Source, DateTimeOffset? Release)>();
        foreach (var (play, original) in unresolvedRoutes)
        {
            if (!unavailableRoutes.Contains(original)) continue;
            var song = await ResolveHistorySong(play);
            if (song == null) { inputs.Diagnostics.Count("history.unresolved playback"); continue; }
            song.CanonicalRecordingId ??= play.CanonicalRecordingId;
            resolved.Add((song, "history", null));
        }
        if (resolved.Count > 0) ranked.AddRange(await RankCandidatesAsync(resolved, context, user, events, hearts, token, inputs));
        inputs.Kind = recipeKind;
        return (ranked, null);
    }

    private async Task<IReadOnlyList<MixCandidate>> FillAsync(UserRecord user, IReadOnlyList<MixCandidate> own,
        ListeningEventRecord[] events, GenerationInputs inputs, string[] kinds, PersonalMixDocument document,
        IReadOnlyDictionary<string, allstarr.Core.AudioMuse.AudioMuseScores> features, CancellationToken token)
    {
        var inbox = kinds.Contains("release-radar") ? await releases.ReadAsync(user.Id, token) : null;
        inputs.InboxEntries = inbox?.Entries.Count ?? 0;
        foreach (var entry in inbox?.Entries ?? []) inputs.Diagnostics.Inbox.Add(new(entry.Sources.Select(source => source.Key).ToArray()));
        var followed = kinds.Contains("release-radar") ? await releases.ListAsync(user.Id, token) : [];
        inputs.ReleaseArtists = followed.Select(artist => artist.DisplayName).Concat(events.Where(play => play.State == ListeningEventState.Completed)
            .GroupBy(play => play.Artist, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() >= MixRecipes.FrequentPlays)
            .Select(group => group.Key).OfType<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        inputs.EligibleReleases = (inbox?.Entries ?? []).Where(entry => inputs.ReleaseArtists.Contains(entry.Artist))
            .SelectMany(entry => entry.Sources).Select(source => source.Key).ToHashSet(StringComparer.Ordinal);
        if (inputs.Playable.Length == 0)
        {
            foreach (var entry in inputs.Diagnostics.Inbox) entry.Count("No enabled playback provider");
            return own;
        }
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
        var shortKinds = kinds.Where(kind => kind == "release-radar" || Selected(kind).Count < MixRecipes.For(kind).Target)
            .OrderBy(kind => Selected(kind).Count >= (PersonalMixMoods.Kinds.Contains(kind) ? 25 : 1)).ToArray();
        foreach (var kind in shortKinds)
        {
            if (kind.StartsWith("daily-", StringComparison.Ordinal) && document.Mixes[kind].CoverTag == null) continue;
            inputs.Kind = kind;
            var recipe = MixRecipes.For(kind);
            bool NeedsSongs() => inputs.CanRead && Selected(kind).Count < recipe.Target;
            async Task<List<Song>> Search(string provider, string query, int count = 100)
            {
                var found = await inputs.ReadAsync(context, c => providers.SearchAutomaticProviderSongsAsync(c, provider, query, count), token, provider);
                if (found?.IsSuccess == true) return found.RequireValue().ToList();
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
                    inputs.Diagnostics.Count("fill.found");
                    inputs.Diagnostics.CurrentEntry?.Count("Tracks");
                    if (context.Policy?.Includes(song) == false)
                    { inputs.Diagnostics.Count("fill.excluded.Listening policy"); inputs.Diagnostics.CurrentEntry?.Count("Listening policy"); continue; }
                    if (kind != "release-radar" && selected.Count(row => string.Equals(row.Song.Artist, song.Artist, StringComparison.OrdinalIgnoreCase)) >= recipe.ArtistLimit) continue;
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
                    if (native != null) { inputs.Diagnostics.CurrentEntry?.Count("Existing recording"); continue; }
                    inputs.Filled.Add(song.Id);
                    rows.Add((song, source, release));
                    if (kind != "release-radar") recordings.Add(song);
                }
                if (kind == "release-radar")
                {
                    foreach (var row in rows)
                    {
                        var ranked = await RankCandidatesAsync([row], context, user, events, [], token, inputs);
                        if (ranked.Count == 0) continue;
                        all.Add(ranked[0]);
                        inputs.Diagnostics.CurrentEntry?.Count("Playable track");
                        inputs.Diagnostics.CurrentEntry?.Count("After first playable track", rows.Count - rows.IndexOf(row) - 1);
                        recordings.Add(ranked[0].Song);
                        break;
                    }
                }
                else if (rows.Count > 0) all.AddRange(await RankCandidatesAsync(rows, context, user, events, [], token, inputs));
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
                        var found = await inputs.ReadAsync(context, c => providers.SearchAsync(c, seed.Key, 0, 0, 5, provider), token, provider);
                        var artist = found?.Artists.FirstOrDefault(item => FuzzyMatcher.NormalizeForMatching(item.Name) == FuzzyMatcher.NormalizeForMatching(seed.Key));
                        if (artist?.ExternalId == null) continue;
                        if (kind != "discover-weekly" && NeedsSongs())
                            await Add((await Search(provider, artist.Name)).Where(song => string.Equals(song.Artist, artist.Name, StringComparison.OrdinalIgnoreCase)), provider);
                        foreach (var related in await inputs.ReadAsync(context, c => providers.GetSimilarArtistsAsync(c, provider, artist.ExternalId, 50), token, provider) ?? [])
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
                        foreach (var suggestion in await inputs.ReadAsync(context, c => similarTracks.FindAsync(c, seed.Key, track.Song.Title, 10, c.CancellationToken), token, "lastfm") ?? [])
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
                var chart = await inputs.ReadAsync(context, c => tagSources.TagTopTracksAsync(tag, c.CancellationToken, c), token, "lastfm") ?? [];
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
            inputs.InboxLimit = Math.Min(inbox!.Entries.Count, recipe.Target);
            inputs.Kind = GenerationInputs.InboxLookups;
            var entryOrder = inbox.Entries.Select((entry, index) => (Entry: entry, Counts: inputs.Diagnostics.Inbox[index]))
                .OrderByDescending(item => item.Entry.ReleaseDate, StringComparer.Ordinal).ThenBy(item => item.Entry.Id, StringComparer.Ordinal);
            var considered = 0;
            foreach (var (entry, counts) in entryOrder)
            {
                inputs.Diagnostics.CurrentEntry = counts;
                if (ReleaseDate(entry.ReleaseDate) is not { } release || release < clock.UtcNow.AddDays(-recipe.WindowDays))
                { counts.Count("Outside release window"); continue; }
                if (++considered > recipe.Target) { counts.Count("Entry limit"); continue; }
                if (!inputs.CanRead) { counts.Count("Service allowance"); continue; }
                var source = entry.Sources.Where(source => context.Policy?.IsAutomatic(source.ProviderId) != false)
                    .OrderByDescending(source => inputs.Playable.Contains(source.ProviderId)).FirstOrDefault();
                if (source == null) { counts.Count("No automatic source"); continue; }
                var found = await inputs.ReadAsync(context, c => providers.GetAlbumAsync(c, source.ProviderId, source.AlbumId), token, source.ProviderId);
                if (found == null) { counts.Count("Album unavailable"); continue; }
                if (found.Songs.Count == 0) counts.Count("Album has no tracks");
                await Add(found.Songs.OrderBy(song => song.Track ?? int.MaxValue).ThenBy(song => song.Id, StringComparer.Ordinal),
                    source.ProviderId, release: release, releaseKey: source.Key);
            }
            inputs.Diagnostics.CurrentEntry = null;
            inputs.Kind = kind;
            var inboxSources = inbox.Entries.SelectMany(entry => entry.Sources).Select(source => source.Key).ToHashSet(StringComparer.Ordinal);
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
                        {
                            var key = provider + ":" + (LocalLibraryService.ParseExternalResource(song.AlbumId).externalId ?? song.AlbumId);
                            if (!inboxSources.Contains(key)) await Add([song], provider, release: at, releaseKey: key);
                        }
                    if (NeedsSongs() && (artistSongs.Length == 0 || artistSongs.Any(song => ReleaseDate(song.ReleaseDate) == null)))
                    {
                        var artistId = seed.Id ?? artistSongs.SelectMany(song => song.ArtistIds)
                            .Select(LocalLibraryService.ParseExternalResource).Where(id => id.isExternal && id.type == "artist" && id.provider == provider)
                            .Select(id => id.externalId).FirstOrDefault();
                        if (artistId == null)
                        {
                            var artists = await inputs.ReadAsync(context, c => providers.SearchAsync(c, seed.Name, 0, 0, 5, provider), token, provider);
                            artistId = artists?.Artists.FirstOrDefault(artist => string.Equals(artist.Name, seed.Name, StringComparison.OrdinalIgnoreCase))?.ExternalId;
                        }
                        if (artistId != null)
                            foreach (var album in (await inputs.ReadAsync(context, c => providers.GetArtistAlbumsAsync(c, provider, artistId), token, provider) ?? [])
                                .OrderByDescending(album => album.ReleaseDate))
                            {
                                if (!NeedsSongs()) break;
                                if (ReleaseDate(album.ReleaseDate) is not { } at || at < clock.UtcNow.AddDays(-recipe.WindowDays)) continue;
                                var id = album.ExternalId ?? LocalLibraryService.ParseExternalResource(album.Id).externalId;
                                if (id == null || inboxSources.Contains(provider + ":" + id)) continue;
                                var release = await inputs.ReadAsync(context, c => providers.GetAlbumAsync(c, provider, id), token, provider);
                                await Add((release?.Songs ?? []).Where(song => string.Equals(song.Artist, seed.Name, StringComparison.OrdinalIgnoreCase))
                                    .OrderBy(song => song.Track ?? int.MaxValue).ThenBy(song => song.Id, StringComparer.Ordinal),
                                    provider, release: at, releaseKey: provider + ":" + id);
                            }
                    }
                    if (found.Count > 0 || seed.Id != null) break;
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
        ProtocolExecutionContext context, UserRecord user, ListeningEventRecord[] events, FavoriteViewItem[] hearts, CancellationToken token, GenerationInputs? inputs = null, bool recount = false,
        SeedMixDiagnostics? diagnostics = null, bool deferPlayback = false, ICollection<Song>? unavailableRoutes = null)
    {
        using var phase = diagnostics?.Phase("rank");
        if (inputs != null && !recount) inputs.Offered += rows.Count;
        inputs?.Diagnostics.Count(inputs.Stage + ".offered", rows.Count);
        var unique = new List<(Song Song, string Source, DateTimeOffset? Release)>();
        var recordings = new RecordingIndex();
        foreach (var row in rows.OrderByDescending(item => item.Release).ThenByDescending(item => item.Song.IsLocal).ThenBy(item => item.Song.Id, StringComparer.Ordinal))
            if (inputs != null) unique.Add(row);
            else if (unique.Count < 1000 && recordings.Find(row.Song) == null) { unique.Add(row); recordings.Add(row.Song); }
        var songs = unique.ToArray();
        foreach (var row in songs) diagnostics?.Count(row.Source, "unique");
        IReadOnlyDictionary<Guid, SharedSongTags> profiles;
        using (diagnostics?.Phase("rank-tags"))
            profiles = await tags.GetTags(songs.Select(item => item.Song.CanonicalRecordingId).OfType<Guid>().Distinct().ToArray(), token);
        (IReadOnlyDictionary<Song, TasteScore> scores, IReadOnlyList<TasteNudge> nudges) ranking;
        using (diagnostics?.Phase("rank-taste"))
            ranking = await taste.ScoreManyAsync(user.Id, songs.Select(item => item.Song).ToArray(), profiles, token, deferPlayback ? events : null);
        var (scores, nudges) = ranking;
        using var rules = diagnostics?.Phase("rank-rules");
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
        var result = new List<MixCandidate>();
        var failed = false;
        foreach (var row in songs)
        {
            var score = scores[row.Song];
            if (string.IsNullOrWhiteSpace(row.Song.Id) || string.IsNullOrWhiteSpace(row.Song.Title) || string.IsNullOrWhiteSpace(row.Song.Artist))
            { inputs?.Exclude("Missing song details", row.Song.Id); continue; }
            diagnostics?.Count(row.Source, "details");
            inputs?.Diagnostics.Count(inputs.Stage + ".details");
            if (context.Policy?.Includes(row.Song) == false) { inputs?.Exclude("Listening policy", row.Song.Id); continue; }
            diagnostics?.Count(row.Source, "policy");
            inputs?.Diagnostics.Count(inputs.Stage + ".policy");
            if (score.Blocked) { inputs?.Exclude("Never", row.Song.Id); continue; }
            diagnostics?.Count(row.Source, "never");
            inputs?.Diagnostics.Count(inputs.Stage + ".never");
            if (inputs != null && (recordings.Find(row.Song) != null || result.Count >= 1000))
            {
                inputs.Exclude(result.Count >= 1000 ? "Candidate limit" : "Duplicate recordings", row.Song.Id);
                continue;
            }
            inputs?.Diagnostics.Count(inputs.Stage + ".unique");
            try
            {
                using var playback = !deferPlayback ? diagnostics?.Phase("playback") : null;
                var playable = deferPlayback || row.Song.IsLocal || (inputs != null ? recount || inputs.Playable.Contains(row.Song.ExternalProvider)
                    : await providers.CanPlaySongAsync(context, row.Song));
                if (!playable && inputs != null)
                {
                    var kind = inputs.Kind;
                    inputs.Kind = kind == GenerationInputs.InboxLookups ? GenerationInputs.InboxPlaybackChecks : GenerationInputs.PlaybackChecks;
                    try { playable = await inputs.ReadAsync(context, async c => await providers.CanPlaySongAsync(c, row.Song) ? row.Song : null, token, row.Song.ExternalProvider ?? "unknown") != null; }
                    finally { inputs.Kind = kind; }
                }
                if (!playable) { inputs?.Exclude("No playable route", row.Song.Id); unavailableRoutes?.Add(row.Song); continue; }
            }
            catch (Exception) when (!token.IsCancellationRequested) { failed = true; inputs?.Exclude("No playable route", row.Song.Id); unavailableRoutes?.Add(row.Song); continue; }
            if (!deferPlayback || row.Song.IsLocal) diagnostics?.Count(row.Source, "playable");
            inputs?.Diagnostics.Count(inputs.Stage + ".playable");
            var matched = PersonalMixRules.IdentityKeys(row.Song).Concat(inputs?.HistoryReferences.GetValueOrDefault(row.Song.Id) ?? [])
                .SelectMany(key => listening.GetValueOrDefault(key) ?? [])
                .DistinctBy(play => play.Id).ToArray();
            var played = matched.Where(play => play.State == ListeningEventState.Completed).ToArray();
            var skipped = matched.Count(play => play.State == ListeningEventState.Skipped && play.UpdatedAt >= clock.UtcNow.AddDays(-PersonalMixRules.SkipDays) &&
                play.UpdatedAt <= clock.UtcNow && play.PositionTicks is >= 0 && (play.PositionTicks < TimeSpan.FromSeconds(30).Ticks ||
                play.DurationMilliseconds > 0 && play.PositionTicks / 10000d < play.DurationMilliseconds * .25)) >= 3;
            if (skipped) { inputs?.Exclude("Three early skips", row.Song.Id); continue; }
            diagnostics?.Count(row.Source, "skips");
            inputs?.Diagnostics.Count(inputs.Stage + ".skips");
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
            { inputs?.Exclude("Never", row.Song.Id); continue; }
            diagnostics?.Count(row.Source, "filters");
            inputs?.Diagnostics.Count(inputs.Stage + ".filters");
            var less = nudges.Any(nudge => nudge.Mode == "less" && (nudge.Kind == "song" && nudge.Name == PersonalMixRules.Key(row.Song) ||
                nudge.Kind == "artist" && string.Equals(nudge.Name, row.Song.Artist, StringComparison.OrdinalIgnoreCase) ||
                nudge.Kind == "tag" && features.ContainsKey(nudge.Name)));
            if (inputs != null) recordings.Add(row.Song);
            result.Add(new(inputs == null ? row.Song.WithTasteReason(row.Song.TasteReason ?? score.Reasons.FirstOrDefault()) : row.Song, features, played.Length, played.Count(play => recentIds.Contains(play.Id)),
                matched.Select(play => (DateTimeOffset?)(play.ListenedAt ?? play.UpdatedAt)).DefaultIfEmpty().Max(),
                false, row.Source, inputs == null ? score.Score : 0, row.Release, matched.Length > 0, hearts.Any(item => item.ItemId == row.Song.Id),
                played.Count(play => play.ListenedAt >= clock.UtcNow.AddDays(-MixRecipes.For("on-repeat").FallbackDays)), inputs?.Filled.Contains(row.Song.Id) == true,
                evidence.Seed, evidence.Tag, evidence.Source, evidence.Release, less, lastFmTags.Distinct().ToArray()));
        }
        if (inputs == null && result.Count == 0 && failed) throw new InvalidOperationException("Mix sources are unavailable. Try again later.");
        inputs?.Diagnostics.Count(inputs.Stage + ".ranked", result.Count);
        return result;
    }

    private DateTimeOffset? ReleaseDate(string? value) => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
        DateTimeStyles.None, out var date) && date <= DateOnly.FromDateTime(clock.UtcNow.UtcDateTime)
        ? new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null;
}

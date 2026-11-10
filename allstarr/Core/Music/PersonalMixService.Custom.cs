using allstarr.Core.Operations;
using allstarr.Core.Enrichment;
using allstarr.Core.ClientEvents;
using allstarr.Core.Favorites;
using allstarr.Core.Identity;
using allstarr.Core.Intelligence;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Services.Local;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Music;

public sealed record MixStartRequest(string SeedKind, string SeedId, int Length = 40);

public sealed partial class PersonalMixService
{
    private const int StartPlaybackBatch = 4, StartPlaybackMargin = 8;

    private sealed record CustomSeed(List<(Song Song, string Source, DateTimeOffset? Release)> Rows,
        string Name, Song? Song, string Artist, string[] Genres);

    private async Task<ProtocolExecutionContext> ContextAsync(UserRecord user, CancellationToken token) => new(
        Enum.Parse<ProtocolKind>(user.BackendType, true), user.BackendInstanceId, user.BackendPrincipalId,
        new AllstarrPrincipal(user.Id, user.BackendType, user.BackendInstanceId, user.BackendPrincipalId, user.DisplayName, user.IsAdmin),
        "mixes", clock.UtcNow.AddMinutes(5), token)
    { Policy = await policies.ResolveForUserAsync(user.Id, token), IsReadOnly = true };

    public async Task<PersonalMix> StartAsync(Guid owner, MixStartRequest request, CancellationToken token = default)
    {
        using var building = RequestTiming.Current?.Stage("build");
        if (request.SeedKind is not ("song" or "artist") || string.IsNullOrWhiteSpace(request.SeedId) || request.SeedId.Length > 200 || request.Length is < 25 or > 50)
            throw new ArgumentException("Choose a song or artist and 25–50 songs.");
        using var diagnostics = new SeedMixDiagnostics(logger);
        using (RequestTiming.Current?.Stage("lock"))
        using (diagnostics.Phase("queue")) await generations.WaitAsync(token);
        try
        {
            UserRecord user;
            ListeningEventRecord[] events;
            string history;
            await using var db = await factory.CreateDbContextAsync(token);
            using (diagnostics.Phase("setup"))
            {
                using (diagnostics.Phase("setup-user")) user = await UserAsync(db, owner, token);
                using (diagnostics.Phase("setup-history")) (events, history) = await HistoryAsync(db, user, token);
            }
            var (fetched, seedName) = await CandidatesAsync(db, user, events, token, request, diagnostics: diagnostics);
            var fetchedCount = fetched.Count(row => row.PlaybackQualified);
            var id = Guid.CreateVersion7();
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var (_, document, version) = await SnapshotAsync(owner, token);
                var revision = await InputRevisionAsync(db, owner, token);
                var candidates = await RevalidateAsync(user, fetched, token, diagnostics);
                var selected = PersonalMixRules.Select("custom", candidates, clock.UtcNow, [], request.Length, diagnostics);
                var qualifiedCount = candidates.Count(row => row.PlaybackQualified);
                var preview = new PersonalMix(id, "custom", "Your mix", "Songs like your seed", 0, clock.UtcNow,
                    new(selected.Count > 0, selected.Count > 0 ? "Ready" : "No eligible playable songs found."), false,
                    selected.Select(row => new PersonalMixItem(id, 0, SafeSong(row.Song), row.Source,
                        row.Source == "audiomuse" ? "Similar sound" : row.Song.TasteReason ?? (row.RelatedTo != null ? "Like your seed" : "To fill your mix"), row.Score)).ToArray())
                {
                    Evidence = new(MixRecipes.For("custom") with { Sentence = "Songs selected from " + seedName + ".", Target = request.Length },
                        events.Count(item => item.State == allstarr.Core.Intelligence.ListeningEventState.Completed && item.SourceKind == "protocol"),
                        events.Count(item => item.State == allstarr.Core.Intelligence.ListeningEventState.Completed && item.SourceKind != "protocol"),
                        null, null, null, 0, 0, fetchedCount, qualifiedCount, selected.Count,
                        selected.Count(item => item.Song.IsLocal), selected.Count(item => !item.Song.IsLocal), new Dictionary<string, int>
                        {
                            ["Artist and size limits"] = qualifiedCount - selected.Count,
                            ["Choices changed"] = fetchedCount - qualifiedCount
                        }.Where(item => item.Value > 0).ToDictionary(), null, null, null),
                    SeedName = seedName,
                    CoverTag = selected.SelectMany(row => row.Tags).GroupBy(tag => tag.Key)
                        .OrderByDescending(group => group.Sum(tag => tag.Value)).ThenBy(group => group.Key, StringComparer.Ordinal)
                        .Select(group => group.Key).FirstOrDefault()
                };
                foreach (var key in document.Previews.Where(item => item.Value.UpdatedAt < clock.UtcNow.AddMinutes(-30))
                    .Select(item => item.Key).ToArray()) document.Previews.Remove(key);
                if (document.Previews.Count >= 10) document.Previews.Remove(document.Previews.MinBy(item => item.Value.UpdatedAt).Key);
                document.Previews[id] = preview;
                using (diagnostics.Phase("save"))
                    if (await TrySaveAsync(user, document, version, revision, history, token)) { diagnostics.Complete(); return preview; }
            }
            throw new InvalidOperationException("Listener choices changed while building this mix. Start again.");
        }
        finally { generations.Release(); }
    }

    private async Task<(IReadOnlyList<MixCandidate> Candidates, string? SeedName)> StartCandidatesAsync(AllstarrDbContext db,
        ProtocolExecutionContext context, BackendLibraryAccess access, UserRecord user, ListeningEventRecord[] events,
        MixStartRequest request, CancellationToken token, SeedMixDiagnostics? diagnostics)
    {
        var query = LibraryTrackAccess.Query(db, context, access);
        diagnostics?.Add("library-access-succeeded", access.Succeeded ? 1 : 0);
        LibraryTrackRecord[] library;
        using (diagnostics?.Phase("seed"))
            library = await query.Where(item => item.BackendItemId == request.SeedId).Take(600).ToArrayAsync(token);
        diagnostics?.Add("seed-library-found", library.Length);
        var seed = await SeedRowsAsync(context, library, request, token, diagnostics);
        var artists = seed.Rows.Where(row => row.Source == "radio").SelectMany(row => row.Song.Artists.Append(row.Song.Artist))
            .Append(seed.Artist).Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim().ToLowerInvariant()).Distinct().ToArray();
        diagnostics?.Add("related-artist-names", artists.Length);
        var seedTags = seed.Genres.Select(genre => SharedTagNormalization.Canonical(genre, [])).OfType<string>().ToHashSet();
        if (seed.Rows.Any(row => row.Source == "audiomuse") && seed.Song?.CanonicalRecordingId is { } recording)
        {
            var profiles = await tags.GetTags([recording], token);
            if (profiles.TryGetValue(recording, out var profile)) seedTags.UnionWith(profile.Tags.Where(tag => tag.Score > 0).Select(tag => tag.Tag));
        }
        LibraryTrackRecord[] related;
        using (diagnostics?.Phase("related-library"))
            related = await query.Where(item => artists.Contains(item.Artist.Trim().ToLower()))
                .OrderBy(item => item.BackendItemId).Take(600).ToArrayAsync(token);
        diagnostics?.Count("library", "found", related.Length);
        seed.Rows.AddRange(related.Select(track => (LibrarySong(track), "library", (DateTimeOffset?)null)));
        var radioRecordings = new RecordingIndex();
        foreach (var row in seed.Rows.Where(row => row.Source == "radio")) radioRecordings.Add(row.Song);
        var rows = WithoutSeed(seed.Rows);
        var relatedRows = new List<(Song Song, string Source, DateTimeOffset? Release)>();
        var relatedRecordings = new RecordingIndex();
        foreach (var row in rows.Where(row => row.Source != "audiomuse")
            .OrderByDescending(row => SeedArtist(row.Song)).ThenByDescending(row => row.Song.IsLocal))
            if (relatedRows.Count < MusicMixService.SeedCandidateLimit &&
                (!SeedArtist(row.Song) || relatedRows.Count(item => SeedArtist(item.Song)) < MusicMixService.SeedArtistCandidateLimit) && relatedRecordings.Find(row.Song) == null)
            { relatedRows.Add(row); relatedRecordings.Add(row.Song); }
        diagnostics?.Add("related-capped", relatedRows.Count);
        var relatedCount = relatedRows.Count;
        var seedArtistCount = relatedRows.Count(row => SeedArtist(row.Song));
        var candidates = (await RankCandidatesAsync(rows.Where(row => row.Source == "audiomuse").Concat(relatedRows).ToList(),
            context, user, events, [], token, diagnostics: diagnostics, deferPlayback: true)).Select(Relationship).ToList();
        var playbackChecks = new Dictionary<string, (bool Playable, bool Failed)>(StringComparer.Ordinal);
        var qualified = await QualifyAsync();
        var selected = PersonalMixRules.Select("custom", qualified, clock.UtcNow, [], request.Length);
        if (selected.Count == request.Length && selected.Count(row => row.Source != "audiomuse") >= Math.Ceiling(request.Length * .6))
            return (qualified, seed.Name);

        FavoriteViewItem[] hearts;
        using (diagnostics?.Phase("hearts"))
            hearts = (await new FavoriteViewReader(factory).ReadAsync(context, token)).Where(item => item.Display.Kind == "song").Take(300).ToArray();
        var heartRows = hearts.Select(item => (Song: item.Song(), Source: "heart", Release: (DateTimeOffset?)null)).ToArray();
        await AddAsync(heartRows.Where(row => RelatedArtist(row.Song) || radioRecordings.Find(row.Song) != null).ToList());
        if (!qualified.Any(row => row.PlaybackQualified && (row.Source == "audiomuse" || row.RelatedTo != null))) return ([], seed.Name);
        if (PersonalMixRules.Select("custom", qualified, clock.UtcNow, [], request.Length).Count == request.Length) return (qualified, seed.Name);
        await AddAsync(heartRows.Where(row => !RelatedArtist(row.Song) && radioRecordings.Find(row.Song) == null).ToList());
        if (PersonalMixRules.Select("custom", qualified, clock.UtcNow, [], request.Length).Count < request.Length)
        {
            var known = events.Select(item => item.LibraryTrackId).OfType<Guid>().Distinct().ToArray();
            LibraryTrackRecord[] fallback;
            using (diagnostics?.Phase("fallback-library"))
                fallback = await query.OrderByDescending(item => known.Contains(item.Id)).ThenBy(item => item.BackendItemId).Take(600).ToArrayAsync(token);
            await AddAsync(fallback.Select(track => (LibrarySong(track), "library", (DateTimeOffset?)null)).ToList());
        }
        return (qualified, seed.Name);

        List<(Song Song, string Source, DateTimeOffset? Release)> WithoutSeed(List<(Song Song, string Source, DateTimeOffset? Release)> rows)
        {
            var remaining = rows.Where(row => seed.Song == null || !MusicMixService.SameRadioRecording(seed.Song, row.Song)).ToList();
            foreach (var row in remaining) diagnostics?.Count(row.Source, "without-seed");
            return remaining;
        }

        bool RelatedArtist(Song song) => song.Artists.Append(song.Artist).Any(name => artists.Contains(name.Trim().ToLowerInvariant()));

        bool SeedArtist(Song song) => song.Artists.Append(song.Artist).Any(name => string.Equals(name.Trim(), seed.Artist, StringComparison.OrdinalIgnoreCase));

        MixCandidate Relationship(MixCandidate row)
        {
            var fromRadio = radioRecordings.Find(row.Song) != null;
            if (fromRadio && row.Source == "audiomuse") diagnostics?.Add("radio-overlaps", 1);
            var related = row with
            {
                Source = fromRadio && row.Source == "audiomuse" ? "radio" : row.Source,
                RelatedTo = fromRadio || RelatedArtist(row.Song) || row.Source == "audiomuse" && row.Tags.Keys.Any(seedTags.Contains) ? seed.Artist : null
            };
            if (related.RelatedTo != null) diagnostics?.Count(related.Source, "evidence");
            return related;
        }

        async Task AddAsync(List<(Song Song, string Source, DateTimeOffset? Release)> rows)
        {
            foreach (var row in rows) diagnostics?.Count(row.Source, "found");
            var recordings = new RecordingIndex();
            foreach (var row in candidates.Where(row => row.Song.IsLocal || !playbackChecks.TryGetValue(row.Song.Id, out var check) || check.Playable)) recordings.Add(row.Song);
            var additional = new List<(Song Song, string Source, DateTimeOffset? Release)>();
            foreach (var row in WithoutSeed(rows))
            {
                if (candidates.Count + additional.Count >= 1000) break;
                if (recordings.Find(row.Song) != null) continue;
                if (!row.Song.IsLocal && playbackChecks.TryGetValue(row.Song.Id, out var previous) && !previous.Playable) continue;
                var isRelated = RelatedArtist(row.Song) || radioRecordings.Find(row.Song) != null;
                var isSeedArtist = SeedArtist(row.Song);
                var newRecording = relatedRecordings.Find(row.Song) == null;
                if (newRecording && (isRelated && relatedCount >= MusicMixService.SeedCandidateLimit || isSeedArtist && seedArtistCount >= MusicMixService.SeedArtistCandidateLimit)) continue;
                additional.Add(row);
                recordings.Add(row.Song);
                if (isRelated && newRecording) { relatedCount++; relatedRecordings.Add(row.Song); diagnostics?.Add("related-capped", 1); }
                if (isSeedArtist && newRecording) seedArtistCount++;
            }
            if (additional.Count == 0) return;
            candidates.AddRange((await RankCandidatesAsync(additional, context, user, events, hearts, token, diagnostics: diagnostics, deferPlayback: true)).Select(Relationship));
            qualified = await QualifyAsync();
        }

        async Task<IReadOnlyList<MixCandidate>> QualifyAsync()
        {
            while (true)
            {
                var available = candidates.Where(row => row.Song.IsLocal || !playbackChecks.TryGetValue(row.Song.Id, out var check) || check.Playable).ToArray();
                var next = PersonalMixRules.Select("custom", available, clock.UtcNow, [], request.Length,
                    selectionLimit: request.Length + StartPlaybackMargin, includeUnqualified: true)
                    .Where(row => !row.Song.IsLocal && !playbackChecks.ContainsKey(row.Song.Id))
                    .Take(Math.Min(StartPlaybackBatch, Math.Max(0, request.Length + StartPlaybackMargin - playbackChecks.Count))).ToArray();
                if (next.Length == 0) break;
                var checks = await CheckStartPlaybackAsync(next.Select(row => row.Song).ToArray(), context, token, diagnostics);
                foreach (var row in next)
                {
                    var check = checks[row.Song];
                    playbackChecks[row.Song.Id] = check;
                    if (check.Playable) diagnostics?.Count(row.Source, "playable");
                }
            }
            var playable = candidates.Where(row => row.Song.IsLocal || !playbackChecks.TryGetValue(row.Song.Id, out var check) || check.Playable)
                .Select(row => row with { PlaybackQualified = row.Song.IsLocal || playbackChecks.TryGetValue(row.Song.Id, out var check) && check.Playable }).ToArray();
            if (!playable.Any(row => row.PlaybackQualified) && playbackChecks.Values.Any(check => check.Failed))
                throw new InvalidOperationException("Mix sources are unavailable. Try again later.");
            return playable;
        }
    }

    private async Task<IReadOnlyDictionary<Song, (bool Playable, bool Failed)>> CheckStartPlaybackAsync(IReadOnlyList<Song> songs,
        ProtocolExecutionContext context, CancellationToken token, SeedMixDiagnostics? diagnostics)
    {
        using var phase = diagnostics?.Phase("playback");
        using var slots = new SemaphoreSlim(StartPlaybackBatch);
        var results = new System.Collections.Concurrent.ConcurrentDictionary<Song, (bool Playable, bool Failed)>();
        await Task.WhenAll(songs.Select(async song =>
        {
            if (song.IsLocal) { results[song] = (true, false); return; }
            await slots.WaitAsync(token);
            try
            {
                diagnostics?.Add("playback-checks", 1);
                results[song] = (await providers.CanPlaySongAsync(context, song).WaitAsync(token), false);
            }
            catch (Exception) when (!token.IsCancellationRequested) { results[song] = (false, true); }
            finally { slots.Release(); }
        }));
        return results;
    }

    private async Task<CustomSeed> SeedRowsAsync(ProtocolExecutionContext context,
        LibraryTrackRecord[] library, MixStartRequest request, CancellationToken token, SeedMixDiagnostics? diagnostics)
    {
        var parsed = LocalLibraryService.ParseExternalResource(request.SeedId);
        Song? seed = null;
        Artist? artist = null;
        // The library keeps a copy of a shared song for each listener, so the seed can match several rows; the listener's own wins.
        var local = library.Where(track => track.BackendItemId == request.SeedId)
            .OrderByDescending(track => track.OwnerUserId == context.Actor?.EffectiveUserId).ThenBy(track => track.Id).FirstOrDefault();
        using (diagnostics?.Phase("seed"))
        {
            if (request.SeedKind == "song")
            {
                if (parsed.isExternal && parsed.type == "song") seed = await providers.GetSongAsync(context, parsed.provider!, parsed.externalId!);
                else if (local != null) seed = LibrarySong(local);
                if (seed == null) throw new KeyNotFoundException();
            }
            else
            {
                if (!parsed.isExternal || parsed.type != "artist") throw new ArgumentException("Choose an artist from search.");
                artist = await providers.GetArtistAsync(context, parsed.provider!, parsed.externalId!) ?? throw new KeyNotFoundException();
            }
        }
        var rows = new List<(Song Song, string Source, DateTimeOffset? Release)>();
        using (diagnostics?.Phase("sources"))
        {
            var sound = SoundAsync();
            var suggestions = RadioAsync();
            await Task.WhenAll(sound, suggestions);
            rows.AddRange((await sound).Select(track => (LibrarySong(track), "audiomuse", (DateTimeOffset?)null)));
            rows.AddRange((await suggestions).Select(song => (song, "radio", (DateTimeOffset?)null)));
        }
        foreach (var row in rows) diagnostics?.Count(row.Source, "found");
        return new(rows.Where(row => seed == null || !MusicMixService.SameRadioRecording(seed, row.Song)).ToList(),
            seed?.Title ?? artist!.Name, seed, seed?.Artist ?? artist!.Name, seed?.Genres.Append(seed.Genre).OfType<string>().ToArray() ?? artist!.Genres.ToArray());

        async Task<LibraryTrackRecord[]> SoundAsync()
        {
            using var phase = diagnostics?.Phase("sound");
            var neighbours = local != null && audioMuse != null ? await audioMuse.MixSimilarAsync(context, local.BackendItemId, token) : [];
            diagnostics?.Add("sound-neighbours", neighbours.Length);
            return neighbours;
        }

        async Task<IReadOnlyList<Song>> RadioAsync()
        {
            using var phase = diagnostics?.Phase("radio");
            return await radio.SeedCandidatesAsync(context, seed, artist, parsed.isExternal ? parsed.provider : null,
                parsed.isExternal ? parsed.externalId : null, token, diagnostics, capCandidates: true);
        }
    }

    private static Song LibrarySong(LibraryTrackRecord track) => new()
    {
        Id = track.BackendItemId,
        CanonicalRecordingId = track.CanonicalRecordingId,
        Title = track.Title,
        Artist = track.Artist,
        Album = track.Album ?? "",
        Duration = (int?)(track.DurationMilliseconds / 1000),
        Isrc = track.Isrc,
        IsLocal = true
    };

    public async Task<PersonalMix> SaveCustomAsync(Guid owner, Guid previewId, string name, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || name.Any(char.IsControl)) throw new ArgumentException("Supply a name of 1–200 characters.");
        var (user, captured, _) = await SnapshotAsync(owner, token);
        var key = "custom:" + previewId.ToString("N");
        if (captured.Mixes.TryGetValue(key, out var saved))
            return (await VisibleAsync(owner, new() { Mixes = new() { [key] = saved } }, token)).Single();
        if (!captured.Previews.TryGetValue(previewId, out var preview)) throw new KeyNotFoundException();
        if (preview.UpdatedAt < clock.UtcNow.AddMinutes(-30)) throw new ArgumentException("Preview expired. Start another mix.");
        await using var db = await factory.CreateDbContextAsync(token);
        var (_, history) = await HistoryAsync(db, user, token);
        var context = await ContextAsync(user, token);
        var candidates = preview.Songs.Select(item => new MixCandidate(item.Song, new Dictionary<string, double>(),
            0, 0, null, false, item.Source, item.Score)).ToArray();
        var playable = new List<MixCandidate>();
        foreach (var item in await RevalidateAsync(user, candidates, token))
            if (item.Song.IsLocal || await providers.CanPlaySongAsync(context, item.Song)) playable.Add(item);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var (_, document, version) = await SnapshotAsync(owner, token);
            if (document.Mixes.TryGetValue(key, out saved))
                return (await VisibleAsync(owner, new() { Mixes = new() { [key] = saved } }, token)).Single();
            if (!document.Previews.ContainsKey(previewId)) throw new KeyNotFoundException();
            if (preview.UpdatedAt < clock.UtcNow.AddMinutes(-30)) throw new ArgumentException("Preview expired. Start another mix.");
            var revision = await InputRevisionAsync(db, owner, token);
            var eligible = (await RevalidateAsync(user, playable, token)).Select(item => item.Song.Id).ToHashSet(StringComparer.Ordinal);
            var items = preview.Songs.Where(item => eligible.Contains(item.Song.Id)).Select(item => item with { Generation = 1 }).ToArray();
            if (items.Length == 0) throw new ArgumentException("No eligible playable songs remain. Start another mix.");
            var evidence = preview.Evidence;
            if (evidence != null)
            {
                var excluded = evidence.Excluded.ToDictionary();
                if (preview.Songs.Count > items.Length) excluded["Changed before saving"] = preview.Songs.Count - items.Length;
                evidence = evidence with
                {
                    Shown = items.Length,
                    FromMusic = items.Count(item => item.Song.IsLocal),
                    FromServices = items.Count(item => !item.Song.IsLocal),
                    Excluded = excluded
                };
            }
            saved = preview with { Evidence = evidence, Name = name.Trim(), Generation = 1, UpdatedAt = clock.UtcNow, Readiness = new(true, "Ready"), Added = true, Songs = items };
            document.Mixes[key] = saved;
            if (await TrySaveAsync(user, document, version, revision, history, token))
            {
                clientEvents?.Publish(owner, ClientEvent.Mix(saved.Id));
                return saved;
            }
        }
        throw new InvalidOperationException("Listener choices changed while saving this mix. Save again.");
    }

    public async Task DeleteCustomAsync(Guid owner, Guid id, CancellationToken token = default)
    {
        var write = await writes.AcquireAsync(owner.ToString(), token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var user = await UserAsync(db, owner, token);
            var document = await LoadAsync(db, user, token);
            var row = document.Mixes.SingleOrDefault(item => item.Value.Id == id && item.Value.Kind == "custom");
            if (row.Key == null) throw new KeyNotFoundException();
            document.Mixes.Remove(row.Key);
            document.Previews.Remove(id);
            await SaveAsync(db, user, document, token);
            clientEvents?.Publish(owner, ClientEvent.Mix(id));
        }
        finally { await write.DisposeAsync(); }
    }
}

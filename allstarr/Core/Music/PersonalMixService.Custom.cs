using allstarr.Core.Enrichment;
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
    private sealed record CustomSeed(List<(Song Song, string Source, DateTimeOffset? Release)> Rows,
        string Name, Song? Song, string Artist, string[] Genres);

    private async Task<ProtocolExecutionContext> ContextAsync(UserRecord user, CancellationToken token) => new(
        Enum.Parse<ProtocolKind>(user.BackendType, true), user.BackendInstanceId, user.BackendPrincipalId,
        new AllstarrPrincipal(user.Id, user.BackendType, user.BackendInstanceId, user.BackendPrincipalId, user.DisplayName, user.IsAdmin),
        "mixes", clock.UtcNow.AddMinutes(5), token)
    { Policy = await policies.ResolveForUserAsync(user.Id, token), IsReadOnly = true };

    public async Task<PersonalMix> StartAsync(Guid owner, MixStartRequest request, CancellationToken token = default)
    {
        if (request.SeedKind is not ("song" or "artist") || string.IsNullOrWhiteSpace(request.SeedId) || request.SeedId.Length > 200 || request.Length is < 25 or > 50)
            throw new ArgumentException("Choose a song or artist and 25–50 songs.");
        await generations.WaitAsync(token);
        try
        {
            var (user, _, _) = await SnapshotAsync(owner, token);
            await using var db = await factory.CreateDbContextAsync(token);
            var (events, history) = await HistoryAsync(db, user, token);
            var (fetched, seedName) = await CandidatesAsync(db, user, events, token, request);
            var id = Guid.CreateVersion7();
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var (_, document, version) = await SnapshotAsync(owner, token);
                var revision = await InputRevisionAsync(db, owner, token);
                var candidates = await RevalidateAsync(user, fetched, token);
                var selected = PersonalMixRules.Select("custom", candidates, clock.UtcNow, [], request.Length);
                var preview = new PersonalMix(id, "custom", "Your mix", "Songs like your seed", 0, clock.UtcNow,
                    new(selected.Count > 0, selected.Count > 0 ? "Ready" : "No eligible playable songs found."), false,
                    selected.Select(row => new PersonalMixItem(id, 0, SafeSong(row.Song), row.Source,
                        row.Source == "audiomuse" ? "Similar sound" : row.Song.TasteReason ?? (row.RelatedTo != null ? "Like your seed" : "To fill your mix"), row.Score)).ToArray())
                {
                    Evidence = new(MixRecipes.For("custom") with { Sentence = "Songs selected from " + seedName + ".", Target = request.Length },
                        events.Count(item => item.State == allstarr.Core.Intelligence.ListeningEventState.Completed && item.SourceKind == "protocol"),
                        events.Count(item => item.State == allstarr.Core.Intelligence.ListeningEventState.Completed && item.SourceKind != "protocol"),
                        null, null, null, 0, 0, fetched.Count, candidates.Count, selected.Count,
                        selected.Count(item => item.Song.IsLocal), selected.Count(item => !item.Song.IsLocal), new Dictionary<string, int>
                        {
                            ["Artist and size limits"] = candidates.Count - selected.Count,
                            ["Choices changed"] = fetched.Count - candidates.Count
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
                if (await TrySaveAsync(user, document, version, revision, history, token)) return preview;
            }
            throw new InvalidOperationException("Listener choices changed while building this mix. Start again.");
        }
        finally { generations.Release(); }
    }

    private async Task<(IReadOnlyList<MixCandidate> Candidates, string? SeedName)> StartCandidatesAsync(AllstarrDbContext db,
        ProtocolExecutionContext context, BackendLibraryAccess access, UserRecord user, ListeningEventRecord[] events,
        MixStartRequest request, CancellationToken token)
    {
        var query = LibraryTrackAccess.Query(db, context, access);
        var library = await query.Where(item => item.BackendItemId == request.SeedId).Take(600).ToArrayAsync(token);
        var seed = await SeedRowsAsync(context, library, request, token);
        var artists = seed.Rows.Where(row => row.Source == "radio").SelectMany(row => row.Song.Artists.Append(row.Song.Artist))
            .Append(seed.Artist).Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim().ToLowerInvariant()).Distinct().ToArray();
        var seedTags = seed.Genres.Select(genre => SharedTagNormalization.Canonical(genre, [])).OfType<string>().ToHashSet();
        if (seed.Song?.CanonicalRecordingId is { } recording)
        {
            var profiles = await tags.GetTags([recording], token);
            if (profiles.TryGetValue(recording, out var profile)) seedTags.UnionWith(profile.Tags.Where(tag => tag.Score > 0).Select(tag => tag.Tag));
        }
        var related = await query.Where(item => artists.Contains(item.Artist.Trim().ToLower()))
            .OrderBy(item => item.BackendItemId).Take(600).ToArrayAsync(token);
        seed.Rows.AddRange(related.Select(track => (LibrarySong(track), "library", (DateTimeOffset?)null)));
        var radioRecordings = new RecordingIndex();
        foreach (var row in seed.Rows.Where(row => row.Source == "radio")) radioRecordings.Add(row.Song);
        var candidates = (await RankCandidatesAsync(WithoutSeed(seed.Rows), context, user, events, [], token)).Select(Relationship).ToList();
        var selected = PersonalMixRules.Select("custom", candidates, clock.UtcNow, [], request.Length);
        if (selected.Count == request.Length && selected.Count(row => row.Source != "audiomuse") >= Math.Ceiling(request.Length * .6))
            return (candidates, seed.Name);

        var hearts = (await new FavoriteViewReader(factory).ReadAsync(context, token)).Where(item => item.Display.Kind == "song").Take(300).ToArray();
        var heartRows = hearts.Select(item => (Song: item.Song(), Source: "heart", Release: (DateTimeOffset?)null)).ToArray();
        await AddAsync(heartRows.Where(row => RelatedArtist(row.Song) || radioRecordings.Find(row.Song) != null).ToList());
        if (!candidates.Any(row => row.Source == "audiomuse" || row.RelatedTo != null)) return ([], seed.Name);
        if (PersonalMixRules.Select("custom", candidates, clock.UtcNow, [], request.Length).Count == request.Length) return (candidates, seed.Name);
        await AddAsync(heartRows.Where(row => !RelatedArtist(row.Song) && radioRecordings.Find(row.Song) == null).ToList());
        if (PersonalMixRules.Select("custom", candidates, clock.UtcNow, [], request.Length).Count < request.Length)
        {
            var known = events.Select(item => item.LibraryTrackId).OfType<Guid>().Distinct().ToArray();
            var fallback = await query.OrderByDescending(item => known.Contains(item.Id)).ThenBy(item => item.BackendItemId).Take(600).ToArrayAsync(token);
            await AddAsync(fallback.Select(track => (LibrarySong(track), "library", (DateTimeOffset?)null)).ToList());
        }
        return (candidates, seed.Name);

        List<(Song Song, string Source, DateTimeOffset? Release)> WithoutSeed(List<(Song Song, string Source, DateTimeOffset? Release)> rows) =>
            rows.Where(row => seed.Song == null || !MusicMixService.SameRadioRecording(seed.Song, row.Song)).ToList();

        bool RelatedArtist(Song song) => song.Artists.Append(song.Artist).Any(name => artists.Contains(name.Trim().ToLowerInvariant()));

        MixCandidate Relationship(MixCandidate row)
        {
            var fromRadio = radioRecordings.Find(row.Song) != null;
            return row with
            {
                Source = fromRadio && row.Source == "audiomuse" ? "radio" : row.Source,
                RelatedTo = fromRadio || RelatedArtist(row.Song) || row.Source == "audiomuse" && row.Tags.Keys.Any(seedTags.Contains) ? seed.Artist : null
            };
        }

        async Task AddAsync(List<(Song Song, string Source, DateTimeOffset? Release)> rows)
        {
            var recordings = new RecordingIndex();
            foreach (var row in candidates) recordings.Add(row.Song);
            var additional = WithoutSeed(rows).Where(row => recordings.Find(row.Song) == null).Take(Math.Max(0, 1000 - candidates.Count)).ToList();
            if (additional.Count == 0) return;
            candidates.AddRange((await RankCandidatesAsync(additional, context, user, events, hearts, token)).Select(Relationship));
        }
    }

    private async Task<CustomSeed> SeedRowsAsync(ProtocolExecutionContext context,
        LibraryTrackRecord[] library, MixStartRequest request, CancellationToken token)
    {
        var parsed = LocalLibraryService.ParseExternalResource(request.SeedId);
        Song? seed = null;
        Artist? artist = null;
        // The library keeps a copy of a shared song for each listener, so the seed can match several rows; the listener's own wins.
        var local = library.Where(track => track.BackendItemId == request.SeedId)
            .OrderByDescending(track => track.OwnerUserId == context.Actor?.EffectiveUserId).ThenBy(track => track.Id).FirstOrDefault();
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
        var rows = new List<(Song Song, string Source, DateTimeOffset? Release)>();
        var sound = local != null && audioMuse != null ? audioMuse.MixSimilarAsync(context, local.BackendItemId, token) : Task.FromResult<LibraryTrackRecord[]>([]);
        var suggestions = radio.SeedCandidatesAsync(context, seed, artist, parsed.isExternal ? parsed.provider : null,
            parsed.isExternal ? parsed.externalId : null, token);
        await Task.WhenAll(sound, suggestions);
        rows.AddRange((await sound).Select(track => (LibrarySong(track), "audiomuse", (DateTimeOffset?)null)));
        rows.AddRange((await suggestions).Select(song => (song, "radio", (DateTimeOffset?)null)));
        return new(rows.Where(row => seed == null || !MusicMixService.SameRadioRecording(seed, row.Song)).ToList(),
            seed?.Title ?? artist!.Name, seed, seed?.Artist ?? artist!.Name, seed?.Genres.Append(seed.Genre).OfType<string>().ToArray() ?? artist!.Genres.ToArray());
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
            if (await TrySaveAsync(user, document, version, revision, history, token)) return saved;
        }
        throw new InvalidOperationException("Listener choices changed while saving this mix. Save again.");
    }

    public async Task DeleteCustomAsync(Guid owner, Guid id, CancellationToken token = default)
    {
        await writes.WaitAsync(token);
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
        }
        finally { writes.Release(); }
    }
}

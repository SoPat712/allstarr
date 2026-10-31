using allstarr.Core.Identity;
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
                        row.Source == "audiomuse" ? "Similar sound" : row.Song.TasteReason ?? "Like your seed", row.Score)).ToArray())
                {
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

    private async Task<(List<(Song Song, string Source, DateTimeOffset? Release)> Rows, string Name)> SeedRowsAsync(ProtocolExecutionContext context,
        LibraryTrackRecord[] library, MixStartRequest request, CancellationToken token)
    {
        var parsed = LocalLibraryService.ParseExternalResource(request.SeedId);
        Song? seed = null;
        Artist? artist = null;
        var local = library.SingleOrDefault(track => track.BackendItemId == request.SeedId);
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
        if (local != null && audioMuse != null)
            rows.AddRange((await audioMuse.MixSimilarAsync(context, local.BackendItemId, token)).Select(track => (LibrarySong(track), "audiomuse", (DateTimeOffset?)null)));
        rows.AddRange((await radio.SeedCandidatesAsync(context, seed, artist, parsed.isExternal ? parsed.provider : null,
            parsed.isExternal ? parsed.externalId : null, token)).Select(song => (song, "radio", (DateTimeOffset?)null)));
        return (rows.Where(row => seed == null || !MusicMixService.SameRadioRecording(seed, row.Song)).ToList(),
            seed?.Title ?? artist!.Name);
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
            saved = preview with { Name = name.Trim(), Generation = 1, UpdatedAt = clock.UtcNow, Readiness = new(true, "Ready"), Added = true, Songs = items };
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

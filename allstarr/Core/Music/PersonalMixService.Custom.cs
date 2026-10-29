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
        await writes.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var user = await UserAsync(db, owner, token);
            var document = await LoadAsync(db, user, token);
            var events = await Events(db, user).Where(item => item.UpdatedAt <= clock.UtcNow &&
                (item.ListenedAt == null || item.ListenedAt <= clock.UtcNow)).ToArrayAsync(token);
            var candidates = await CandidatesAsync(db, user, events, token, request);
            var selected = PersonalMixRules.Select("custom", candidates, clock.UtcNow, [], request.Length);
            var id = Guid.CreateVersion7();
            var preview = new PersonalMix(id, "custom", "Your mix", "Songs like your seed", 0, clock.UtcNow,
                new(selected.Count > 0, selected.Count > 0 ? "Ready" : "No eligible playable songs found."), false,
                selected.Select(row => new PersonalMixItem(id, 0, SafeSong(row.Song), row.Source,
                    row.Source == "audiomuse" ? "Similar sound" : row.Song.TasteReason ?? "Like your seed", row.Score)).ToArray());
            foreach (var key in document.Previews.Where(item => item.Value.UpdatedAt < clock.UtcNow.AddMinutes(-30))
                .Select(item => item.Key).ToArray()) document.Previews.Remove(key);
            if (document.Previews.Count >= 10) document.Previews.Remove(document.Previews.MinBy(item => item.Value.UpdatedAt).Key);
            document.Previews[id] = preview;
            await SaveAsync(db, user, document, token);
            return preview;
        }
        finally { writes.Release(); }
    }

    private async Task<List<(Song Song, string Source, DateTimeOffset? Release)>> SeedRowsAsync(ProtocolExecutionContext context,
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
        return rows.Where(row => seed == null || !MusicMixService.SameRadioRecording(seed, row.Song)).ToList();
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
        await writes.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var user = await UserAsync(db, owner, token);
            var document = await LoadAsync(db, user, token);
            var key = "custom:" + previewId.ToString("N");
            if (document.Mixes.TryGetValue(key, out var saved)) return (await VisibleAsync(owner, document, token)).Single(item => item.Id == saved.Id);
            if (!document.Previews.TryGetValue(previewId, out var preview)) throw new KeyNotFoundException();
            if (preview.UpdatedAt < clock.UtcNow.AddMinutes(-30)) throw new ArgumentException("Preview expired. Start another mix.");
            var context = await ContextAsync(user, token);
            var allowed = await libraryAccess.ResolveAsync(context, token);
            var ids = preview.Songs.Where(item => item.Song.IsLocal).Select(item => item.Song.Id).ToArray();
            var local = await LibraryTrackAccess.Query(db, context, allowed).Where(item => ids.Contains(item.BackendItemId))
                .Select(item => item.BackendItemId).ToArrayAsync(token);
            var visible = (await VisibleAsync(owner, new() { Mixes = new() { [key] = preview } }, token)).Single();
            var items = new List<PersonalMixItem>();
            foreach (var item in visible.Songs)
                if (item.Song.IsLocal ? local.Contains(item.Song.Id) : await providers.CanPlaySongAsync(context, item.Song))
                    items.Add(item with { Generation = 1 });
            if (items.Count == 0) throw new ArgumentException("No eligible playable songs remain. Start another mix.");
            saved = preview with { Name = name.Trim(), Generation = 1, UpdatedAt = clock.UtcNow, Readiness = new(true, "Ready"), ShowInApps = true, Songs = items };
            document.Mixes[key] = saved;
            await SaveAsync(db, user, document, token);
            return saved;
        }
        finally { writes.Release(); }
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

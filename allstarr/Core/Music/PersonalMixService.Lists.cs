using allstarr.Core.ClientEvents;
using allstarr.Models.Domain;

namespace allstarr.Core.Music;

public sealed partial class PersonalMixService
{
    // A fixed list of songs saved as a mix in the listener's apps, such as a new release. The same id saves once.
    public async Task<PersonalMix> SaveListAsync(Guid owner, Guid id, string name, string description,
        IReadOnlyList<Song> songs, string reason, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || name.Any(char.IsControl)) throw new ArgumentException("Supply a name of 1–200 characters.");
        if (songs.Count is 0 or > 100) throw new ArgumentException("Supply 1–100 songs.");
        var key = "custom:" + id.ToString("N");
        var (user, captured, _) = await SnapshotAsync(owner, token);
        if (captured.Mixes.TryGetValue(key, out var saved))
        {
            if (!saved.Added)
            {
                await SetInAppsAsync(owner, saved.Id, true, token);
                saved = saved with { Added = true };
            }
            return (await VisibleAsync(owner, new() { Mixes = new() { [key] = saved } }, token)).Single();
        }
        var context = await ContextAsync(user, token);
        var playable = new List<Song>();
        foreach (var song in songs)
            if (song.IsLocal || await providers.CanPlaySongAsync(context, song)) playable.Add(song);
        if (playable.Count == 0) throw new ArgumentException("None of these songs can play right now.");
        await using var db = await factory.CreateDbContextAsync(token);
        var (_, history) = await HistoryAsync(db, user, token);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var (_, document, version) = await SnapshotAsync(owner, token);
            if (document.Mixes.TryGetValue(key, out saved))
            {
                if (!saved.Added)
                {
                    await SetInAppsAsync(owner, saved.Id, true, token);
                    saved = saved with { Added = true };
                }
                return (await VisibleAsync(owner, new() { Mixes = new() { [key] = saved } }, token)).Single();
            }
            var revision = await InputRevisionAsync(db, owner, token);
            saved = new PersonalMix(id, "custom", name.Trim(), description, 1, clock.UtcNow, new(true, "Ready"), true,
                playable.Select(song => new PersonalMixItem(id, 1, SafeSong(song), song.IsLocal ? "library" : song.ExternalProvider ?? "service",
                    reason, 1)).ToArray())
            {
                SeedName = name.Trim(),
                Evidence = new(MixRecipes.For("custom") with { Sentence = $"The songs on {name.Trim()}.", Target = playable.Count },
                    0, 0, null, null, null, 0, 0, songs.Count, playable.Count, playable.Count,
                    playable.Count(song => song.IsLocal), playable.Count(song => !song.IsLocal),
                    songs.Count > playable.Count ? new Dictionary<string, int> { ["Not playable right now"] = songs.Count - playable.Count } : new Dictionary<string, int>(),
                    null, null, null)
            };
            document.Mixes[key] = saved;
            if (await TrySaveAsync(user, document, version, revision, history, token))
            {
                clientEvents?.Publish(owner, ClientEvent.Mix(saved.Id));
                return saved;
            }
        }
        throw new InvalidOperationException("Listener choices changed while saving this mix. Save again.");
    }
}

using System.Collections.Concurrent;
using allstarr.Core.AudioMuse;
using allstarr.Core.Identity;
using allstarr.Core.Intelligence;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Music;

// Difference is how far the listener's favourites sit from their library, in hundredths of the library's spread:
// 0 is typical, +50 is half a spread more. Lean compares what they finish with what they skip the same way.
public sealed record SoundTrait(string Name, int Difference, int? Lean);
public sealed record SoundProfile(int Songs, int SkippedSongs, int LibrarySongs, IReadOnlyList<SoundTrait> Traits, int? Tempo,
    int? LibraryTempo, DateTimeOffset At);

// How a listener's music sounds, from AudioMuse's analysis of the library songs they finish, set against the rest of
// their library and the ones they skip. AudioMuse rates nearly every song 0.6–0.7 on every mood, so only differences
// from the library mean anything. It reads stored analysis only, takes seconds, and is kept for hours.
public sealed class SoundProfileService(IDbContextFactory<AllstarrDbContext> factory, IEffectiveProviderPolicyResolver policies,
    IPlatformClock clock, AudioMuseDiscovery? audioMuse = null, ILogger<SoundProfileService>? logger = null)
{
    private const int FinishedSongs = 250, SkippedSongs = 100, LibrarySample = 450, MinimumSongs = 20, MinimumSkipped = 10;
    private static readonly TimeSpan KeptFor = TimeSpan.FromHours(6);
    private static readonly (string Name, Func<AudioMuseScores, double?> Value)[] Traits =
    [
        ("Energetic", item => item.Energy), ("Danceable", item => item.Danceable), ("Happy", item => item.Happy),
        ("Relaxed", item => item.Relaxed), ("Festive", item => item.Party), ("Intense", item => item.Aggressive),
        ("Melancholy", item => item.Sad)
    ];
    private sealed record Kept(SoundProfile? Profile, DateTimeOffset At);
    private readonly ConcurrentDictionary<Guid, Kept> kept = new();
    private readonly ConcurrentDictionary<Guid, Lazy<Task<SoundProfile?>>> loading = new();

    public async Task<SoundProfile?> GetAsync(Guid user, CancellationToken token)
    {
        if (kept.TryGetValue(user, out var known) && clock.UtcNow - known.At < KeptFor)
        { RequestTiming.Current?.NoteCache(true); return known.Profile; }
        RequestTiming.Current?.NoteCache(false);
        var pending = loading.GetOrAdd(user, key => new(() => Task.Run(async () =>
        {
            try
            {
                var profile = await ComputeAsync(user, CancellationToken.None);
                kept[user] = new(profile, clock.UtcNow);
                return profile;
            }
            finally { loading.TryRemove(user, out _); }
        })));
        return await pending.Value.WaitAsync(token);
    }

    internal async Task WarmAsync(Guid user, CancellationToken token)
    {
        if (kept.TryGetValue(user, out var known) && clock.UtcNow - known.At < KeptFor) return;
        // Requests compute independently if they arrive before this background read finishes.
        var profile = await ComputeAsync(user, token);
        var warmed = new Kept(profile, clock.UtcNow);
        if (known == null) kept.TryAdd(user, warmed);
        else kept.TryUpdate(user, warmed, known);
    }

    private async Task<SoundProfile?> ComputeAsync(Guid userId, CancellationToken token)
    {
        if (audioMuse == null) return null;
        await using var db = await factory.CreateDbContextAsync(token);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == userId && item.Enabled, token);
        if (user == null) return null;
        var plays = await (from play in db.ListeningEvents.AsNoTracking()
                           join track in db.LibraryTracks.AsNoTracking() on play.LibraryTrackId equals (Guid?)track.Id
                           where play.OwnerUserId == userId &&
                               (play.State == ListeningEventState.Completed || play.State == ListeningEventState.Skipped)
                           group play by new { track.BackendItemId, play.State } into songs
                           select new { songs.Key.BackendItemId, songs.Key.State, Plays = songs.Count() }).ToArrayAsync(token);
        var finished = plays.Where(item => item.State == ListeningEventState.Completed).ToDictionary(item => item.BackendItemId, item => item.Plays);
        var skipped = plays.Where(item => item.State == ListeningEventState.Skipped && item.Plays > finished.GetValueOrDefault(item.BackendItemId))
            .ToDictionary(item => item.BackendItemId, item => item.Plays);
        var favourites = finished.OrderByDescending(item => item.Value).ThenBy(item => item.Key, StringComparer.Ordinal).Take(FinishedSongs).ToArray();
        var skips = skipped.OrderByDescending(item => item.Value).ThenBy(item => item.Key, StringComparer.Ordinal).Take(SkippedSongs).ToArray();
        if (favourites.Length < MinimumSongs) return null;
        var context = new ProtocolExecutionContext(Enum.Parse<ProtocolKind>(user.BackendType, true), user.BackendInstanceId,
            user.BackendPrincipalId, new AllstarrPrincipal(user.Id, user.BackendType, user.BackendInstanceId, user.BackendPrincipalId,
                user.DisplayName, user.IsAdmin), "sound-profile", clock.UtcNow.AddMinutes(5), token)
        { Policy = await policies.ResolveForUserAsync(user.Id, token), IsReadOnly = true };
        // A spread of the library to compare with; song IDs are hashes, so ordering by them samples evenly.
        var known = favourites.Concat(skips).Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
        var sample = (await db.LibraryTracks.AsNoTracking().Where(item => item.OwnerUserId == userId &&
                item.Protocol == user.BackendType && item.BackendInstanceId == user.BackendInstanceId)
            .Select(item => item.BackendItemId).Distinct().OrderBy(id => id).ToArrayAsync(token))
            .Where(id => !known.Contains(id)).Take(LibrarySample).ToArray();
        var features = await audioMuse.MixFeaturesAsync(context, known.Concat(sample).ToArray(), token);
        // A song played a hundred times says more than one played once, but not a hundred times more.
        var liked = favourites.Where(item => features.ContainsKey(item.Key))
            .Select(item => (Scores: features[item.Key], Weight: 1 + Math.Log(item.Value))).ToArray();
        var disliked = skips.Where(item => features.ContainsKey(item.Key)).Select(item => (Scores: features[item.Key], Weight: 1d)).ToArray();
        var library = sample.Where(features.ContainsKey).Select(id => (Scores: features[id], Weight: 1d)).ToArray();
        logger?.LogInformation("Sound profile: {Liked} finished, {Disliked} skipped and {Library} other library songs analyzed",
            liked.Length, disliked.Length, library.Length);
        if (liked.Length < MinimumSongs || library.Length < MinimumSongs) return null;
        var traits = Traits.Select(trait =>
        {
            var value = Mean(liked, trait.Value);
            var typical = Mean(library, trait.Value);
            var spread = Spread(library, trait.Value);
            if (value == null || typical == null || spread is not > 1e-6) return null;
            var skippedValue = disliked.Length >= MinimumSkipped ? Mean(disliked, trait.Value) : null;
            return new SoundTrait(trait.Name, (int)Math.Round(100 * (value.Value - typical.Value) / spread.Value),
                skippedValue == null ? null : (int)Math.Round(100 * (value.Value - skippedValue.Value) / spread.Value));
        }).OfType<SoundTrait>().ToArray();
        static double? Tempo(AudioMuseScores item) => item.Tempo is > 0 and < 300 ? item.Tempo : null;
        var tempo = Mean(liked, Tempo);
        var libraryTempo = Mean(library, Tempo);
        return traits.Length == 0 ? null : new(liked.Length, disliked.Length, library.Length, traits,
            tempo == null ? null : (int)Math.Round(tempo.Value), libraryTempo == null ? null : (int)Math.Round(libraryTempo.Value), clock.UtcNow);
    }

    private static double? Spread(IReadOnlyList<(AudioMuseScores Scores, double Weight)> songs, Func<AudioMuseScores, double?> value)
    {
        var values = songs.Select(item => value(item.Scores)).OfType<double>().ToArray();
        if (values.Length < 2) return null;
        var mean = values.Average();
        return Math.Sqrt(values.Sum(item => (item - mean) * (item - mean)) / (values.Length - 1));
    }

    private static double? Mean(IReadOnlyList<(AudioMuseScores Scores, double Weight)> songs, Func<AudioMuseScores, double?> value)
    {
        var known = songs.Where(item => value(item.Scores) != null).ToArray();
        return known.Length == 0 ? null : known.Sum(item => item.Weight * value(item.Scores)!.Value) / known.Sum(item => item.Weight);
    }
}

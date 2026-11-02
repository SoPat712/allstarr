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

public sealed record SoundTrait(string Name, int Value, int? Lean);
public sealed record SoundProfile(int Songs, int SkippedSongs, IReadOnlyList<SoundTrait> Traits, int? Tempo, DateTimeOffset At);

// How a listener's music sounds, from AudioMuse's analysis of the library songs they finish, set against the ones they
// skip. It reads stored analysis only, takes a few seconds for a long history, and is kept for hours.
public sealed class SoundProfileService(IDbContextFactory<AllstarrDbContext> factory, IEffectiveProviderPolicyResolver policies,
    IPlatformClock clock, AudioMuseDiscovery? audioMuse = null)
{
    private const int FinishedSongs = 250, SkippedSongs = 100, MinimumSongs = 20, MinimumSkipped = 10;
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
        if (kept.TryGetValue(user, out var known) && clock.UtcNow - known.At < KeptFor) return known.Profile;
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
        var features = await audioMuse.MixFeaturesAsync(context, favourites.Concat(skips).Select(item => item.Key).ToArray(), token);
        // A song played a hundred times says more than one played once, but not a hundred times more.
        var liked = favourites.Where(item => features.ContainsKey(item.Key))
            .Select(item => (Scores: features[item.Key], Weight: 1 + Math.Log(item.Value))).ToArray();
        var disliked = skips.Where(item => features.ContainsKey(item.Key)).Select(item => (Scores: features[item.Key], Weight: 1d)).ToArray();
        if (liked.Length < MinimumSongs) return null;
        var traits = Traits.Select(trait =>
        {
            var value = Mean(liked, trait.Value);
            var skippedValue = disliked.Length >= MinimumSkipped ? Mean(disliked, trait.Value) : null;
            return value == null ? null : new SoundTrait(trait.Name, (int)Math.Round(100 * value.Value),
                skippedValue == null ? null : (int)Math.Round(100 * (value.Value - skippedValue.Value)));
        }).OfType<SoundTrait>().ToArray();
        var tempo = Mean(liked, item => item.Tempo is > 0 and < 300 ? item.Tempo : null);
        return traits.Length == 0 ? null : new(liked.Length, disliked.Length, traits, tempo == null ? null : (int)Math.Round(tempo.Value), clock.UtcNow);
    }

    private static double? Mean(IReadOnlyList<(AudioMuseScores Scores, double Weight)> songs, Func<AudioMuseScores, double?> value)
    {
        var known = songs.Where(item => value(item.Scores) != null).ToArray();
        return known.Length == 0 ? null : known.Sum(item => item.Weight * value(item.Scores)!.Value) / known.Sum(item => item.Weight);
    }
}

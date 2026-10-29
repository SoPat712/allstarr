using System.Text.Json;
using allstarr.Core.Enrichment;
using allstarr.Core.Operations;
using allstarr.Core.Playback;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Music;

public sealed record TasteNudgeRequest(string Kind, string Name, string Mode, bool TodayOnly = false, string? TimeZoneId = null);
public sealed record TasteNudge(string Kind, string Name, string Mode, DateTimeOffset? ExpiresAt);
public sealed record TasteStrength(string Name, int Strength, double Value);
public sealed record TasteLayer(IReadOnlyList<TasteStrength> Tags, IReadOnlyList<TasteStrength> Artists);
public sealed record TasteProfile(TasteLayer Lately, TasteLayer Usually, double LatelyWeight, IReadOnlyList<TasteNudge> Nudges, bool Paused, bool UseImportedHistory = true);
public sealed record TasteScore(double Score, IReadOnlyList<string> Reasons, bool Blocked = false);
internal sealed record TasteAffinity(string Name, double Lately, double Usually, DateTimeOffset At);
internal sealed record TasteOccurrence(DateTimeOffset At, double Position, double Listened, bool Done = false);
internal sealed class TasteDocument
{
    public bool Paused { get; set; }
    public bool UseImportedHistory { get; set; } = true;
    public DateTimeOffset LearnAfter { get; set; }
    public Dictionary<string, TasteAffinity> Learned { get; set; } = [];
    public List<DateTimeOffset> Recent { get; set; } = [];
    public List<TasteNudge> Nudges { get; set; } = [];
    public Dictionary<string, DateTimeOffset> Receipts { get; set; } = [];
    public Dictionary<Guid, DateTimeOffset> Plays { get; set; } = [];
    public Dictionary<string, TasteOccurrence> Occurrences { get; set; } = [];
}

public sealed partial class TasteService(IDbContextFactory<AllstarrDbContext> factory, SharedTagStore tags, IPlatformClock clock)
{
    public static readonly TimeSpan LatelyHalfLife = TimeSpan.FromHours(6);
    public static readonly TimeSpan UsuallyHalfLife = TimeSpan.FromDays(30);
    private const int RecentThreshold = 5;
    private const double RecentBlend = .8;
    private readonly SemaphoreSlim writes = new(1, 1);

    public static double Decay(double value, TimeSpan age, TimeSpan halfLife) =>
        value * Math.Pow(.5, Math.Max(0, age.TotalSeconds) / halfLife.TotalSeconds);
    private static string Key(string kind, string name) => kind + ":" + name.Trim().ToLowerInvariant();
    private static double Blend(TasteDocument document) => document.Recent.Count >= RecentThreshold ? RecentBlend : 0;
    private static double Bounded(double value) => value / (3 + Math.Abs(value));
    private static TasteDocument Read(TasteProfileRecord? row) => row == null ? new() : JsonSerializer.Deserialize<TasteDocument>(row.PayloadJson) ?? new();

    private void Prune(TasteDocument document)
    {
        var now = clock.UtcNow;
        document.Nudges.RemoveAll(item => item.ExpiresAt <= now);
        document.Recent.RemoveAll(at => at < now - LatelyHalfLife);
        foreach (var key in document.Receipts.Where(item => item.Value < now.AddDays(-8)).Select(item => item.Key).ToArray()) document.Receipts.Remove(key);
        foreach (var key in document.Plays.Where(item => item.Value < now.AddDays(-7)).Select(item => item.Key).ToArray()) document.Plays.Remove(key);
        foreach (var key in document.Occurrences.Where(item => item.Value.At < now.AddDays(-8)).Select(item => item.Key).ToArray()) document.Occurrences.Remove(key);
    }

    private async Task<TasteDocument> LoadAsync(Guid user, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        await RequireUser(db, user, token);
        var document = Read(await db.TasteProfiles.AsNoTracking().SingleOrDefaultAsync(item => item.OwnerUserId == user, token));
        Prune(document);
        if (document.UseImportedHistory && !document.Paused) await AddImportedHistoryAsync(db, user, document, token);
        return document;
    }

    private static async Task RequireUser(AllstarrDbContext db, Guid user, CancellationToken token)
    {
        if (!await db.Users.AnyAsync(item => item.Id == user && item.Enabled, token)) throw new UnauthorizedAccessException();
    }

    private async Task ChangeAsync(Guid user, Action<TasteDocument> change, CancellationToken token)
    {
        await writes.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            await RequireUser(db, user, token);
            var row = await db.TasteProfiles.SingleOrDefaultAsync(item => item.OwnerUserId == user, token);
            var document = Read(row);
            Prune(document);
            change(document);
            if (row == null) db.TasteProfiles.Add(row = new() { OwnerUserId = user });
            row.PayloadJson = JsonSerializer.Serialize(document);
            row.Revision++;
            await db.SaveChangesAsync(token);
        }
        finally { writes.Release(); }
    }

    public async Task<TasteProfile> GetAsync(Guid user, CancellationToken token = default)
    {
        var document = await LoadAsync(user, token);
        TasteLayer Layer(bool lately)
        {
            TasteStrength[] Top(string kind) => document.Learned.Where(item => item.Key.StartsWith(kind + ":", StringComparison.Ordinal))
                .Select(item =>
                {
                    var value = Decay(lately ? item.Value.Lately : item.Value.Usually, clock.UtcNow - item.Value.At, lately ? LatelyHalfLife : UsuallyHalfLife);
                    return new TasteStrength(item.Value.Name, (int)Math.Round(100 * Math.Abs(Bounded(value))), value);
                }).Where(item => item.Strength > 0).OrderByDescending(item => item.Strength).ThenBy(item => item.Name, StringComparer.Ordinal).Take(20).ToArray();
            return new(Top("tag"), Top("artist"));
        }
        return new(Layer(true), Layer(false), Blend(document), document.Nudges, document.Paused, document.UseImportedHistory);
    }

    public Task PutNudgeAsync(Guid user, TasteNudgeRequest request, CancellationToken token = default)
    {
        var name = NormalizeName(request.Kind, request.Name);
        if (request.Mode is not ("more" or "less" or "never")) throw new ArgumentException("Invalid nudge.");
        DateTimeOffset? expires = null;
        if (request.TodayOnly)
        {
            if (string.IsNullOrWhiteSpace(request.TimeZoneId)) throw new ArgumentException("A local time zone is required.");
            TimeZoneInfo zone;
            try { zone = TimeZoneInfo.FindSystemTimeZoneById(request.TimeZoneId); }
            catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException) { throw new ArgumentException("Invalid time zone."); }
            var midnight = TimeZoneInfo.ConvertTime(clock.UtcNow, zone).Date.AddDays(1);
            while (zone.IsInvalidTime(midnight)) midnight = midnight.AddMinutes(1);
            expires = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(midnight, zone));
        }
        return ChangeAsync(user, document =>
        {
            document.Nudges.RemoveAll(item => Key(item.Kind, item.Name) == Key(request.Kind, name));
            document.Nudges.Add(new(request.Kind, name, request.Mode, expires));
        }, token);
    }

    private static string NormalizeName(string kind, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || name.Any(char.IsControl)) throw new ArgumentException("Invalid name.");
        return kind switch
        {
            "tag" => SharedTagNormalization.Canonical(name, []) ?? throw new ArgumentException("Unknown canonical tag."),
            "artist" => name.Trim(),
            "song" => name.Trim(),
            _ => throw new ArgumentException("Invalid nudge kind.")
        };
    }

    public Task DeleteNudgeAsync(Guid user, string kind, string name, CancellationToken token = default)
    {
        name = NormalizeName(kind, name);
        return ChangeAsync(user, document => document.Nudges.RemoveAll(item => Key(item.Kind, item.Name) == Key(kind, name)), token);
    }

    public Task SetPausedAsync(Guid user, bool paused, CancellationToken token = default) => ChangeAsync(user, document =>
    {
        if (document.Paused == paused) return;
        document.Paused = paused;
        document.LearnAfter = clock.UtcNow;
        document.Occurrences.Clear();
    }, token);

    public Task ResetAsync(Guid user, bool clearNudges, CancellationToken token = default) => ChangeAsync(user, document =>
    {
        document.Learned.Clear();
        document.Recent.Clear();
        document.Plays.Clear();
        document.Occurrences.Clear();
        document.LearnAfter = clock.UtcNow;
        if (clearNudges) document.Nudges.Clear();
    }, token);

    private async Task<bool> ExcludedAsync(Guid user, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        return !await db.Users.AnyAsync(row => row.Id == user && row.Enabled && !row.IsSynthetic, token);
    }

    public async Task ObservePlaybackAsync(PlaybackSignalPayload payload, PlaybackTrackSnapshot? track, CancellationToken token)
    {
        if (payload.Failed || payload.StreamSource?.RejectionReason != null || payload.SourceKind != "protocol" || track?.CanonicalRecordingId == null ||
            payload.Transition == PlaybackTransition.InferredStop ||
            await ExcludedAsync(payload.Scope.OwnerUserId, token)) return;
        var song = new Song { CanonicalRecordingId = track.CanonicalRecordingId, Artist = track.Artist };
        var features = await FeaturesAsync([song], token);
        var occurrence = payload.OccurrenceKey ?? payload.SignalKey;
        await ChangeAsync(payload.Scope.OwnerUserId, document =>
        {
            if (document.Paused || payload.ObservedAt < document.LearnAfter || payload.ObservedAt > clock.UtcNow || payload.ObservedAt < clock.UtcNow.AddDays(-8)) return;
            if (payload.Transition == PlaybackTransition.Submission)
            {
                if (!payload.Paused) Learn(document, occurrence, song, features[song], 1, payload.ObservedAt, true);
                return;
            }
            var seconds = TimeSpan.FromTicks(payload.ReportedPositionTicks ?? payload.PositionTicks ?? 0).TotalSeconds;
            var previous = document.Occurrences.GetValueOrDefault(occurrence);
            if (previous?.Done == true || previous?.At >= payload.ObservedAt) return;
            if (payload.Transition is PlaybackTransition.Start or PlaybackTransition.InferredStart)
            {
                document.Occurrences.TryAdd(occurrence, new(payload.ObservedAt, seconds, 0));
                return;
            }
            if (previous == null) return;
            if (payload.Paused)
            {
                document.Occurrences[occurrence] = previous with { At = payload.ObservedAt, Position = seconds };
                return;
            }
            var elapsed = (payload.ObservedAt - previous.At).TotalSeconds;
            var delta = seconds - previous.Position;
            // Position jumps and long reporting gaps do not establish listening time.
            var listened = previous.Listened + (delta > 0 && delta <= elapsed + 2 && elapsed <= 300 ? Math.Min(delta, elapsed) : 0);
            var stopped = payload.Transition == PlaybackTransition.Stop;
            var qualified = ScopedPlaybackScrobbleDelivery.EligibleForCompletedScrobble(track.DurationMilliseconds, TimeSpan.FromSeconds(listened).Ticks) ||
                stopped && track.DurationMilliseconds is > 0 && listened * 1000 >= track.DurationMilliseconds;
            document.Occurrences[occurrence] = new(payload.ObservedAt, seconds, listened, stopped || qualified);
            if (qualified) Learn(document, occurrence, song, features[song], 1, payload.ObservedAt, true);
            else if (stopped && listened > 0 && delta >= 0 && delta <= elapsed + 2)
                Learn(document, occurrence, song, features[song], listened < 30 || listened * 1000 < track.DurationMilliseconds * .25 ? -1 : -.3, payload.ObservedAt, false);
        }, token);
    }

    public async Task ObserveHeartAsync(Guid user, string receipt, Song song, DateTimeOffset at, CancellationToken token)
    {
        if (await ExcludedAsync(user, token)) return;
        var features = await FeaturesAsync([song], token);
        await ChangeAsync(user, document => Learn(document, "heart:" + receipt, song, features[song], 2, at, false), token);
    }

    private bool CanLearn(TasteDocument document, DateTimeOffset at) => !document.Paused && at > document.LearnAfter && at <= clock.UtcNow && at >= clock.UtcNow.AddDays(-8);

    private void Learn(TasteDocument document, string receipt, Song song, IReadOnlyDictionary<string, string> features, double value, DateTimeOffset at, bool play)
    {
        if (!CanLearn(document, at) || !document.Receipts.TryAdd(receipt, at)) return;
        if (play && song.CanonicalRecordingId is { } id)
        {
            if (document.Plays.TryGetValue(id, out var previous) && previous <= at && at - previous <= TimeSpan.FromDays(7)) value += .5;
            if (previous <= at) document.Plays[id] = at;
        }
        if (at >= clock.UtcNow - LatelyHalfLife) document.Recent.Add(at);
        foreach (var feature in features)
        {
            if (feature.Key.StartsWith("song:", StringComparison.Ordinal)) continue;
            var prior = document.Learned.GetValueOrDefault(feature.Key) ?? new(feature.Value, 0, 0, clock.UtcNow);
            document.Learned[feature.Key] = new(feature.Value,
                Decay(prior.Lately, clock.UtcNow - prior.At, LatelyHalfLife) + Decay(value, clock.UtcNow - at, LatelyHalfLife),
                Decay(prior.Usually, clock.UtcNow - prior.At, UsuallyHalfLife) + Decay(value, clock.UtcNow - at, UsuallyHalfLife), clock.UtcNow);
        }
    }

    private async Task<Dictionary<Song, IReadOnlyDictionary<string, string>>> FeaturesAsync(IReadOnlyList<Song> songs, CancellationToken token)
    {
        var profiles = await tags.GetTags(songs.Select(song => song.CanonicalRecordingId).OfType<Guid>().Distinct().ToArray(), token);
        return songs.Distinct().ToDictionary(song => song, song =>
        {
            var features = new Dictionary<string, string>();
            features[Key("song", PersonalMixRules.Key(song))] = song.Title;
            if (song.CanonicalRecordingId is { } id && profiles.TryGetValue(id, out var profile))
                foreach (var tag in profile.Tags) features[Key("tag", tag.Tag)] = tag.Tag;
            foreach (var artist in song.Artists.Append(song.Artist).Where(artist => !string.IsNullOrWhiteSpace(artist)).Distinct(StringComparer.OrdinalIgnoreCase))
                features[Key("artist", artist)] = artist.Trim();
            return (IReadOnlyDictionary<string, string>)features;
        });
    }

    private TasteScore Score(TasteDocument document, IReadOnlyDictionary<string, string> features, string? seed, bool followed)
    {
        var matching = document.Nudges.Where(item => features.ContainsKey(Key(item.Kind, item.Name))).ToArray();
        if (matching.FirstOrDefault(item => item.Mode == "never") is { } blocked) return new(0, [$"You blocked {blocked.Name}"], true);
        if (!features.Keys.Any(key => key.StartsWith("tag:", StringComparison.Ordinal)) && matching.Length == 0) return new(0, []);
        var reasons = new List<(double Weight, string Reason)>();
        var blend = Blend(document);
        var score = 0d;
        foreach (var feature in features)
        {
            if (!document.Learned.TryGetValue(feature.Key, out var affinity)) continue;
            var lately = Decay(affinity.Lately, clock.UtcNow - affinity.At, LatelyHalfLife);
            var usually = Decay(affinity.Usually, clock.UtcNow - affinity.At, UsuallyHalfLife);
            var contribution = blend * Bounded(lately) + (1 - blend) * Bounded(usually);
            score += contribution / Math.Max(1, features.Keys.Count(key => !key.StartsWith("song:", StringComparison.Ordinal)));
            if (Math.Abs(contribution) < .01) continue;
            var name = affinity.Name;
            reasons.Add((Math.Abs(contribution), blend > .5 && Math.Sign(lately) == Math.Sign(contribution)
                ? lately > 0 ? $"You've been playing {name} today" : $"You've been skipping {name} today"
                : usually > 0 ? $"You usually enjoy {name}" : $"You usually skip {name}"));
        }
        foreach (var nudge in matching)
        {
            score += nudge.Mode == "more" ? 2 : -2;
            reasons.Add((2, $"You asked for {nudge.Mode} {nudge.Name}"));
        }
        var explanation = reasons.OrderByDescending(item => item.Weight).Select(item => item.Reason).Take(3).ToList();
        if (!string.IsNullOrWhiteSpace(seed)) explanation.Add($"Similar to {seed}");
        if (followed) explanation.Add("New from an artist you follow");
        return new(score, explanation);
    }

    public async Task<TasteScore> ScoreAsync(Guid user, Song song, string? seed = null, bool followed = false, CancellationToken token = default)
    {
        var document = await LoadAsync(user, token);
        return Score(document, (await FeaturesAsync([song], token))[song], seed, followed);
    }

    internal async Task<IReadOnlyDictionary<Song, TasteScore>> ScoreManyAsync(Guid user, IReadOnlyList<Song> songs, CancellationToken token)
    {
        var document = await LoadAsync(user, token);
        var features = await FeaturesAsync(songs, token);
        return features.ToDictionary(item => item.Key, item => Score(document, item.Value, null, false));
    }

    public async Task<IReadOnlyList<Song>> RankAsync(Guid user, IReadOnlyList<Song> songs, string? seed = null, CancellationToken token = default)
    {
        var document = await LoadAsync(user, token);
        var features = await FeaturesAsync(songs, token);
        return songs.Select(song => (Song: song, Taste: Score(document, features[song], seed, false)))
            .Where(item => !item.Taste.Blocked).OrderByDescending(item => item.Taste.Score)
            .Select(item => item.Song.WithTasteReason(item.Taste.Reasons.FirstOrDefault())).ToArray();
    }
}

using allstarr.Core.Intelligence;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Music;

public sealed partial class TasteService
{
    public async Task SetUseImportedHistoryAsync(Guid user, bool enabled, CancellationToken token = default)
    {
        await ChangeAsync(user, document => document.UseImportedHistory = enabled, token);
        if (jobs == null) return;
        await using var db = await factory.CreateDbContextAsync(token);
        if (!await db.ExternalMetadataSnapshots.AnyAsync(item => item.OwnerUserId == user && item.ResourceKind == "personal-mixes", token)) return;
        var revision = await db.TasteProfiles.Where(item => item.OwnerUserId == user).Select(item => item.Revision).SingleAsync(token);
        await jobs.EnqueueAsync(new allstarr.Core.Jobs.DurableJobEnqueueRequest<Guid>(PersonalMixService.JobType,
            $"taste-history:{user:N}:{revision}", Guid.Empty, user, Priority: -100), token);
    }

    internal Task RememberPlaybackAsync(Guid user, CancellationToken token) => ChangeAsync(user, _ => { }, token);

    private async Task RememberPlaybackAsync(AllstarrDbContext db, Guid user, TasteDocument document, CancellationToken token)
    {
        var receipts = document.Receipts.Keys.Where(key => !document.PlaybackLearning.ContainsKey(key)).ToArray();
        if (receipts.Length == 0) return;
        var listener = await db.Users.AsNoTracking().SingleAsync(item => item.Id == user, token);
        var events = await db.ListeningEvents.AsNoTracking().Where(item => item.OwnerUserId == user &&
            item.Protocol == listener.BackendType && item.BackendInstanceId == listener.BackendInstanceId &&
            item.SourceKind == "protocol" && receipts.Contains(item.OccurrenceKey) &&
            (item.State == ListeningEventState.Completed || item.State == ListeningEventState.Skipped)).ToArrayAsync(token);
        var captured = document.Learned.ToDictionary(item => item.Key, item => item.Value.Name);
        foreach (var item in events)
            document.PlaybackLearning[item.OccurrenceKey] = new(item.ListenedAt ?? item.UpdatedAt, HistoryWeight(item), captured, Legacy: true);
    }

    private static double HistoryWeight(ListeningEventRecord item)
    {
        var seconds = TimeSpan.FromTicks(item.PositionTicks ?? 0).TotalSeconds;
        return item.State == ListeningEventState.Completed ? 1 :
            seconds < 30 || seconds * 1000 < item.DurationMilliseconds * .25 ? -1 : -.3;
    }

    internal async Task<bool> UseImportedHistoryAsync(Guid user, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        return Read(await db.TasteProfiles.AsNoTracking().SingleOrDefaultAsync(item => item.OwnerUserId == user, token)).UseImportedHistory;
    }

    private async Task AddListeningHistoryAsync(AllstarrDbContext db, Guid user, TasteDocument document, CancellationToken token)
    {
        var listener = await db.Users.AsNoTracking().SingleAsync(item => item.Id == user, token);
        if (listener.IsSynthetic) return;
        var events = db.ListeningEvents.AsNoTracking().Where(item => item.OwnerUserId == user &&
            item.Protocol == listener.BackendType && item.BackendInstanceId == listener.BackendInstanceId &&
            (item.SourceKind == "protocol" || document.UseImportedHistory && (item.SourceKind == "import" || item.SourceKind == "listenbrainz-api")) && (item.State == ListeningEventState.Completed || item.State == ListeningEventState.Skipped));
        var offsets = new Dictionary<string, TimeSpan>();
        if (document.UseImportedHistory)
        {
            var newest = await db.ListeningEvents.AsNoTracking().Where(item => item.OwnerUserId == user &&
                item.Protocol == listener.BackendType && item.BackendInstanceId == listener.BackendInstanceId &&
                item.SourceKind == "import" && item.ImportProvenance != null && item.ImportProvenance.StartsWith("history-import:") &&
                item.ImportProvenance.Length >= 47 && (item.ListenedAt ?? item.UpdatedAt) <= clock.UtcNow)
                .GroupBy(item => item.ImportProvenance!.Substring(0, 47))
                .Select(group => new { Key = group.Key, At = group.Max(item => item.ListenedAt ?? item.UpdatedAt) }).ToArrayAsync(token);
            var imports = await db.ListeningHistoryImports.AsNoTracking().Where(item => item.OwnerUserId == user &&
                item.Protocol == listener.BackendType && item.BackendInstanceId == listener.BackendInstanceId &&
                item.State == ListeningHistoryImportState.Completed && item.CompletedAt != null).ToArrayAsync(token);
            foreach (var import in imports)
            {
                var key = $"history-import:{import.Id:N}";
                if (newest.FirstOrDefault(item => item.Key == key) is { } last && import.CompletedAt > last.At)
                    offsets[key] = import.CompletedAt.Value - last.At;
            }
        }
        var batch = new List<ListeningEventRecord>(500);
        await foreach (var item in events.AsAsyncEnumerable().WithCancellation(token))
        {
            batch.Add(item);
            if (batch.Count == 500) await ApplyBatch();
        }
        await ApplyBatch();

        async Task ApplyBatch()
        {
            if (batch.Count == 0) return;
            var songs = batch.Select(item => new Song { CanonicalRecordingId = item.CanonicalRecordingId, Artist = item.Artist ?? "" }).ToArray();
            var features = await FeaturesAsync(songs, token);
            for (var index = 0; index < batch.Count; index++)
            {
                var item = batch[index];
                var at = item.ListenedAt ?? item.UpdatedAt;
                if (item.SourceKind == "import" && item.ImportProvenance is { Length: >= 47 } provenance)
                    at += offsets.GetValueOrDefault(provenance[..47]);
                if (at > clock.UtcNow || item.SourceKind == "protocol" && at <= document.LearnAfter) continue;
                var value = HistoryWeight(item);
                var live = item.SourceKind == "protocol";
                var learned = live ? document.PlaybackLearning.GetValueOrDefault(item.OccurrenceKey) : null;
                if (learned != null && !learned.Legacy)
                {
                    value = learned.Value;
                    foreach (var feature in learned.Features)
                    {
                        if (feature.Key.StartsWith("song:", StringComparison.Ordinal) || !document.Learned.TryGetValue(feature.Key, out var prior)) continue;
                        document.Learned[feature.Key] = prior with
                        {
                            Lately = Decay(prior.Lately, clock.UtcNow - prior.At, LatelyHalfLife) - Decay(value, clock.UtcNow - learned.At, LatelyHalfLife),
                            Usually = Decay(prior.Usually, clock.UtcNow - prior.At, UsuallyHalfLife) - Decay(value, clock.UtcNow - learned.At, UsuallyHalfLife),
                            At = clock.UtcNow
                        };
                    }
                }
                var legacy = learned?.Legacy == true;
                if (live && learned == null && !legacy && at >= clock.UtcNow - LatelyHalfLife) document.Recent.Add(at);
                foreach (var feature in features[songs[index]])
                {
                    if (feature.Key.StartsWith("song:", StringComparison.Ordinal) || legacy && learned!.Features.ContainsKey(feature.Key)) continue;
                    var prior = document.Learned.GetValueOrDefault(feature.Key) ?? new(feature.Value, 0, 0, clock.UtcNow);
                    document.Learned[feature.Key] = new(feature.Value,
                        Decay(prior.Lately, clock.UtcNow - prior.At, LatelyHalfLife) + (live ? Decay(value, clock.UtcNow - at, LatelyHalfLife) : 0),
                        Decay(prior.Usually, clock.UtcNow - prior.At, UsuallyHalfLife) + Decay(value, clock.UtcNow - at, live ? UsuallyHalfLife : ImportedHistoryHalfLife), clock.UtcNow);
                }
            }
            batch.Clear();
        }
    }
}

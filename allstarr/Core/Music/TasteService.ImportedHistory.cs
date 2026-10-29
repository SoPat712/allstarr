using allstarr.Core.Intelligence;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Music;

public sealed partial class TasteService
{
    public Task SetUseImportedHistoryAsync(Guid user, bool enabled, CancellationToken token = default) =>
        ChangeAsync(user, document => document.UseImportedHistory = enabled, token);

    private async Task AddImportedHistoryAsync(AllstarrDbContext db, Guid user, TasteDocument document, CancellationToken token)
    {
        var listener = await db.Users.AsNoTracking().SingleAsync(item => item.Id == user, token);
        if (listener.IsSynthetic) return;
        var events = db.ListeningEvents.AsNoTracking().Where(item => item.OwnerUserId == user &&
            item.Protocol == listener.BackendType && item.BackendInstanceId == listener.BackendInstanceId &&
            item.SourceKind == "import" && (item.State == ListeningEventState.Completed || item.State == ListeningEventState.Skipped));
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
                if (at > clock.UtcNow) continue;
                var seconds = TimeSpan.FromTicks(item.PositionTicks ?? 0).TotalSeconds;
                var value = item.State == ListeningEventState.Completed ? 1 :
                    seconds < 30 || seconds * 1000 < item.DurationMilliseconds * .25 ? -1 : -.3;
                foreach (var feature in features[songs[index]])
                {
                    if (feature.Key.StartsWith("song:", StringComparison.Ordinal)) continue;
                    var prior = document.Learned.GetValueOrDefault(feature.Key) ?? new(feature.Value, 0, 0, clock.UtcNow);
                    document.Learned[feature.Key] = new(feature.Value,
                        Decay(prior.Lately, clock.UtcNow - prior.At, LatelyHalfLife),
                        Decay(prior.Usually, clock.UtcNow - prior.At, UsuallyHalfLife) + Decay(value, clock.UtcNow - at, UsuallyHalfLife), clock.UtcNow);
                }
            }
            batch.Clear();
        }
    }
}

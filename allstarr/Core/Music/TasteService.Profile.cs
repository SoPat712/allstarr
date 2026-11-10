using System.Collections.Concurrent;
using System.Text.Json;
using allstarr.Core.Enrichment;
using allstarr.Core.Intelligence;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Music;

internal sealed record TasteProfileSnapshot(TasteProfile Profile, long Revision, int Plays, long Tags, DateTimeOffset At);

public sealed partial class TasteService
{
    private readonly ConcurrentDictionary<Guid, Lazy<Task>> refreshingProfiles = new();
    private readonly ConcurrentDictionary<Guid, long> requestedProfiles = new();

    public async Task<TasteProfile> GetCachedAsync(Guid user, CancellationToken token = default)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        await RequireUser(db, user, token);
        var row = await db.TasteProfiles.AsNoTracking().SingleOrDefaultAsync(item => item.OwnerUserId == user, token);
        var document = Read(row);
        var snapshot = document.Snapshot;
        var plays = snapshot == null ? 0 : await db.ListeningEvents.AsNoTracking().CountAsync(item => item.OwnerUserId == user &&
            (item.State == ListeningEventState.Completed || item.State == ListeningEventState.Skipped), token);
        if (snapshot == null || !loaded.ContainsKey(user) || snapshot.Revision != row!.Revision || snapshot.Plays != plays ||
            snapshot.Tags != SharedTagStore.Changes || clock.UtcNow - snapshot.At >= LoadedFor ||
            snapshot.Profile.Nudges.Any(item => item.ExpiresAt <= clock.UtcNow))
            _ = RefreshProfileAsync(user);
        document.Nudges.RemoveAll(item => item.ExpiresAt <= clock.UtcNow);
        if (snapshot == null) document.Scales = Scales(document);
        return (snapshot?.Profile ?? Profile(document)) with
        { Nudges = document.Nudges.ToArray(), Paused = document.Paused, UseImportedHistory = document.UseImportedHistory };
    }

    internal Task RefreshProfileAsync(Guid user, CancellationToken token = default)
    {
        requestedProfiles.AddOrUpdate(user, 1, (_, version) => version + 1);
        var pending = refreshingProfiles.GetOrAdd(user, id => new Lazy<Task>(() => Task.Run(async () =>
        {
            long completed = 0;
            try
            {
                do { completed = requestedProfiles[id]; await GetAsync(id); }
                while (completed != requestedProfiles[id]);
            }
            catch (Exception error) { logger?.LogDebug("Taste refresh skipped a listener ({ErrorType})", error.GetType().Name); }
            finally
            {
                refreshingProfiles.TryRemove(id, out _);
                if (completed != requestedProfiles[id]) _ = RefreshProfileAsync(id);
            }
        })));
        return pending.Value.WaitAsync(token);
    }

    private async Task StoreProfileAsync(Guid user, LoadedTaste computed, CancellationToken token)
    {
        var snapshot = new TasteProfileSnapshot(Profile(computed.Document), computed.Revision, computed.Plays, computed.Tags, computed.At);
        await writes.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var row = await db.TasteProfiles.SingleOrDefaultAsync(item => item.OwnerUserId == user, token);
            if ((row?.Revision ?? 0) != computed.Revision) return;
            var document = Read(row);
            document.Snapshot = snapshot;
            if (row == null) db.TasteProfiles.Add(row = new() { OwnerUserId = user });
            row.PayloadJson = JsonSerializer.Serialize(document);
            // Saving a computed view does not change the listening inputs or their revision.
            await db.SaveChangesAsync(token);
        }
        catch (DbUpdateConcurrencyException) { }
        finally { writes.Release(); }
    }
}

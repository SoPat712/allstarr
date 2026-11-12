using allstarr.Core.Storage;

namespace allstarr.Core.Matching;

public sealed partial class TrackIdentityService
{
    internal static (Guid Old, Guid New)[] PendingRenames(AllstarrDbContext db)
    {
        db.ChangeTracker.DetectChanges();
        return db.ChangeTracker.Entries<CanonicalRecordingRecord>()
            .Where(entry => entry.Entity.MergedIntoId != null &&
                entry.Property(item => item.MergedIntoId).OriginalValue != entry.Entity.MergedIntoId)
            .Select(entry => (entry.Entity.Id, entry.Entity.MergedIntoId!.Value)).ToArray();
    }

    private void PublishRenames((Guid Old, Guid New)[] renames)
    {
        foreach (var (oldId, newId) in renames) _nativeEvents?.Renamed(oldId, newId);
    }
}

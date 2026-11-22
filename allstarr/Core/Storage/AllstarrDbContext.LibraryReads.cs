using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Storage;

public sealed partial class AllstarrDbContext
{
    private static readonly ConcurrentDictionary<string, long> LibraryReadVersions = new(StringComparer.Ordinal);
    private bool _libraryReadsChanged;
    internal long LibraryReadRevision => LibraryReadVersions.GetValueOrDefault(Database.GetConnectionString() ?? "");

    private bool ChangesLibraryReads() => ChangeTracker.Entries<LibraryTrackRecord>().Any(entry =>
        entry.State is EntityState.Added or EntityState.Deleted || entry.State == EntityState.Modified && !OnlyIndexedAgain(entry));

    // Every index pass touches every song; only a pass that finds a song gone, or back again, changes what is read.
    internal static bool OnlyIndexedAgain(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry)
    {
        if (entry.Properties.Any(property => property.IsModified && property.Metadata.Name is not
                (nameof(LibraryTrackRecord.IndexedAt) or nameof(LibraryTrackRecord.UpdatedAt) or
                 nameof(LibraryTrackRecord.DurationRetrievedAt) or nameof(LibraryTrackRecord.Revision)))) return false;
        var indexed = entry.Property(nameof(LibraryTrackRecord.IndexedAt));
        return !indexed.IsModified || LibraryTrackRecord.IsGone((DateTimeOffset)indexed.OriginalValue!) ==
            LibraryTrackRecord.IsGone((DateTimeOffset)indexed.CurrentValue!);
    }

    private void LibraryReadsSaved(bool changed)
    {
        if (!changed) return;
        if (Database.CurrentTransaction != null) _libraryReadsChanged = true;
        else InvalidateLibraryReads();
    }

    private void InvalidateLibraryReads() => LibraryReadVersions.AddOrUpdate(Database.GetConnectionString() ?? "", 1, (_, revision) => revision + 1);
}

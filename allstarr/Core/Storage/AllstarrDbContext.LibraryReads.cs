using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Storage;

public sealed partial class AllstarrDbContext
{
    private static readonly ConcurrentDictionary<string, long> LibraryReadVersions = new(StringComparer.Ordinal);
    private bool _libraryReadsChanged;
    internal long LibraryReadRevision => LibraryReadVersions.GetValueOrDefault(Database.GetConnectionString() ?? "");

    private bool ChangesLibraryReads() => ChangeTracker.Entries<LibraryTrackRecord>().Any(entry =>
        entry.State is EntityState.Added or EntityState.Deleted || entry.State == EntityState.Modified &&
        entry.Properties.Any(property => property.IsModified && property.Metadata.Name is not
            (nameof(LibraryTrackRecord.IndexedAt) or nameof(LibraryTrackRecord.UpdatedAt) or
             nameof(LibraryTrackRecord.DurationRetrievedAt) or nameof(LibraryTrackRecord.Revision))));

    private void LibraryReadsSaved(bool changed)
    {
        if (!changed) return;
        if (Database.CurrentTransaction != null) _libraryReadsChanged = true;
        else InvalidateLibraryReads();
    }

    private void InvalidateLibraryReads() => LibraryReadVersions.AddOrUpdate(Database.GetConnectionString() ?? "", 1, (_, revision) => revision + 1);
}

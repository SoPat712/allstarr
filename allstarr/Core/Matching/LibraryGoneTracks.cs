using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Matching;

/// <summary>
/// Notices library songs the media server no longer has. An index pass only adds and updates what it lists, so a song
/// deleted from the media server stayed in the index, kept counting as in the library and kept being chosen as the
/// local copy. After each pass the songs it did not list are asked about, and a song that fails to play is asked about
/// at once. Only the server's own word marks a song gone.
/// </summary>
public sealed class LibraryGoneTracks(IDbContextFactory<AllstarrDbContext> factory, BackendLibraryCatalogScannerResolver scanners,
    IBackendLibraryAccessResolver libraryAccess, IPlatformClock clock, ILogger<LibraryGoneTracks>? logger = null)
{
    /// <summary>After an index pass that started at <paramref name="started"/>: the songs of the libraries it listed that it
    /// did not see are asked about, a batch at a time.</summary>
    public async Task<int> SweepAsync(ProtocolExecutionContext context, DateTimeOffset started, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var tracks = LibraryTrackAccess.Query(db, context, await libraryAccess.ResolveAsync(context, token));
        var listed = await tracks.Where(item => item.IndexedAt >= started).Select(item => item.BackendLibraryId).Distinct().ToArrayAsync(token);
        var unseen = await tracks.Where(item => listed.Contains(item.BackendLibraryId) && item.IndexedAt < started)
            .Select(item => new { item.Id, item.BackendLibraryId, item.BackendItemId }).ToArrayAsync(token);
        var marked = 0;
        foreach (var library in unseen.GroupBy(item => item.BackendLibraryId))
        {
            var gone = await scanners.Resolve(context.Protocol).GoneAsync(context, library.Key,
                library.Select(item => item.BackendItemId).ToArray(), token);
            marked += await MarkAsync(library.Where(item => gone.Contains(item.BackendItemId)).Select(item => item.Id).ToArray(), started, token);
        }
        if (marked > 0) logger?.LogInformation("{Count} library songs the media server no longer has are no longer counted as in the library", marked);
        return marked;
    }

    /// <summary>A library song that failed to play: one media-server read says whether it is still there. True when it is
    /// gone, and from then on it is not taken for a song in the library.</summary>
    public async Task<bool> CheckAsync(ProtocolExecutionContext context, string backendItemId, CancellationToken token)
    {
        try
        {
            if (context.Principal == null || string.IsNullOrWhiteSpace(backendItemId)) return false;
            // Jellyfin writes one id with or without dashes.
            var ids = Guid.TryParse(backendItemId, out var parsed) ? new[] { backendItemId, parsed.ToString("N"), parsed.ToString("D") }.Distinct().ToArray() : [backendItemId];
            await using var db = await factory.CreateDbContextAsync(token);
            var rows = await LibraryTrackAccess.Query(db, context, await libraryAccess.ResolveAsync(context, token))
                .Where(item => ids.Contains(item.BackendItemId)).Select(item => new { item.Id, item.BackendLibraryId, item.BackendItemId }).ToArrayAsync(token);
            var marked = 0;
            foreach (var row in rows)
                if ((await scanners.Resolve(context.Protocol).GoneAsync(context, row.BackendLibraryId, [row.BackendItemId], token)).Contains(row.BackendItemId))
                    marked += await MarkAsync([row.Id], clock.UtcNow, token);
            if (marked > 0) logger?.LogInformation("A library song that failed to play is gone from the media server; it plays from a service instead");
            return marked > 0;
        }
        catch (Exception error) when (error is HttpRequestException or NotSupportedException or System.Text.Json.JsonException or DbUpdateException ||
            error is OperationCanceledException && !token.IsCancellationRequested)
        {
            logger?.LogDebug("A library song that failed to play could not be checked ({ErrorType})", error.GetType().Name);
            return false;
        }
    }

    // Only a song not indexed again since the server was asked is marked: one listed again in the meantime is there.
    private async Task<int> MarkAsync(IReadOnlyCollection<Guid> ids, DateTimeOffset asked, CancellationToken token)
    {
        if (ids.Count == 0) return 0;
        await using var db = await factory.CreateDbContextAsync(token);
        var rows = await db.LibraryTracks.Where(item => ids.Contains(item.Id)).ToListAsync(token);
        var now = clock.UtcNow;
        var marked = 0;
        foreach (var row in rows.Where(item => !LibraryTrackRecord.IsGone(item.IndexedAt) && item.IndexedAt <= asked))
        {
            row.IndexedAt = LibraryTrackRecord.Gone;
            row.UpdatedAt = now;
            row.Revision++;
            marked++;
        }
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateConcurrencyException) { return 0; }
        return marked;
    }
}

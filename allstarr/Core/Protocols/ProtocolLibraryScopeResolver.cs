using System.Text.Json;
using allstarr.Core.Storage;
using allstarr.Core.Matching;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Protocols;

public interface IProtocolLibraryScopeResolver
{
    Task<ProtocolExecutionContext> ResolveAsync(
        ProtocolExecutionContext context,
        string itemId,
        CancellationToken cancellationToken = default);
}

public sealed class ProtocolLibraryScopeResolver(
    IDbContextFactory<AllstarrDbContext> factory,
    IBackendLibraryAccessResolver libraryAccess)
    : IProtocolLibraryScopeResolver
{
    public async Task<ProtocolExecutionContext> ResolveAsync(
        ProtocolExecutionContext context,
        string itemId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        _ = context.RequireActor();
        if (string.IsNullOrWhiteSpace(itemId)) throw new ArgumentException("A library item is required.", nameof(itemId));
        var access = await libraryAccess.ResolveAsync(context, cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var tracks = await LibraryTrackAccess.Query(db, context, access).ToListAsync(cancellationToken);
        var library = tracks.Where(track => Matches(track, itemId))
            .Select(track => track.LibraryScopeId)
            .Order(StringComparer.Ordinal)
            .FirstOrDefault() ?? (access.Succeeded ? access.LibraryIds.Order(StringComparer.Ordinal).FirstOrDefault() : null);
        // Playback and favorite jobs also describe external tracks without a backend library.
        // Their bookkeeping scope does not authorize access to indexed items.
        return context.WithLibraryScope(library ?? "music");
    }

    internal static bool Matches(LibraryTrackRecord track, string itemId)
    {
        if (track.BackendItemId.Equals(itemId, StringComparison.Ordinal) ||
            track.Id.ToString("D").Equals(itemId, StringComparison.OrdinalIgnoreCase) ||
            track.CanonicalRecordingId?.ToString("D").Equals(itemId, StringComparison.OrdinalIgnoreCase) == true)
            return true;
        try
        {
            var providers = JsonSerializer.Deserialize<Dictionary<string, string>>(track.ProviderIdsJson);
            return providers?.Any(pair =>
                pair.Value.Equals(itemId, StringComparison.Ordinal) ||
                $"{pair.Key}:{pair.Value}".Equals(itemId, StringComparison.OrdinalIgnoreCase) ||
                $"external:{pair.Key}:{pair.Value}".Equals(itemId, StringComparison.OrdinalIgnoreCase) ||
                $"ext-{pair.Key}-song-{pair.Value}".Equals(itemId, StringComparison.OrdinalIgnoreCase) ||
                $"ext-{pair.Key}-{pair.Value}".Equals(itemId, StringComparison.OrdinalIgnoreCase)) == true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

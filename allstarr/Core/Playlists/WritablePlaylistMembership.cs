using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Services.Local;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Playlists;

public sealed record WritablePlaylistMember(string OccurrenceId, string ItemId, Song? Song = null,
    string? NativeEntryId = null)
{
    public bool IsProvider => Song != null;
    [System.Text.Json.Serialization.JsonIgnore]
    public int? NativePosition { get; init; }
}

public sealed record WritablePlaylistState(IReadOnlyList<WritablePlaylistMember> Members);

public sealed class WritablePlaylistMembership(
    IDbContextFactory<AllstarrDbContext> factory,
    IProtocolProviderGateway providers,
    ILibraryIndexService library)
{
    private const string ResourceKind = "playlist-membership";
    private readonly SemaphoreSlim _writer = new(1, 1);

    public async Task<IDisposable> LockAsync(CancellationToken cancellationToken)
    {
        await _writer.WaitAsync(cancellationToken);
        return new WriterLease(_writer);
    }

    public async Task<WritablePlaylistState?> ReadAsync(ProtocolExecutionContext context, string playlistId,
        CancellationToken cancellationToken)
    {
        if (context.Actor == null) return null;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var hash = Key(context, playlistId);
        var row = await db.ExternalMetadataSnapshots.AsNoTracking().Where(item =>
                item.ResourceKind == ResourceKind && item.ProviderId == "allstarr" && item.ExternalIdHash == hash &&
                item.Protocol == context.Protocol.ToString().ToLowerInvariant() && item.BackendInstanceId == context.BackendInstanceId)
            .OrderByDescending(item => item.SnapshotVersion).FirstOrDefaultAsync(cancellationToken);
        return row == null ? null : JsonSerializer.Deserialize<WritablePlaylistState>(row.PayloadJson);
    }

    public async Task SaveAsync(ProtocolExecutionContext context, string playlistId,
        IReadOnlyList<WritablePlaylistMember> members, CancellationToken cancellationToken)
    {
        var owner = context.RequireActor().EffectiveUserId ?? throw new UnauthorizedAccessException();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var hash = Key(context, playlistId);
        var version = await db.ExternalMetadataSnapshots.Where(item => item.ResourceKind == ResourceKind &&
                item.ProviderId == "allstarr" && item.ExternalIdHash == hash)
            .Select(item => (int?)item.SnapshotVersion).MaxAsync(cancellationToken) ?? 0;
        var payload = JsonSerializer.Serialize(new WritablePlaylistState(members));
        db.ExternalMetadataSnapshots.Add(new()
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = owner,
            BackendInstanceId = context.BackendInstanceId,
            BackendPrincipalId = context.VerifiedBackendPrincipalId,
            Protocol = context.Protocol.ToString().ToLowerInvariant(),
            ProviderId = "allstarr",
            ResourceKind = ResourceKind,
            ExternalIdHash = hash,
            SnapshotVersion = version + 1,
            ProviderRevision = (version + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
            PayloadJson = payload,
            PayloadSha256 = Hash(payload),
            CorrelationId = context.CorrelationId,
            RetrievedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<WritablePlaylistMember>> ResolveAsync(ProtocolExecutionContext context,
        IReadOnlyList<string> itemIds, CancellationToken cancellationToken)
    {
        var result = new List<WritablePlaylistMember>();
        var resolved = new Dictionary<string, (string Id, Song? Song)>(StringComparer.Ordinal);
        foreach (var id in itemIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!resolved.TryGetValue(id, out var value))
            {
                var parsed = LocalLibraryService.ParseExternalResource(id);
                if (!parsed.isExternal)
                {
                    if (id.StartsWith("ext-", StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("Invalid playlist song identifier.");
                    value = (id, null);
                }
                else
                {
                    _ = context.RequireActor();
                    if (parsed.type != "song") throw new ArgumentException("Playlists require song identifiers.");
                    var nativeId = await library.ResolveKnownTrackAsync(context, id, cancellationToken);
                    if (nativeId != null) value = (nativeId, null);
                    else
                    {
                        var song = await providers.GetSongAsync(context, parsed.provider!, parsed.externalId!)
                            ?? throw new ArgumentException("A playlist song is unavailable.");
                        // Membership retains metadata and stable identities, never expiring media URLs.
                        song = JsonSerializer.Deserialize<Song>(JsonSerializer.Serialize(song))!;
                        song.Id = id;
                        song.CoverArtUrl = null;
                        song.CoverArtUrlLarge = null;
                        song.LocalPath = null;
                        song.JellyfinMetadata = null;
                        value = (id, song);
                    }
                }
                resolved.Add(id, value);
            }
            result.Add(new(Guid.CreateVersion7().ToString("N"), value.Id, value.Song));
        }
        return result;
    }

    public static IReadOnlyList<WritablePlaylistMember> PreserveOccurrences(IReadOnlyList<WritablePlaylistMember> current,
        IReadOnlyList<WritablePlaylistMember> requested)
    {
        var previous = current.GroupBy(member => (member.ItemId, member.IsProvider))
            .ToDictionary(group => group.Key, group => new Queue<WritablePlaylistMember>(group));
        return requested.Select(member => previous.TryGetValue((member.ItemId, member.IsProvider), out var copies) &&
            copies.TryDequeue(out var existing)
            ? member with { OccurrenceId = existing.OccurrenceId, NativeEntryId = existing.NativeEntryId }
            : member).ToArray();
    }

    public static IReadOnlyList<WritablePlaylistMember> Reconcile(WritablePlaylistState? saved,
        IReadOnlyList<WritablePlaylistMember> native)
    {
        if (saved == null) return native;
        var remaining = native.ToList();
        var result = new List<WritablePlaylistMember>();
        foreach (var member in saved.Members)
        {
            if (member.IsProvider) { result.Add(member); continue; }
            var index = member.NativeEntryId == null ? -1 : remaining.FindIndex(item => item.NativeEntryId == member.NativeEntryId);
            if (index < 0) index = remaining.FindIndex(item => SameNativeId(item.ItemId, member.ItemId));
            if (index < 0) continue;
            var current = remaining[index];
            remaining.RemoveAt(index);
            result.Add(current with { OccurrenceId = current.NativeEntryId ?? member.OccurrenceId });
        }
        var used = result.Select(member => member.OccurrenceId).ToHashSet(StringComparer.Ordinal);
        foreach (var member in remaining)
        {
            var occurrence = member.OccurrenceId;
            while (!used.Add(occurrence)) occurrence += "-untracked";
            result.Add(member with { OccurrenceId = occurrence });
        }
        return result;
    }

    public static bool HasProviderIds(IEnumerable<string> ids) => ids.Any(id => id.StartsWith("ext-", StringComparison.OrdinalIgnoreCase));
    private static bool SameNativeId(string left, string right) => left == right ||
        Guid.TryParse(left, out var leftId) && Guid.TryParse(right, out var rightId) && leftId == rightId;
    internal static string Key(ProtocolExecutionContext context, string id)
    {
        if (context.Protocol == ProtocolKind.Jellyfin && Guid.TryParse(id, out var guid)) id = guid.ToString("N");
        return Hash($"{context.Protocol}\n{context.BackendInstanceId}\n{id}");
    }
    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private sealed class WriterLease(SemaphoreSlim writer) : IDisposable
    {
        public void Dispose() => writer.Release();
    }
}

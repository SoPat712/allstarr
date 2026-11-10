using System.Text.Json;
using System.Text.Json.Serialization;
using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Playback;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Services.Local;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.ClientApi;

public sealed record ClientHistoryPage(IReadOnlyList<ClientHistoryPlay> Plays,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Next);
public sealed record ClientHistoryPlay(string? ItemId, string Title, string Artist, string? Album,
    long? DurationMs, string? ArtworkUrl, DateTimeOffset PlayedAt, bool IsExternal, string? Provider);

public sealed class ClientListeningHistory(IDbContextFactory<AllstarrDbContext> factory, ILibraryIndexService library,
    IBackendLibraryAccessResolver libraryAccess, IEffectiveProviderPolicyResolver policies, IProtocolProviderGateway providers)
{
    private sealed record Cursor(DateTimeOffset At, Guid Id);

    public async Task<ClientHistoryPage> ReadAsync(ProtocolExecutionContext execution, int limit, string? before)
    {
        if (limit is < 1 or > 200) throw new ArgumentException("Invalid history limit.");
        Cursor? cursor = null;
        if (before != null)
        {
            try { cursor = before.Length > 512 ? null : JsonSerializer.Deserialize<Cursor>(WebEncoders.Base64UrlDecode(before)); }
            catch (Exception error) when (error is FormatException or JsonException or ArgumentException) { throw new ArgumentException("Invalid history cursor."); }
            if (cursor == null) throw new ArgumentException("Invalid history cursor.");
        }
        var token = execution.CancellationToken;
        var owner = execution.RequireActor().EffectiveUserId!.Value;
        var policy = execution.Policy ?? await policies.ResolveForUserAsync(owner, token);
        await using var db = await factory.CreateDbContextAsync(token);
        var query = ListeningStatsService.CountedPlays(db, new(owner, execution.Protocol.ToString().ToLowerInvariant(), execution.BackendInstanceId));
        if (policy.Preferences.ExplicitFilter == "CleanOnly") query = query.Where(item => db.CanonicalRecordings.Any(recording =>
            recording.Id == item.CanonicalRecordingId && recording.IsExplicit == false));
        if (cursor != null) query = query.Where(item => (item.ListenedAt ?? item.StartedAt) < cursor.At ||
            (item.ListenedAt ?? item.StartedAt) == cursor.At && item.Id.CompareTo(cursor.Id) < 0);
        var rows = await query.OrderByDescending(item => item.ListenedAt ?? item.StartedAt).ThenByDescending(item => item.Id)
            .Take(limit + 1).ToListAsync(token);
        var more = rows.Count > limit;
        if (more) rows.RemoveAt(rows.Count - 1);
        var ids = rows.Select(item => item.CanonicalRecordingId).OfType<Guid>().Distinct().ToArray();
        var recordings = await db.CanonicalRecordings.AsNoTracking().Where(item => ids.Contains(item.Id) && item.MergedIntoId == null)
            .ToDictionaryAsync(item => item.Id, token);
        var playable = ids.Length == 0 ? [] : await providers.GetPlayableProviderOrderAsync(execution.RequireActor(), token);
        var links = (await db.ProviderTrackIdentities.AsNoTracking().Where(item => ids.Contains(item.CanonicalRecordingId) &&
            item.ResourceKind == ProviderResourceKind.Track && (item.Scope == ProviderIdentityScope.Catalog ||
                db.ProviderAccounts.Any(account => account.Id == item.ProviderAccountId && account.Enabled &&
                    (account.OwnerUserId == null || account.OwnerUserId == owner)))).ToArrayAsync(token))
            .Where(CatalogIdentityPolicy.IsPlaybackEligible).Where(item => playable.Contains(item.ProviderId))
            .Select(item => item.CanonicalRecordingId).ToHashSet();
        var lookups = rows.Select(item =>
        {
            var parsed = LocalLibraryService.ParseExternalResource(item.TrackReference);
            return new LibraryRecordingLookup(item.Id.ToString("N"), item.CanonicalRecordingId, item.Isrc, parsed.provider, parsed.externalId);
        }).ToArray();
        var native = await library.FindKnownRecordingTracksAsync(execution, lookups, token);
        var trackIds = rows.Select(item => item.LibraryTrackId).OfType<Guid>().ToArray();
        // Imports can point directly at an indexed library track before a recording identity is available.
        var access = trackIds.Length == 0 ? new Dictionary<Guid, LibraryTrackRecord>() :
            await LibraryTrackAccess.Query(db, execution, await libraryAccess.ResolveAsync(execution, token)).Where(item => trackIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, token);
        var plays = rows.Select(item =>
        {
            var parsed = LocalLibraryService.ParseExternalResource(item.TrackReference);
            var local = native.GetValueOrDefault(item.Id.ToString("N"));
            local ??= item.LibraryTrackId is { } track ? access.GetValueOrDefault(track)?.BackendItemId : null;
            if (local == null && item.SourceKind == "protocol" && !parsed.isExternal && !string.IsNullOrWhiteSpace(item.TrackReference))
                local = item.TrackReference;
            var id = local ?? (item.CanonicalRecordingId is { } recording && links.Contains(recording)
                ? recordings.GetValueOrDefault(recording)?.PublicId : null);
            var external = local == null;
            return new ClientHistoryPlay(id, item.Title ?? "", item.Artist ?? "", item.Album, item.DurationMilliseconds,
                string.IsNullOrWhiteSpace(item.Artist) ? null : ClientListeningStats.Artwork(item.Artist, title: item.Title),
                (item.ListenedAt ?? item.StartedAt)!.Value, external, external ? item.ProviderId ?? parsed.provider : null);
        }).ToArray();
        var last = rows.LastOrDefault();
        return new(plays, more && last != null ? WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(
            new Cursor((last.ListenedAt ?? last.StartedAt)!.Value, last.Id))) : null);
    }
}

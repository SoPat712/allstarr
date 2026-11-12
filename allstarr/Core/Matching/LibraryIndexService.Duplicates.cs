using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Matching;

internal sealed record LibraryIndexDuplicateSummary(int GroupsConsolidated, int RowsRemoved, int GroupsDeferred);

public sealed partial class LibraryIndexService
{
    private const string JellyfinProtocol = "jellyfin";
    private const int MaxMaintenanceGroups = 50;

    private static Guid DeterministicJellyfinTrackId(string backendInstanceId, string backendItemId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            JellyfinProtocol + "\0" + backendInstanceId + "\0" + backendItemId));
        return new Guid(bytes.AsSpan(0, 16));
    }

    // A fresh catalog response can confirm a move between libraries; maintenance cannot.
    internal async Task<bool> PrepareJellyfinUpsertAsync(
        string backendInstanceId,
        string backendItemId,
        string backendLibraryId,
        Guid? authoritativeCanonicalRecordingId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var rows = await db.LibraryTracks
            .Where(item => item.Protocol == JellyfinProtocol &&
                item.BackendInstanceId == backendInstanceId && item.BackendItemId == backendItemId)
            .OrderBy(item => item.Id)
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }
        if (rows.Count == 1 && rows[0].BackendLibraryId == backendLibraryId)
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        var survivor = rows.FirstOrDefault(item => item.BackendLibraryId == backendLibraryId) ?? rows[0];
        var losers = rows.Where(item => item.Id != survivor.Id).ToArray();
        try
        {
            if (losers.Length > 0 && !await RepointReferencesAsync(db, survivor, losers, cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                throw new InvalidOperationException("The indexed item's saved references could not be consolidated safely.");
            }

            var canonicalConflict = ConflictingCanonicalRecordings(rows);
            MergeMetadata(survivor, rows);
            if (canonicalConflict)
            {
                survivor.CanonicalRecordingId = authoritativeCanonicalRecordingId;
                survivor.AcceptedDecisionVersion = null;
            }
            survivor.BackendLibraryId = backendLibraryId;
            foreach (var loser in losers)
            {
                db.LibraryTracks.Remove(loser);
                db.AuditEvents.Add(ConsolidationAudit(loser, survivor, correlationId));
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    internal async Task<LibraryIndexDuplicateSummary> ConsolidateJellyfinDuplicatesAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureStorageReady();
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var keys = await db.LibraryTracks.AsNoTracking()
            .Where(item => item.Protocol == JellyfinProtocol)
            .GroupBy(item => new { item.BackendInstanceId, item.BackendLibraryId, item.BackendItemId })
            .Where(group => group.Count() > 1)
            .OrderBy(group => group.Key.BackendInstanceId).ThenBy(group => group.Key.BackendLibraryId).ThenBy(group => group.Key.BackendItemId)
            .Select(group => new DuplicateKey(group.Key.BackendInstanceId, group.Key.BackendLibraryId, group.Key.BackendItemId))
            .ToListAsync(cancellationToken);
        var deferredLibraryGroups = await db.LibraryTracks.AsNoTracking()
            .Where(item => item.Protocol == JellyfinProtocol)
            .GroupBy(item => new { item.BackendInstanceId, item.BackendItemId })
            .Where(group => group.Select(item => item.BackendLibraryId).Distinct().Count() > 1)
            .CountAsync(cancellationToken);

        var consolidated = 0;
        var removed = 0;
        var deferred = 0;
        // Rotate the bounded work so a group awaiting review cannot hold up the rest of the index.
        var rotation = Math.Abs((_clock.UtcNow - DateTimeOffset.UnixEpoch).Ticks / LocalLibraryAvailability.RefreshInterval.Ticks);
        var offset = keys.Count == 0 ? 0 : (int)(rotation * MaxMaintenanceGroups % keys.Count);
        foreach (var key in keys.Skip(offset).Concat(keys.Take(offset)).Take(MaxMaintenanceGroups))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var rows = await db.LibraryTracks
                .Where(item => item.Protocol == JellyfinProtocol &&
                    item.BackendInstanceId == key.BackendInstanceId &&
                    item.BackendLibraryId == key.BackendLibraryId &&
                    item.BackendItemId == key.BackendItemId)
                .OrderBy(item => item.Id)
                .ToListAsync(cancellationToken);
            if (rows.Count < 2)
            {
                await transaction.CommitAsync(cancellationToken);
                continue;
            }

            try
            {
                if (ConflictingCanonicalRecordings(rows) ||
                    !await RepointReferencesAsync(db, rows[0], rows.Skip(1).ToArray(), cancellationToken))
                {
                    deferred++;
                    await transaction.RollbackAsync(cancellationToken);
                    db.ChangeTracker.Clear();
                    continue;
                }

                var survivor = rows[0];
                MergeMetadata(survivor, rows);
                foreach (var loser in rows.Skip(1))
                {
                    db.LibraryTracks.Remove(loser);
                    db.AuditEvents.Add(ConsolidationAudit(loser, survivor, "library-index-maintenance"));
                }

                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                db.ChangeTracker.Clear();
                consolidated++;
                removed += rows.Count - 1;
            }
            catch (DbUpdateException)
            {
                deferred++;
                await transaction.RollbackAsync(cancellationToken);
                db.ChangeTracker.Clear();
            }
        }

        deferred += deferredLibraryGroups;
        if (keys.Count > 0 || deferredLibraryGroups > 0)
        {
            db.ChangeTracker.Clear();
            db.AuditEvents.Add(new AuditEventRecord
            {
                Id = Guid.CreateVersion7(),
                Category = "library-index",
                Action = "duplicates.reconciled",
                Outcome = "succeeded",
                CorrelationId = "library-index-maintenance",
                DetailsJson = JsonSerializer.Serialize(new
                {
                    protocol = JellyfinProtocol,
                    groups = keys.Count,
                    groupsConsolidated = consolidated,
                    rowsRemoved = removed,
                    groupsDeferred = deferred,
                    groupsPending = Math.Max(0, keys.Count - MaxMaintenanceGroups)
                }),
                CreatedAt = _clock.UtcNow
            });
            await db.SaveChangesAsync(cancellationToken);
        }

        return new(consolidated, removed, deferred);
    }

    private async Task<bool> RepointReferencesAsync(
        AllstarrDbContext db,
        LibraryTrackRecord survivor,
        IReadOnlyCollection<LibraryTrackRecord> losers,
        CancellationToken cancellationToken)
    {
        foreach (var loser in losers)
        {
            await db.TrackMatches.Where(item => item.LibraryTrackId == loser.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.LibraryTrackId, survivor.Id), cancellationToken);
            await db.ManualTrackOverrides.Where(item => item.LibraryTrackId == loser.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.LibraryTrackId, survivor.Id), cancellationToken);
            await db.PlaylistSyncEntryResults.Where(item => item.LibraryTrackId == loser.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.LibraryTrackId, survivor.Id), cancellationToken);
            await db.PlaylistTargetMemberships.Where(item => item.LibraryTrackId == loser.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.LibraryTrackId, survivor.Id), cancellationToken);
            await db.ListeningEvents.Where(item => item.LibraryTrackId == loser.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.LibraryTrackId, survivor.Id), cancellationToken);

            if (!await RewriteOperationalJsonAsync(db, loser.Id, survivor.Id, cancellationToken))
                return false;
        }

        return true;
    }

    private static async Task<bool> RewriteOperationalJsonAsync(
        AllstarrDbContext db,
        Guid oldId,
        Guid newId,
        CancellationToken cancellationToken)
    {
        var oldText = oldId.ToString("D");
        var matches = await db.TrackMatches.Where(item => item.CandidateResultsJson.ToLower().Contains(oldText)).ToListAsync(cancellationToken);
        foreach (var item in matches)
            if (!RewriteJson(item.CandidateResultsJson, oldId, newId, out var rewritten, deduplicateCandidates: true)) return false;
            else item.CandidateResultsJson = rewritten;

        var reports = await db.ProblemReports.Where(item => item.ContextJson.ToLower().Contains(oldText)).ToListAsync(cancellationToken);
        foreach (var item in reports)
            if (!RewriteJson(item.ContextJson, oldId, newId, out var rewritten)) return false;
            else item.ContextJson = rewritten;

        var recommendations = await db.RecommendationCandidates.Where(item => item.IdentityJson.ToLower().Contains(oldText)).ToListAsync(cancellationToken);
        foreach (var item in recommendations)
            if (!RewriteJson(item.IdentityJson, oldId, newId, out var rewritten)) return false;
            else item.IdentityJson = rewritten;

        var generated = await db.GeneratedSetEntries.Where(item => item.IdentityJson.ToLower().Contains(oldText)).ToListAsync(cancellationToken);
        foreach (var item in generated)
            if (!RewriteJson(item.IdentityJson, oldId, newId, out var rewritten)) return false;
            else item.IdentityJson = rewritten;

        var playlistResults = await db.PlaylistSyncEntryResults.Where(item => item.DetailsJson.ToLower().Contains(oldText)).ToListAsync(cancellationToken);
        foreach (var item in playlistResults)
            if (!RewriteJson(item.DetailsJson, oldId, newId, out var rewritten)) return false;
            else item.DetailsJson = rewritten;

        return true;
    }

    private static bool RewriteJson(string json, Guid oldId, Guid newId, out string rewritten,
        bool deduplicateCandidates = false)
    {
        rewritten = json;
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException) { return false; }
        if (root == null) return true;
        var changed = RewriteNode(root, oldId, newId, false);
        if (deduplicateCandidates) changed |= DeduplicateCandidateIds(root, newId);
        if (changed) rewritten = root.ToJsonString();
        return !rewritten.Contains(oldId.ToString("D"), StringComparison.OrdinalIgnoreCase);
    }

    private static bool DeduplicateCandidateIds(JsonNode root, Guid consolidatedId)
    {
        if (root is not JsonArray candidates) return false;
        var seen = new HashSet<Guid>();
        var changed = false;
        for (var index = 0; index < candidates.Count;)
        {
            if (candidates[index] is not JsonObject candidate)
            {
                index++;
                continue;
            }
            var hasId = candidate.TryGetPropertyValue("libraryTrackId", out var idNode) ||
                candidate.TryGetPropertyValue("LibraryTrackId", out idNode);
            if (!hasId || idNode is not JsonValue idValue ||
                !idValue.TryGetValue<string>(out var text) || !Guid.TryParse(text, out var id) || id != consolidatedId)
            {
                index++;
                continue;
            }
            if (!seen.Add(id))
            {
                candidates.RemoveAt(index);
                changed = true;
                continue;
            }
            index++;
        }
        return changed;
    }

    private static bool RewriteNode(JsonNode node, Guid oldId, Guid newId, bool idValue)
    {
        var changed = false;
        if (idValue && node is JsonValue value && value.TryGetValue<string>(out var text) &&
            Guid.TryParse(text, out var parsed) && parsed == oldId)
        {
            // The parent replaces this value; this branch is handled by RewriteChild.
            return true;
        }

        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToArray())
            {
                var relevant = property.Key.Equals("libraryTrackId", StringComparison.OrdinalIgnoreCase) ||
                    property.Key.Equals("libraryTrackIds", StringComparison.OrdinalIgnoreCase);
                changed |= RewriteChild(obj, property.Key, property.Value, oldId, newId, relevant);
            }
        }
        else if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
                if (array[index] != null) changed |= RewriteNode(array[index]!, oldId, newId, idValue);
        }

        return changed;
    }

    private static bool RewriteChild(JsonObject parent, string key, JsonNode? value, Guid oldId, Guid newId, bool relevant)
    {
        if (value == null) return false;
        if (relevant && value is JsonValue scalar && scalar.TryGetValue<string>(out var text) &&
            Guid.TryParse(text, out var parsed) && parsed == oldId)
        {
            parent[key] = JsonValue.Create(newId.ToString("D"));
            return true;
        }

        var changed = RewriteNode(value, oldId, newId, relevant);
        if (relevant && value is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
            {
                if (array[index] is JsonValue item && item.TryGetValue<string>(out var itemText) &&
                    Guid.TryParse(itemText, out var itemId) && itemId == oldId)
                {
                    array[index] = JsonValue.Create(newId.ToString("D"));
                    changed = true;
                }
            }
        }
        return changed;
    }

    private static bool ConflictingCanonicalRecordings(IReadOnlyCollection<LibraryTrackRecord> rows) =>
        rows.Select(item => item.CanonicalRecordingId).OfType<Guid>().Distinct().Count() > 1;

    private static void MergeMetadata(LibraryTrackRecord survivor, IReadOnlyCollection<LibraryTrackRecord> rows)
    {
        var latest = rows.OrderByDescending(item => item.IndexedAt).ThenByDescending(item => item.Id).First();
        if (latest.Id != survivor.Id)
        {
            survivor.FilePath = latest.FilePath;
            survivor.Title = latest.Title;
            survivor.Artist = latest.Artist;
            survivor.Album = latest.Album;
            survivor.AlbumArtist = latest.AlbumArtist;
            survivor.DurationMilliseconds = latest.DurationMilliseconds;
            survivor.DurationProvenance = latest.DurationProvenance;
            survivor.DurationRetrievedAt = latest.DurationRetrievedAt;
            survivor.Isrc = latest.Isrc;
            survivor.MusicBrainzRecordingId = latest.MusicBrainzRecordingId;
            survivor.MusicBrainzReleaseId = latest.MusicBrainzReleaseId;
            survivor.MusicBrainzArtistId = latest.MusicBrainzArtistId;
            survivor.CoverArtReference = latest.CoverArtReference;
            survivor.SourceModifiedAt = latest.SourceModifiedAt;
        }

        survivor.CanonicalRecordingId ??= rows.FirstOrDefault(item => item.CanonicalRecordingId.HasValue)?.CanonicalRecordingId;
        var version = rows.Max(item => item.AcceptedDecisionVersion ?? 0);
        survivor.AcceptedDecisionVersion = version > 0 ? version : null;
        survivor.ProviderIdsJson = JsonSerializer.Serialize(
            rows.OrderBy(item => item.IndexedAt).ThenBy(item => item.Id)
                .SelectMany(item => ParseProviderIds(item.ProviderIdsJson))
                .GroupBy(item => item.Key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal),
            JsonOptions);
        survivor.UpdatedAt = rows.Max(item => item.UpdatedAt);
        survivor.IndexedAt = rows.Max(item => item.IndexedAt);
        survivor.Revision++;
    }

    private AuditEventRecord ConsolidationAudit(
        LibraryTrackRecord loser,
        LibraryTrackRecord survivor,
        string correlationId) => new()
        {
            Id = Guid.CreateVersion7(),
            ActorUserId = survivor.OwnerUserId,
            Category = "library-index",
            Action = "track.consolidated",
            Outcome = "succeeded",
            CorrelationId = correlationId,
            SubjectType = "library-track",
            SubjectId = survivor.Id.ToString("D"),
            DetailsJson = JsonSerializer.Serialize(new
            {
                oldLibraryTrackId = loser.Id,
                newLibraryTrackId = survivor.Id,
                protocol = JellyfinProtocol,
                backendInstanceId = survivor.BackendInstanceId,
                backendLibraryId = survivor.BackendLibraryId,
                oldCanonicalRecordingId = loser.CanonicalRecordingId,
                newCanonicalRecordingId = survivor.CanonicalRecordingId
            }),
            CreatedAt = _clock.UtcNow
        };

    private sealed record DuplicateKey(string BackendInstanceId, string BackendLibraryId, string BackendItemId);
}

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Identity;
using allstarr.Core.Operations;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Core.Settings;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Matching;

public sealed partial class TrackMatchCommandService
{
    private async Task<ManualTrackOverrideRecord> ReplaceOverrideAsync(
        AllstarrDbContext db, ExternalMetadataSnapshotRecord snapshot, Guid? ownerId,
        ManualOverrideDecision decision, Guid? libraryTrackId, string? targetProviderId, string? targetExternalId,
        string reason, ManualAuthorityRevision? expected, CancellationToken cancellationToken)
    {
        var layer = ManualTrackOverrides.ForSource(db, snapshot).Where(item => item.OwnerUserId == ownerId);
        var active = await layer.SingleOrDefaultAsync(item => item.RevokedAt == null, cancellationToken);
        if (expected == null ? active != null : active == null || active.Id != expected.Id || active.Revision != expected.Revision)
            throw new DbUpdateConcurrencyException("The selected authority changed; refresh and try again.");
        var version = await layer.Select(item => (int?)item.DecisionVersion).MaxAsync(cancellationToken) ?? 0;
        if (active != null)
        {
            active.RevokedAt = clock.UtcNow;
            active.Revision++;
            await db.SaveChangesAsync(cancellationToken);
        }
        var record = new ManualTrackOverrideRecord
        {
            Id = Guid.CreateVersion7(),

            OwnerUserId = ownerId,
            ExternalSnapshotId = snapshot.Id,
            SourceProviderId = snapshot.ProviderId,
            SourceExternalIdHash = snapshot.ExternalIdHash,

            Decision = decision,
            LibraryTrackId = libraryTrackId,
            TargetProviderId = targetProviderId,
            TargetExternalId = targetExternalId,
            Reason = reason,
            DecisionVersion = version + 1,
            MatcherVersion = TrackMatchDecisionEngine.AlgorithmVersion,
            CreatedAt = clock.UtcNow
        };
        db.ManualTrackOverrides.Add(record);
        await db.SaveChangesAsync(cancellationToken);
        return record;
    }

    private static async Task<ProviderTrackIdentityRecord> AddSourceSnapshotIdentityAsync(
        AllstarrDbContext db,
        ProviderActorContext actor,
        ExternalMetadataSnapshotRecord snapshot,
        Guid canonicalRecordingId,
        int decisionVersion,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var identity = new ProviderTrackIdentityRecord
        {
            Id = Guid.CreateVersion7(),

            CanonicalRecordingId = canonicalRecordingId,
            ProviderAccountId = snapshot.ProviderAccountId,
            ProviderId = snapshot.ProviderId,
            ResourceKind = ProviderResourceKind.Track,
            CatalogNamespace = "default",
            Scope = ProviderIdentityScope.Account,
            ExternalId = snapshot.ExternalIdHash,
            ExternalIdHash = snapshot.ExternalIdHash,
            Verification = ProviderIdentityVerification.Verified,
            VerificationMethod = "source-snapshot-hash",
            DecisionVersion = decisionVersion,
            VerifiedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.ProviderTrackIdentities.Add(identity);
        return identity;
    }

    private static CanonicalRecordingRecord CreateProvisionalRecording(
        Guid userId, DateTimeOffset now, string? isrc = null)
    {
        var id = Guid.CreateVersion7();
        var recording = new CanonicalRecordingRecord
        {
            Id = id,
            CreatedByUserId = userId,
            PublicId = CanonicalCatalogKeys.DefaultPublicId(id),
            IsProvisional = true,
            CreatedAt = now,
            UpdatedAt = now
        };
        if (!string.IsNullOrWhiteSpace(isrc))
        {
            recording.Identifiers.Add(new RecordingIdentifierRecord
            {
                Id = Guid.CreateVersion7(),
                RecordingId = id,
                Kind = RecordingIdentifierKinds.Isrc,
                Value = isrc.Trim().Replace("-", "", StringComparison.Ordinal).ToUpperInvariant(),
                Source = "match",
                CreatedAt = now
            });
        }
        return recording;
    }

    internal static async Task<bool> TryRetargetProvisionalIdentityAsync(
        AllstarrDbContext db,
        ProviderTrackIdentityRecord identity,
        Guid targetRecordingId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (identity.CanonicalRecordingId == targetRecordingId) return true;
        if (identity.Verification != ProviderIdentityVerification.Verified ||
            identity.VerificationMethod is not ("source-snapshot" or "source-snapshot-hash"))
            return false;
        var previous = db.CanonicalRecordings.Local.FirstOrDefault(item => item.Id == identity.CanonicalRecordingId)
            ?? await db.CanonicalRecordings
                .Include(item => item.Identifiers)
                .SingleOrDefaultAsync(item => item.Id == identity.CanonicalRecordingId, cancellationToken);
        if (previous != null && !db.Entry(previous).Collection(item => item.Identifiers).IsLoaded)
            await db.Entry(previous).Collection(item => item.Identifiers).LoadAsync(cancellationToken);
        if (previous is not { IsProvisional: true } || previous.Identifiers.Count != 0)
            return false;
        identity.CanonicalRecordingId = targetRecordingId;
        identity.UpdatedAt = now;
        identity.Revision++;
        return true;
    }

    private static ProviderActorContext CatalogActor(
        TrackMatchActor actor,
        ExternalMetadataSnapshotRecord snapshot) => new(

            actor.IsAdministrator ? ProviderActorKind.Administrator : ProviderActorKind.User,
            actor.UserId,
            new ProviderBackendPrincipal(
                snapshot.Protocol,
                snapshot.BackendInstanceId,
                snapshot.BackendPrincipalId),
            actingForUserId: actor.IsAdministrator && snapshot.OwnerUserId != actor.UserId
                ? snapshot.OwnerUserId
                : null);

    private static ProviderActorContext CatalogActor(
        ExternalMetadataSnapshotRecord snapshot) => new(

            ProviderActorKind.User,
            snapshot.OwnerUserId,
            new ProviderBackendPrincipal(
                snapshot.Protocol,
                snapshot.BackendInstanceId,
                snapshot.BackendPrincipalId));

    private static string CleanReason(string? value, string fallback)
    {
        var reason = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return reason.Length <= 512 ? reason : reason[..512];
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void ValidateDecisionInput(MatchDecisionInput input)
    {
        if (input.DecisionVersion <= 0 ||
            input.SourceSnapshotVersion <= 0 ||
            string.IsNullOrWhiteSpace(input.MatcherVersion) ||
            input.Confidence is < 0 or > 1 ||
            input.Threshold is < 0 or > 1 ||
            string.IsNullOrWhiteSpace(input.PolicyVersion))
            throw new ArgumentException("The match decision is incomplete.", nameof(input));
        PersistenceGuard.ValidateSafeJson(input.CandidateResultsJson, nameof(input.CandidateResultsJson));
        PersistenceGuard.ValidateSafeJson(input.ReasonsJson, nameof(input.ReasonsJson));
        PersistenceGuard.ValidateSafeJson(input.WarningsJson, nameof(input.WarningsJson));
        if (input.State is TrackMatchState.Accepted or TrackMatchState.Suggested &&
                !input.LibraryTrackId.HasValue &&
                !input.CanonicalRecordingId.HasValue ||
            input.State == TrackMatchState.Pinned &&
                !input.LibraryTrackId.HasValue ||
            input.State is TrackMatchState.Unresolved or TrackMatchState.Rejected or
                TrackMatchState.Ambiguous &&
                input.LibraryTrackId.HasValue)
            throw new ArgumentException(
                "The selected library track does not match the decision state.",
                nameof(input));
        if (input.State == TrackMatchState.Accepted && input.Confidence < input.Threshold)
            throw new ArgumentException(
                "A match below its acceptance threshold cannot be accepted for automatic action.",
                nameof(input));
    }

    private static bool MatchesImmutableDecision(
        TrackMatchRecord record,
        MatchDecisionInput input) =>
        record.State == input.State &&
        record.LibraryTrackId == input.LibraryTrackId &&
        record.CanonicalRecordingId == input.CanonicalRecordingId &&
        record.SourceSnapshotVersion == input.SourceSnapshotVersion &&
        record.LibraryIndexRevision == input.LibraryIndexRevision &&
        record.MatcherVersion == input.MatcherVersion.Trim() &&
        record.PolicyVersion == input.PolicyVersion.Trim() &&
        record.Confidence == input.Confidence &&
        record.Threshold == input.Threshold &&
        record.CandidateResultsJson == input.CandidateResultsJson &&
        record.ReasonsJson == input.ReasonsJson &&
        record.WarningsJson == input.WarningsJson;

    private static bool IsConcurrentMatchWrite(Exception exception) =>
        DbErrors.IsTransientConflict(exception) || DbErrors.IsUniqueViolation(exception);

    private static TrackMatchRecord ToRecord(
        MatchDecisionInput input,
        Guid ownerUserId,
        string correlationId,
        DateTimeOffset decidedAt) => new()
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = ownerUserId,
            ExternalSnapshotId = input.ExternalSnapshotId,
            LibraryTrackId = input.LibraryTrackId,
            CanonicalRecordingId = input.CanonicalRecordingId,
            State = input.State,
            Confidence = input.Confidence,
            Threshold = input.Threshold,
            DecisionVersion = input.DecisionVersion,
            SourceSnapshotVersion = input.SourceSnapshotVersion,
            LibraryIndexRevision = input.LibraryIndexRevision,
            MatcherVersion = input.MatcherVersion.Trim(),
            PolicyVersion = input.PolicyVersion.Trim(),
            CandidateResultsJson = input.CandidateResultsJson,
            ReasonsJson = input.ReasonsJson,
            WarningsJson = input.WarningsJson,
            CorrelationId = correlationId,
            DecidedAt = decidedAt
        };

    private static async Task<ExternalMetadataSnapshotRecord> OwnedSnapshotAsync(
        AllstarrDbContext db,
        ProviderActorContext actor,
        Guid id,
        CancellationToken cancellationToken)
    {
        var record = await db.ExternalMetadataSnapshots.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == id,
                cancellationToken) ?? throw new KeyNotFoundException("Snapshot not found.");
        PersistenceGuard.RequireOwner(actor, record.OwnerUserId);
        return record;
    }

    private static void ValidateHash(string value, string name)
    {
        if (value.Length != 64 ||
            value.Any(character => !Uri.IsHexDigit(character)) ||
            value != value.ToLowerInvariant())
            throw new ArgumentException("A normalized SHA-256 value is required.", name);
    }

    private static (string? Title, string? Artist, string? Album, string? AlbumArtist, string? Isrc) ReadMetadata(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return (
                ReadString(root, "title", "Title", "name", "Name"),
                TrackSnapshotMetadata.Artist(root),
                ReadString(root, "album", "Album"),
                ReadString(root, "albumArtist", "AlbumArtist"),
                ReadString(root, "isrc", "Isrc", "ISRC"));
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static long? ReadDurationMilliseconds(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            foreach (var name in new[] { "durationMilliseconds", "DurationMilliseconds", "durationMs", "DurationMs" })
            {
                if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number)
                    return (long)Math.Round(value.GetDouble());
            }
            foreach (var name in new[] { "durationSeconds", "DurationSeconds", "duration", "Duration" })
            {
                if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number)
                    return (long)Math.Round(value.GetDouble() * 1000d);
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    private static IReadOnlyDictionary<string, string> ReadProviderTrackIds(string json)
    {
        try
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (providerId, trackId) in values ?? [])
            {
                if (!string.IsNullOrWhiteSpace(providerId) && !string.IsNullOrWhiteSpace(trackId))
                {
                    normalized[providerId.Trim()] = trackId.Trim();
                }
            }
            return normalized;
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string? ReadString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }
        return null;
    }
}

using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Playlists;

public sealed record ExternalSnapshotInput(
    Guid ProviderAccountId, string ProviderId, string LibraryScopeId, string ResourceKind,
    string ExternalIdHash, int SnapshotVersion, string ProviderRevision, string PayloadJson,
    string PayloadSha256, Guid? ProviderTrackIdentityId = null, Guid? SourceJobId = null);
public sealed record MatchDecisionInput(
    Guid ExternalSnapshotId, Guid? LibraryTrackId, Guid? CanonicalRecordingId,
    TrackMatchState State, double Confidence, double Threshold, int DecisionVersion,
    int SourceSnapshotVersion, long LibraryIndexRevision, string MatcherVersion,
    string PolicyVersion, string CandidateResultsJson, string ReasonsJson, string WarningsJson)
{
    public static MatchDecisionInput FromDecision(
        Guid externalSnapshotId,
        Guid? canonicalRecordingId,
        TrackMatchDecision decision,
        int decisionVersion,
        int sourceSnapshotVersion,
        long libraryIndexRevision,
        string policyVersion) => new(
        externalSnapshotId,
        decision.SelectedLibraryTrackId,
        canonicalRecordingId,
        Enum.Parse<TrackMatchState>(decision.State.ToString(), true),
        decision.Confidence,
        decision.AcceptThreshold,
        decisionVersion,
        sourceSnapshotVersion,
        libraryIndexRevision,
        TrackMatchDecisionEngine.AlgorithmVersion,
        policyVersion,
        JsonSerializer.Serialize(decision.Candidates),
        JsonSerializer.Serialize(decision.Reasons),
        JsonSerializer.Serialize(decision.Warnings));

    public static MatchDecisionInput FromExternalDecision(
        Guid externalSnapshotId,
        Guid canonicalRecordingId,
        TrackMatchDecision decision,
        int decisionVersion,
        int sourceSnapshotVersion,
        long libraryIndexRevision,
        string policyVersion) => new(
        externalSnapshotId,
        null,
        canonicalRecordingId,
        Enum.Parse<TrackMatchState>(decision.State.ToString(), true),
        decision.Confidence,
        decision.AcceptThreshold,
        decisionVersion,
        sourceSnapshotVersion,
        libraryIndexRevision,
        TrackMatchDecisionEngine.AlgorithmVersion,
        policyVersion,
        JsonSerializer.Serialize(decision.Candidates),
        JsonSerializer.Serialize(decision.Reasons),
        JsonSerializer.Serialize(decision.Warnings));
}
public sealed record ManualOverrideInput(
    Guid ExternalSnapshotId, string LibraryScopeId, ManualOverrideDecision Decision,
    Guid? LibraryTrackId, string Reason);

public sealed record PlaylistLinkInput(Guid ProviderAccountId, string SourceProviderId, string SourcePlaylistId, string SourcePlaylistIdHash, string LibraryScopeId, string TargetProtocol, string TargetBackendInstanceId, PlaylistLinkMode Mode, PlaylistMaterializationMode MaterializationMode, string RuleVersion, string PolicyVersion, Guid? ScheduleId = null, string? TargetPlaylistId = null, Guid? TargetCredentialReferenceId = null, bool MirrorStaleEntries = false, bool PreserveManualEntries = true, bool SyncName = true, bool SyncDescription = true, bool SyncArtwork = true, PlaylistProjectionMode ProjectionMode = PlaylistProjectionMode.Resolved);
public sealed record PlaylistLinkUpdate(long ExpectedRevision, PlaylistLinkMode Mode, PlaylistMaterializationMode MaterializationMode, string RuleVersion, string PolicyVersion, Guid? ScheduleId, string? TargetPlaylistId, bool MirrorStaleEntries, bool PreserveManualEntries, bool SyncName, bool SyncDescription, bool SyncArtwork, Guid? TargetCredentialReferenceId = null, PlaylistProjectionMode ProjectionMode = PlaylistProjectionMode.Resolved);
public sealed record PlaylistSourceEntryInput(int Position, Guid ExternalMetadataSnapshotId, string SourceEntryIdHash);
public sealed record PlaylistSourceSnapshotInput(int SnapshotVersion, string ProviderRevision, string? ETag, string Name, string? Description, string? ArtworkReferenceKey, string PayloadSha256, IReadOnlyList<PlaylistSourceEntryInput> Entries, Guid? SourceJobId = null);
public sealed record PersistedPlaylistPreviewEntry(
    int Position,
    Guid ExternalSnapshotId,
    TrackMatchState State,
    Guid? LibraryTrackId,
    ManualOverrideDecision? Override,
    Guid SourceEntryId = default,
    string SourceTrackReference = "",
    PlaylistSourceIdentity? SourceIdentity = null,
    PlaylistSourceMetadata? SourceMetadata = null,
    PlaylistResolvedRoute? ResolvedRoute = null,
    bool TargetEligible = false,
    string OutcomeCode = PlaylistMaterializationOutcomeCodes.SkippedUnresolved,
    int? TargetPosition = null,
    PlaylistPreviewEntryStatus Status = PlaylistPreviewEntryStatus.Unresolved,
    Guid? DuplicateOfSourceEntryId = null);
public sealed record PlaylistPreview(
    Guid LinkId,
    Guid SnapshotId,
    string Name,
    string? Description,
    string? ArtworkReferenceKey,
    IReadOnlyList<PersistedPlaylistPreviewEntry> Entries)
{
    public string SourceRevision { get; init; } = string.Empty;
    public string LibraryScopeId { get; init; } = string.Empty;
    public string TargetProtocol { get; init; } = string.Empty;
    public string TargetBackendInstanceId { get; init; } = string.Empty;
}
public sealed record PlaylistRunInput(Guid SnapshotId, long Generation, string IdempotencyKey, string RuleVersion, PlaylistMaterializationMode MaterializationMode, PlaylistSyncState State, string? TargetRevisionBefore, Guid? ScheduleId = null, Guid? JobId = null);
public sealed record PlaylistRunEntryInput(Guid SourceEntryId, Guid? TrackMatchId, Guid? LibraryTrackId, int SourcePosition, int? TargetPosition, PlaylistEntryOutcome Outcome, string? OutcomeCode, string DetailsJson);

public interface IPlaylistPersistenceService
{
    Task<PlaylistLinkRecord> CreateLinkAsync(ProtocolExecutionContext context, PlaylistLinkInput input, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PlaylistLinkRecord>> ListLinksAsync(ProtocolExecutionContext context, string? libraryScopeId = null, CancellationToken cancellationToken = default);
    Task<PlaylistLinkRecord> GetLinkAsync(ProtocolExecutionContext context, Guid linkId, CancellationToken cancellationToken = default);
    Task<PlaylistLinkRecord> UpdateLinkAsync(ProtocolExecutionContext context, Guid linkId, PlaylistLinkUpdate update, CancellationToken cancellationToken = default);
    Task DeleteLinkAsync(ProtocolExecutionContext context, Guid linkId, long expectedRevision, CancellationToken cancellationToken = default);
    Task<PlaylistSourceSnapshotRecord> CaptureSourceSnapshotAsync(ProtocolExecutionContext context, Guid linkId, PlaylistSourceSnapshotInput input, CancellationToken cancellationToken = default);
    Task<PlaylistPreview> ReadPreviewAsync(ProtocolExecutionContext context, Guid linkId, Guid snapshotId, CancellationToken cancellationToken = default);
    Task<PlaylistSyncRunRecord> RecordRunAsync(ProtocolExecutionContext context, Guid linkId, PlaylistRunInput input, IReadOnlyList<PlaylistRunEntryInput> results, CancellationToken cancellationToken = default);
}

/// <summary>Owns durable playlist links, source snapshots, previews, and sync-run evidence.</summary>
public sealed class PlaylistPersistenceService : IPlaylistPersistenceService
{
    private static readonly JsonSerializerOptions PreviewJson = new() { PropertyNameCaseInsensitive = true };
    private readonly IDbContextFactory<AllstarrDbContext> _factory;
    private readonly ProviderAccountResolver _accounts;
    private readonly IPlatformClock _clock;
    private readonly ITrackMatchRepository _trackMatches;
    private readonly PlaylistMaterializationPlanner _planner;
    public PlaylistPersistenceService(
        IDbContextFactory<AllstarrDbContext> factory,
        ProviderAccountResolver accounts,
        IPlatformClock clock,
        ITrackMatchRepository trackMatches,
        PlaylistMaterializationPlanner? planner = null) =>
        (_factory, _accounts, _clock, _trackMatches, _planner) =
        (factory, accounts, clock, trackMatches, planner ?? new PlaylistMaterializationPlanner());

    public async Task<PlaylistLinkRecord> CreateLinkAsync(ProtocolExecutionContext context, PlaylistLinkInput input, CancellationToken cancellationToken = default)
    {
        var (principal, actor) = PersistenceGuard.Require(context, input.LibraryScopeId);
        PersistenceGuard.Required(input.RuleVersion, nameof(input.RuleVersion)); PersistenceGuard.Required(input.PolicyVersion, nameof(input.PolicyVersion)); PersistenceGuard.ValidateStableReference(input.TargetPlaylistId, nameof(input.TargetPlaylistId));
        if (input.SourcePlaylistIdHash.Length != 64) throw new ArgumentException("A source playlist hash is required.", nameof(input));
        var account = await _accounts.ResolveAsync(new ProviderAccountResolutionRequest(principal, input.SourceProviderId, "playlist", input.ProviderAccountId, input.LibraryScopeId), cancellationToken) ?? throw new UnauthorizedAccessException("The provider account is unavailable.");
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var existing = await db.PlaylistLinks.AsNoTracking().SingleOrDefaultAsync(item => item.TenantId == actor.TenantId && item.OwnerUserId == actor.EffectiveUserId && item.LibraryScopeId == input.LibraryScopeId && item.ProviderAccountId == input.ProviderAccountId && item.SourcePlaylistIdHash == input.SourcePlaylistIdHash && item.TargetProtocol == input.TargetProtocol && item.TargetBackendInstanceId == input.TargetBackendInstanceId, cancellationToken);
        if (existing != null) return existing;
        var record = new PlaylistLinkRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = actor.TenantId,
            OwnerUserId = actor.EffectiveUserId!.Value,
            ProviderAccountId = account.Account.Id,
            ScheduleId = input.ScheduleId,
            Enabled = true,
            LibraryScopeId = input.LibraryScopeId,
            SourceProviderId = account.Account.ProviderId,
            SourcePlaylistId = PersistenceGuard.Required(input.SourcePlaylistId, nameof(input.SourcePlaylistId)),
            SourcePlaylistIdHash = input.SourcePlaylistIdHash,
            TargetProtocol = PersistenceGuard.Required(input.TargetProtocol, nameof(input.TargetProtocol)).ToLowerInvariant(),
            TargetBackendInstanceId = PersistenceGuard.Required(input.TargetBackendInstanceId, nameof(input.TargetBackendInstanceId)),
            TargetPlaylistId = input.TargetPlaylistId,
            TargetCredentialReferenceId = input.TargetCredentialReferenceId,
            Mode = input.Mode,
            ProjectionMode = input.ProjectionMode,
            MaterializationMode = input.MaterializationMode,
            MirrorStaleEntries = input.MirrorStaleEntries,
            PreserveManualEntries = input.PreserveManualEntries,
            SyncName = input.SyncName,
            SyncDescription = input.SyncDescription,
            SyncArtwork = input.SyncArtwork,
            RuleVersion = input.RuleVersion.Trim(),
            PolicyVersion = input.PolicyVersion.Trim(),
            CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow
        };
        db.PlaylistLinks.Add(record); await db.SaveChangesAsync(cancellationToken); return record;
    }

    public async Task<IReadOnlyList<PlaylistLinkRecord>> ListLinksAsync(ProtocolExecutionContext context, string? libraryScopeId = null, CancellationToken cancellationToken = default)
    {
        var actor = context.RequireActor();
        _ = context.Principal ?? throw new UnauthorizedAccessException("A linked actor is required.");
        if (!actor.EffectiveUserId.HasValue) throw new UnauthorizedAccessException("A user owner is required.");
        var normalizedLibraryScopeId = string.IsNullOrWhiteSpace(libraryScopeId) ? null : libraryScopeId.Trim();
        if (normalizedLibraryScopeId != null) PersistenceGuard.RequireLibrary(context, normalizedLibraryScopeId);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var query = db.PlaylistLinks.AsNoTracking().Where(item => item.TenantId == actor.TenantId);
        if (normalizedLibraryScopeId != null) query = query.Where(item => item.LibraryScopeId == normalizedLibraryScopeId);
        if (actor.Kind != ProviderActorKind.Administrator) query = query.Where(item => item.OwnerUserId == actor.EffectiveUserId);
        return await query.OrderBy(item => item.LibraryScopeId).ThenBy(item => item.CreatedAt).ToListAsync(cancellationToken);
    }

    public async Task<PlaylistLinkRecord> GetLinkAsync(ProtocolExecutionContext context, Guid linkId, CancellationToken cancellationToken = default)
    {
        var actor = context.RequireActor(); await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var record = await db.PlaylistLinks.AsNoTracking().SingleOrDefaultAsync(item => item.Id == linkId && item.TenantId == actor.TenantId, cancellationToken) ?? throw new KeyNotFoundException("Playlist link not found.");
        PersistenceGuard.RequireOwner(actor, record.OwnerUserId); PersistenceGuard.RequireLibrary(context, record.LibraryScopeId); return record;
    }

    public async Task<PlaylistLinkRecord> UpdateLinkAsync(ProtocolExecutionContext context, Guid linkId, PlaylistLinkUpdate update, CancellationToken cancellationToken = default)
    {
        var actor = context.RequireActor(); PersistenceGuard.Required(update.RuleVersion, nameof(update.RuleVersion)); PersistenceGuard.Required(update.PolicyVersion, nameof(update.PolicyVersion)); PersistenceGuard.ValidateStableReference(update.TargetPlaylistId, nameof(update.TargetPlaylistId));
        await using var db = await _factory.CreateDbContextAsync(cancellationToken); var record = await db.PlaylistLinks.SingleOrDefaultAsync(item => item.Id == linkId && item.TenantId == actor.TenantId, cancellationToken) ?? throw new KeyNotFoundException("Playlist link not found.");
        PersistenceGuard.RequireOwner(actor, record.OwnerUserId); PersistenceGuard.RequireLibrary(context, record.LibraryScopeId); if (record.Revision != update.ExpectedRevision) throw new DbUpdateConcurrencyException("The playlist link changed before this update.");
        record.Mode = update.Mode; record.ProjectionMode = update.ProjectionMode; record.MaterializationMode = update.MaterializationMode; record.RuleVersion = update.RuleVersion.Trim(); record.PolicyVersion = update.PolicyVersion.Trim(); record.ScheduleId = update.ScheduleId; record.TargetPlaylistId = update.TargetPlaylistId; record.TargetCredentialReferenceId = update.TargetCredentialReferenceId; record.MirrorStaleEntries = update.MirrorStaleEntries; record.PreserveManualEntries = update.PreserveManualEntries; record.SyncName = update.SyncName; record.SyncDescription = update.SyncDescription; record.SyncArtwork = update.SyncArtwork; record.UpdatedAt = _clock.UtcNow; record.Revision++;
        await db.SaveChangesAsync(cancellationToken); return record;
    }

    public async Task DeleteLinkAsync(
        ProtocolExecutionContext context, Guid linkId, long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var actor = context.RequireActor();
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var record = await db.PlaylistLinks.SingleOrDefaultAsync(item =>
            item.Id == linkId && item.TenantId == actor.TenantId, cancellationToken)
            ?? throw new KeyNotFoundException("Playlist link not found.");
        PersistenceGuard.RequireOwner(actor, record.OwnerUserId);
        PersistenceGuard.RequireLibrary(context, record.LibraryScopeId);
        if (record.Revision != expectedRevision)
            throw new DbUpdateConcurrencyException("The playlist link changed before it could be removed.");

        var snapshotIds = await db.PlaylistSourceSnapshots
            .Where(item => item.TenantId == actor.TenantId && item.PlaylistLinkId == linkId)
            .Select(item => item.Id).ToListAsync(cancellationToken);
        var sourceEntryIds = await db.PlaylistSourceEntries
            .Where(item => item.TenantId == actor.TenantId && snapshotIds.Contains(item.PlaylistSourceSnapshotId))
            .Select(item => item.Id).ToListAsync(cancellationToken);
        var runIds = await db.PlaylistSyncRuns
            .Where(item => item.TenantId == actor.TenantId && item.PlaylistLinkId == linkId)
            .Select(item => item.Id).ToListAsync(cancellationToken);

        await db.PlaylistSyncEntryResults
            .Where(item => item.TenantId == actor.TenantId &&
                (runIds.Contains(item.PlaylistSyncRunId) || sourceEntryIds.Contains(item.PlaylistSourceEntryId)))
            .ExecuteDeleteAsync(cancellationToken);
        await db.PlaylistTargetMemberships
            .Where(item => item.TenantId == actor.TenantId && item.PlaylistLinkId == linkId)
            .ExecuteDeleteAsync(cancellationToken);
        await db.PlaylistSyncRuns
            .Where(item => item.TenantId == actor.TenantId && item.PlaylistLinkId == linkId)
            .ExecuteDeleteAsync(cancellationToken);
        await db.PlaylistSourceEntries
            .Where(item => item.TenantId == actor.TenantId && snapshotIds.Contains(item.PlaylistSourceSnapshotId))
            .ExecuteDeleteAsync(cancellationToken);
        await db.PlaylistSourceSnapshots
            .Where(item => item.TenantId == actor.TenantId && item.PlaylistLinkId == linkId)
            .ExecuteDeleteAsync(cancellationToken);

        if (record.ScheduleId is { } scheduleId &&
            !await db.PlaylistLinks.AnyAsync(item => item.Id != linkId && item.TenantId == actor.TenantId && item.ScheduleId == scheduleId, cancellationToken))
        {
            var schedule = await db.JobSchedules.SingleOrDefaultAsync(item =>
                item.Id == scheduleId && item.TenantId == actor.TenantId, cancellationToken);
            if (schedule != null)
            {
                schedule.Enabled = false;
                schedule.NextRunAt = null;
                schedule.Revision++;
            }
        }

        db.PlaylistLinks.Remove(record);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<PlaylistSourceSnapshotRecord> CaptureSourceSnapshotAsync(ProtocolExecutionContext context, Guid linkId, PlaylistSourceSnapshotInput input, CancellationToken cancellationToken = default)
    {
        var actor = context.RequireActor(); if (input.SnapshotVersion <= 0 || input.Entries.Select((entry, index) => entry.Position == index).Any(valid => !valid)) throw new ArgumentException("Snapshot entries must use contiguous source order.", nameof(input));
        if (input.Entries.Any(entry => entry.SourceEntryIdHash.Length != 64 || entry.SourceEntryIdHash.Any(character => !Uri.IsHexDigit(character)))) throw new ArgumentException("Every source entry requires a SHA-256 identity hash.", nameof(input));
        PersistenceGuard.Required(input.ProviderRevision, nameof(input.ProviderRevision)); PersistenceGuard.Required(input.Name, nameof(input.Name)); PersistenceGuard.ValidateStableReference(input.ArtworkReferenceKey, nameof(input.ArtworkReferenceKey));
        if (input.PayloadSha256.Length != 64) throw new ArgumentException("A payload hash is required.", nameof(input));
        await using var db = await _factory.CreateDbContextAsync(cancellationToken); var link = await db.PlaylistLinks.SingleOrDefaultAsync(item => item.Id == linkId && item.TenantId == actor.TenantId, cancellationToken) ?? throw new KeyNotFoundException("Playlist link not found."); PersistenceGuard.RequireOwner(actor, link.OwnerUserId); PersistenceGuard.RequireLibrary(context, link.LibraryScopeId);
        var existing = await db.PlaylistSourceSnapshots.AsNoTracking().SingleOrDefaultAsync(item =>
            item.TenantId == actor.TenantId &&
            item.OwnerUserId == link.OwnerUserId &&
            item.ProviderAccountId == link.ProviderAccountId &&
            item.PlaylistLinkId == link.Id &&
            item.SnapshotVersion == input.SnapshotVersion, cancellationToken);
        if (existing != null)
        {
            if (existing.PayloadSha256 != input.PayloadSha256)
                throw new InvalidOperationException("The immutable playlist snapshot version already has different content.");
            return existing;
        }
        var ids = input.Entries.Select(item => item.ExternalMetadataSnapshotId).Distinct().ToArray();
        var targetProtocols = TargetProtocols(link.TargetProtocol);
        var owned = await db.ExternalMetadataSnapshots.CountAsync(item =>
            ids.Contains(item.Id) &&
            item.TenantId == actor.TenantId &&
            item.OwnerUserId == link.OwnerUserId &&
            item.LibraryScopeId == link.LibraryScopeId &&
            item.ProviderAccountId == link.ProviderAccountId &&
            item.ProviderId == link.SourceProviderId &&
            item.ResourceKind == "track" &&
            item.BackendInstanceId == link.TargetBackendInstanceId &&
            targetProtocols.Contains(item.Protocol), cancellationToken);
        if (owned != ids.Length)
            throw new UnauthorizedAccessException("A source entry snapshot is outside the playlist scope or account.");
        var snapshot = new PlaylistSourceSnapshotRecord { Id = Guid.CreateVersion7(), TenantId = actor.TenantId, OwnerUserId = link.OwnerUserId, PlaylistLinkId = link.Id, ProviderAccountId = link.ProviderAccountId, SourceJobId = input.SourceJobId, SnapshotVersion = input.SnapshotVersion, ProviderRevision = input.ProviderRevision.Trim(), ETag = input.ETag, Name = input.Name.Trim(), Description = input.Description?.Trim(), ArtworkReferenceKey = input.ArtworkReferenceKey, PayloadSha256 = input.PayloadSha256, CorrelationId = context.CorrelationId, RetrievedAt = _clock.UtcNow };
        db.PlaylistSourceSnapshots.Add(snapshot); await db.SaveChangesAsync(cancellationToken);
        db.PlaylistSourceEntries.AddRange(input.Entries.Select(entry => new PlaylistSourceEntryRecord { Id = Guid.CreateVersion7(), TenantId = actor.TenantId, PlaylistSourceSnapshotId = snapshot.Id, ExternalMetadataSnapshotId = entry.ExternalMetadataSnapshotId, SourcePosition = entry.Position, SourceEntryIdHash = entry.SourceEntryIdHash })); await db.SaveChangesAsync(cancellationToken); return snapshot;
    }

    public async Task<PlaylistPreview> ReadPreviewAsync(ProtocolExecutionContext context, Guid linkId, Guid snapshotId, CancellationToken cancellationToken = default)
    {
        var actor = context.RequireActor();
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var link = await db.PlaylistLinks.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == linkId && item.TenantId == actor.TenantId,
            cancellationToken) ?? throw new KeyNotFoundException("Playlist link not found.");
        PersistenceGuard.RequireOwner(actor, link.OwnerUserId);
        PersistenceGuard.RequireLibrary(context, link.LibraryScopeId);
        var snapshot = await db.PlaylistSourceSnapshots.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == snapshotId &&
                    item.TenantId == actor.TenantId &&
                    item.OwnerUserId == link.OwnerUserId &&
                    item.ProviderAccountId == link.ProviderAccountId &&
                    item.PlaylistLinkId == link.Id,
            cancellationToken) ?? throw new KeyNotFoundException("Playlist snapshot not found.");
        var entries = await db.PlaylistSourceEntries.AsNoTracking()
            .Where(item => item.TenantId == actor.TenantId && item.PlaylistSourceSnapshotId == snapshot.Id)
            .OrderBy(item => item.SourcePosition)
            .ToListAsync(cancellationToken);
        var externalSnapshotIds = entries.Select(item => item.ExternalMetadataSnapshotId).Distinct().ToArray();
        var resolution = await _trackMatches.GetResolutionDataAsync(
            new TrackMatchActor(
                actor.TenantId,
                actor.EffectiveUserId ?? link.OwnerUserId,
                actor.Kind == ProviderActorKind.Administrator),
            link.OwnerUserId,
            link.LibraryScopeId,
            externalSnapshotIds,
            cancellationToken);

        var targetProtocols = TargetProtocols(link.TargetProtocol);
        var snapshots = resolution.Snapshots
            .Where(item => item.OwnerUserId == link.OwnerUserId &&
                           item.LibraryScopeId == link.LibraryScopeId &&
                           item.ProviderAccountId == link.ProviderAccountId &&
                           item.ProviderId == link.SourceProviderId &&
                           item.ResourceKind == "track" &&
                           item.BackendInstanceId == link.TargetBackendInstanceId &&
                           targetProtocols.Contains(item.Protocol))
            .ToDictionary(item => item.Id);
        var sourceIdentities = resolution.ProviderIdentities.ToDictionary(item => item.Id);
        var sourceIdentityLookup = resolution.ProviderIdentities
            .Where(item => item.ResourceKind == ProviderResourceKind.Track)
            .ToLookup(item => (item.ProviderId.ToLowerInvariant(), item.ExternalIdHash));
        var linkedSourceIdentityByExternalId = snapshots.Values.ToDictionary(
            external => external.Id,
            external =>
                external.ProviderTrackIdentityId is { } identityId &&
                sourceIdentities.TryGetValue(identityId, out var identity) &&
                MatchesSourceIdentity(external, identity)
                    ? identity
                    : null);
        var sourceIdentityByExternalId = snapshots.Values.ToDictionary(
            external => external.Id,
            external => linkedSourceIdentityByExternalId[external.Id] ??
                sourceIdentityLookup[(external.ProviderId.ToLowerInvariant(), external.ExternalIdHash)]
                    .FirstOrDefault(identity => MatchesSourceIdentity(external, identity)));
        var identitiesByCanonical = resolution.ProviderIdentities
            .Where(item => item.ResourceKind == ProviderResourceKind.Track)
            .GroupBy(item => item.CanonicalRecordingId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var manualOverrides = resolution.ActiveOverrides.ToDictionary(item => item.ExternalSnapshotId);
        var latestMatches = resolution.LatestDecisions.ToDictionary(item => item.ExternalSnapshotId);
        var libraryTracks = await db.LibraryTracks.AsNoTracking()
            .Where(item => item.TenantId == actor.TenantId &&
                           item.OwnerUserId == link.OwnerUserId &&
                           item.LibraryScopeId == link.LibraryScopeId &&
                           item.BackendInstanceId == link.TargetBackendInstanceId &&
                           targetProtocols.Contains(item.Protocol))
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var playableIds = libraryTracks.Keys.ToHashSet();
        var providerPriority = new[] { link.SourceProviderId }
            .Concat(resolution.ProviderIdentities.Select(item => item.ProviderId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var source = new ImmutablePlaylistSourceSnapshot(
            snapshot.Id,
            link.Id,
            snapshot.ProviderRevision,
            snapshot.Name,
            entries.Select(entry =>
            {
                var external = snapshots.GetValueOrDefault(entry.ExternalMetadataSnapshotId);
                var identity = external == null
                    ? null
                    : linkedSourceIdentityByExternalId.GetValueOrDefault(external.Id);
                return new ImmutablePlaylistSourceEntry(
                    entry.Id,
                    entry.SourcePosition,
                    entry.ExternalMetadataSnapshotId,
                    external?.ExternalIdHash ?? entry.SourceEntryIdHash,
                    external == null ? null : new PlaylistSourceIdentity(
                        external.ProviderId,
                        external.ProviderAccountId,
                        external.ExternalIdHash,
                        external.ProviderRevision,
                        external.SnapshotVersion,
                        identity?.ExternalId),
                    external == null ? null : ReadSourceMetadata(external.PayloadJson));
            }),
            snapshot.Description,
            snapshot.ArtworkReferenceKey);
        var sourceEntriesById = source.Entries.ToDictionary(item => item.SourceEntryId);
        var decisions = new List<PersistedPlaylistMatchDecision>(entries.Count);
        foreach (var entry in entries)
        {
            snapshots.TryGetValue(entry.ExternalMetadataSnapshotId, out var external);
            manualOverrides.TryGetValue(entry.ExternalMetadataSnapshotId, out var manual);
            latestMatches.TryGetValue(entry.ExternalMetadataSnapshotId, out var match);
            if (external == null)
            {
                decisions.Add(new(entry.Id, entry.ExternalMetadataSnapshotId,
                    TrackMatchReviewState.Unresolved, null, null, null, 0, 1, 0, [], []));
                continue;
            }

            var sourceIdentity = sourceIdentityByExternalId.GetValueOrDefault(external.Id);
            var canonicalId = match?.CanonicalRecordingId ?? sourceIdentity?.CanonicalRecordingId;
            var routeIdentities = canonicalId.HasValue &&
                                  identitiesByCanonical.TryGetValue(canonicalId.Value, out var canonicalIdentities)
                ? canonicalIdentities
                : [];
            var classification = TrackClassifier.Classify(
                manual,
                match,
                sourceIdentity,
                routeIdentities,
                providerPriority,
                playableIds);
            libraryTracks.TryGetValue(classification.LibraryTrackId ?? Guid.Empty, out var library);
            var route = classification.RouteKind switch
            {
                TrackRouteKind.Local => new PlaylistResolvedRoute(
                    TrackRouteKind.Local,
                    classification.LibraryTrackId ?? match?.LibraryTrackId,
                    library?.BackendItemId,
                    library?.BackendInstanceId,
                    library?.Protocol,
                    library?.LibraryScopeId,
                    CanonicalRecordingId: canonicalId),
                TrackRouteKind.External when classification.PrimaryProviderRoute is { } selected => new PlaylistResolvedRoute(
                    TrackRouteKind.External,
                    ProviderId: selected.ProviderId,
                    ExternalId: selected.ExternalId,
                    CanonicalRecordingId: canonicalId),
                _ when match?.LibraryTrackId is { } matchedId => new PlaylistResolvedRoute(
                    TrackRouteKind.Local,
                    LibraryTrackId: matchedId,
                    CanonicalRecordingId: canonicalId),
                _ => new PlaylistResolvedRoute(TrackRouteKind.Unresolved, CanonicalRecordingId: canonicalId)
            };
            decisions.Add(new(
                entry.Id,
                external.Id,
                ToPlannerState(classification.ReviewState),
                classification.LibraryTrackId,
                library?.BackendItemId,
                library?.BackendInstanceId,
                match?.Confidence ?? 0,
                match?.Threshold ?? 1,
                match?.DecisionVersion ?? 0,
                ReadStringArray(match?.ReasonsJson),
                ReadStringArray(match?.WarningsJson),
                Route: route));
        }

        var plan = _planner.Plan(
            PlaylistPlanMode.Virtual,
            source,
            decisions,
            new PlaylistPlanningTarget(link.TargetProtocol, link.TargetBackendInstanceId, link.TargetPlaylistId),
            new PlaylistPlanningRules(link.RuleVersion, 1, link.PreserveManualEntries, link.MirrorStaleEntries,
                syncName: link.SyncName, syncDescription: link.SyncDescription, syncArtwork: link.SyncArtwork));
        var decisionsByEntry = decisions.ToDictionary(item => item.SourceEntryId);
        var result = plan.Entries.Select(item =>
        {
            decisionsByEntry.TryGetValue(item.SourceEntryId, out var decision);
            var sourceEntry = sourceEntriesById[item.SourceEntryId];
            manualOverrides.TryGetValue(sourceEntry.ExternalSnapshotId, out var manual);
            return new PersistedPlaylistPreviewEntry(
                item.SourcePosition,
                sourceEntry.ExternalSnapshotId,
                ToTrackMatchState(decision?.State ?? TrackMatchReviewState.Unresolved),
                item.LibraryTrackId,
                manual?.Decision,
                item.SourceEntryId,
                item.SourceTrackReference,
                item.SourceIdentity,
                item.SourceMetadata,
                item.ResolvedRoute,
                item.TargetEligible,
                item.OutcomeCode,
                item.TargetPosition,
                item.Status,
                item.DuplicateOfSourceEntryId);
        }).ToArray();
        return new PlaylistPreview(link.Id, snapshot.Id, snapshot.Name, snapshot.Description, snapshot.ArtworkReferenceKey, result)
        {
            SourceRevision = snapshot.ProviderRevision,
            LibraryScopeId = link.LibraryScopeId,
            TargetProtocol = link.TargetProtocol,
            TargetBackendInstanceId = link.TargetBackendInstanceId
        };
    }

    private static string[] TargetProtocols(string protocol) => protocol.Trim().ToLowerInvariant() switch
    {
        "jellyfin" => ["jellyfin"],
        "subsonic" or "opensubsonic" or "navidrome" => ["subsonic", "opensubsonic", "navidrome"],
        _ => [protocol.Trim().ToLowerInvariant()]
    };

    private static bool MatchesSourceIdentity(
        ExternalMetadataSnapshotRecord external,
        ProviderTrackIdentityRecord identity) =>
        identity.TenantId == external.TenantId &&
        identity.ResourceKind == ProviderResourceKind.Track &&
        identity.ProviderId.Equals(external.ProviderId, StringComparison.OrdinalIgnoreCase) &&
        identity.ExternalIdHash.Equals(external.ExternalIdHash, StringComparison.Ordinal) &&
        (identity.ProviderAccountId == null || identity.ProviderAccountId == external.ProviderAccountId);

    private static TrackMatchReviewState ToPlannerState(TrackMatchState state) => state switch
    {
        TrackMatchState.Suggested => TrackMatchReviewState.Suggested,
        TrackMatchState.Accepted => TrackMatchReviewState.Accepted,
        TrackMatchState.Rejected => TrackMatchReviewState.Rejected,
        TrackMatchState.Pinned => TrackMatchReviewState.Pinned,
        TrackMatchState.Ambiguous => TrackMatchReviewState.Ambiguous,
        _ => TrackMatchReviewState.Unresolved
    };

    private static TrackMatchState ToTrackMatchState(TrackMatchReviewState state) => state switch
    {
        TrackMatchReviewState.Suggested => TrackMatchState.Suggested,
        TrackMatchReviewState.Accepted => TrackMatchState.Accepted,
        TrackMatchReviewState.Rejected => TrackMatchState.Rejected,
        TrackMatchReviewState.Pinned => TrackMatchState.Pinned,
        TrackMatchReviewState.Ambiguous => TrackMatchState.Ambiguous,
        _ => TrackMatchState.Unresolved
    };

    private static string[] ReadStringArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static PlaylistSourceMetadata ReadSourceMetadata(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<PlaylistSourceMetadata>(payload, PreviewJson) ?? new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    public async Task<PlaylistSyncRunRecord> RecordRunAsync(ProtocolExecutionContext context, Guid linkId, PlaylistRunInput input, IReadOnlyList<PlaylistRunEntryInput> results, CancellationToken cancellationToken = default)
    {
        var actor = context.RequireActor(); PersistenceGuard.Required(input.IdempotencyKey, nameof(input.IdempotencyKey)); PersistenceGuard.Required(input.RuleVersion, nameof(input.RuleVersion)); if (input.Generation <= 0) throw new ArgumentException("Run generation must be positive.", nameof(input));
        await using var db = await _factory.CreateDbContextAsync(cancellationToken); var link = await db.PlaylistLinks.SingleOrDefaultAsync(item => item.Id == linkId && item.TenantId == actor.TenantId, cancellationToken) ?? throw new KeyNotFoundException("Playlist link not found."); PersistenceGuard.RequireOwner(actor, link.OwnerUserId); PersistenceGuard.RequireLibrary(context, link.LibraryScopeId); if (!await db.PlaylistSourceSnapshots.AnyAsync(item => item.Id == input.SnapshotId && item.TenantId == actor.TenantId && item.PlaylistLinkId == link.Id, cancellationToken)) throw new UnauthorizedAccessException("The run snapshot is outside the playlist link.");
        var existing = await db.PlaylistSyncRuns.AsNoTracking().SingleOrDefaultAsync(item => item.TenantId == actor.TenantId && item.PlaylistLinkId == link.Id && item.IdempotencyKey == input.IdempotencyKey, cancellationToken); if (existing != null) { if (existing.PlaylistSourceSnapshotId != input.SnapshotId || existing.Generation != input.Generation || existing.RuleVersion != input.RuleVersion.Trim() || existing.MaterializationMode != input.MaterializationMode) throw new InvalidOperationException("The sync-run idempotency key already belongs to different inputs."); return existing; }
        foreach (var result in results) PersistenceGuard.ValidateSafeJson(result.DetailsJson, nameof(results));
        if (results.Select(result => result.SourcePosition).Distinct().Count() != results.Count) throw new ArgumentException("A run may record only one result per source position.", nameof(results));
        var sourceEntryIds = results.Select(result => result.SourceEntryId).Distinct().ToArray();
        var sourceEntries = await db.PlaylistSourceEntries.AsNoTracking().Where(item => sourceEntryIds.Contains(item.Id) && item.TenantId == actor.TenantId && item.PlaylistSourceSnapshotId == input.SnapshotId).ToDictionaryAsync(item => item.Id, cancellationToken);
        if (sourceEntries.Count != sourceEntryIds.Length || results.Any(result => sourceEntries[result.SourceEntryId].SourcePosition != result.SourcePosition)) throw new UnauthorizedAccessException("A run result is outside the immutable source snapshot order.");
        var libraryIds = results.Where(result => result.LibraryTrackId.HasValue).Select(result => result.LibraryTrackId!.Value).Distinct().ToArray();
        if (await db.LibraryTracks.CountAsync(item => libraryIds.Contains(item.Id) && item.TenantId == actor.TenantId && item.OwnerUserId == link.OwnerUserId && item.LibraryScopeId == link.LibraryScopeId, cancellationToken) != libraryIds.Length) throw new UnauthorizedAccessException("A run result selected a library track outside the link scope.");
        var run = new PlaylistSyncRunRecord { Id = Guid.CreateVersion7(), TenantId = actor.TenantId, OwnerUserId = link.OwnerUserId, PlaylistLinkId = link.Id, PlaylistSourceSnapshotId = input.SnapshotId, ScheduleId = input.ScheduleId, JobId = input.JobId, Generation = input.Generation, IdempotencyKey = input.IdempotencyKey.Trim(), RuleVersion = input.RuleVersion.Trim(), MaterializationMode = input.MaterializationMode, State = input.State, TargetRevisionBefore = input.TargetRevisionBefore, StartedAt = _clock.UtcNow, CompletedAt = input.State is PlaylistSyncState.Pending or PlaylistSyncState.Running ? null : _clock.UtcNow };
        db.PlaylistSyncRuns.Add(run); await db.SaveChangesAsync(cancellationToken); db.PlaylistSyncEntryResults.AddRange(results.Select(result => new PlaylistSyncEntryResultRecord { Id = Guid.CreateVersion7(), TenantId = actor.TenantId, PlaylistSyncRunId = run.Id, PlaylistSourceEntryId = result.SourceEntryId, TrackMatchId = result.TrackMatchId, LibraryTrackId = result.LibraryTrackId, SourcePosition = result.SourcePosition, TargetPosition = result.TargetPosition, Outcome = result.Outcome, OutcomeCode = result.OutcomeCode, DetailsJson = result.DetailsJson })); await db.SaveChangesAsync(cancellationToken); return run;
    }
}

internal static class PersistenceGuard
{
    private static readonly string[] ForbiddenNames = ["password", "secret", "token", "cookie", "authorization", "credential", "audio", "mediaBytes", "base64"];
    public static (AllstarrPrincipal Principal, ProviderActorContext Actor) Require(ProtocolExecutionContext context, string library) { var actor = context.RequireActor(); var principal = context.Principal ?? throw new UnauthorizedAccessException("A linked actor is required."); RequireLibrary(context, library); if (!actor.EffectiveUserId.HasValue) throw new UnauthorizedAccessException("A user owner is required."); return (principal, actor); }
    public static void RequireLibrary(ProtocolExecutionContext context, string library) { Required(library, nameof(library)); if (context.LibraryScopeId != null && context.LibraryScopeId != library) throw new UnauthorizedAccessException("The library is outside the execution context."); }
    public static void RequireOwner(ProviderActorContext actor, Guid owner) { if (actor.Kind != ProviderActorKind.Administrator && actor.EffectiveUserId != owner) throw new UnauthorizedAccessException("The record belongs to another user."); }
    public static string Required(string value, string name) { if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A value is required.", name); return value.Trim(); }
    public static void ValidateStableReference(string? value, string name) { if (value?.Contains("://", StringComparison.Ordinal) == true) throw new ArgumentException("Only stable provider/backend reference keys may be persisted.", name); }
    public static void ValidateSafeJson(string json, string name)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 1_048_576) throw new ArgumentException("A bounded JSON payload is required.", name);
        using var document = JsonDocument.Parse(json); Walk(document.RootElement, name);
    }
    private static void Walk(JsonElement value, string name)
    {
        if (value.ValueKind == JsonValueKind.Object) foreach (var property in value.EnumerateObject()) { if (ForbiddenNames.Any(forbidden => property.Name.Contains(forbidden, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Credentials and media payloads may not be persisted.", name); Walk(property.Value, name); }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Walk(item, name);
        else if (value.ValueKind == JsonValueKind.String) { var text = value.GetString(); if (text?.StartsWith("data:audio", StringComparison.OrdinalIgnoreCase) == true || text?.Contains("X-Amz-Signature=", StringComparison.OrdinalIgnoreCase) == true) throw new ArgumentException("Signed URLs and media payloads may not be persisted.", name); }
    }
}

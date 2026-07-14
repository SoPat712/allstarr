using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
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
    string PolicyVersion, string CandidateResultsJson, string ReasonsJson, string WarningsJson);
public sealed record ManualOverrideInput(
    Guid ExternalSnapshotId, string LibraryScopeId, ManualOverrideDecision Decision,
    Guid? LibraryTrackId, string Reason);

public interface ITrackMatchPersistenceService
{
    Task<ExternalMetadataSnapshotRecord> CaptureSnapshotAsync(ProtocolExecutionContext context, ExternalSnapshotInput input, CancellationToken cancellationToken = default);
    Task<TrackMatchRecord> RecordDecisionAsync(ProtocolExecutionContext context, MatchDecisionInput input, CancellationToken cancellationToken = default);
    Task<ManualTrackOverrideRecord> SetOverrideAsync(ProtocolExecutionContext context, ManualOverrideInput input, CancellationToken cancellationToken = default);
    Task RevokeOverrideAsync(ProtocolExecutionContext context, Guid overrideId, long expectedRevision, CancellationToken cancellationToken = default);
    Task<ManualTrackOverrideRecord?> GetActiveOverrideAsync(ProtocolExecutionContext context, Guid externalSnapshotId, CancellationToken cancellationToken = default);
}

public sealed class TrackMatchPersistenceService : ITrackMatchPersistenceService
{
    private readonly IDbContextFactory<AllstarrDbContext> _factory;
    private readonly ProviderAccountResolver _accounts;
    private readonly IPlatformClock _clock;

    public TrackMatchPersistenceService(IDbContextFactory<AllstarrDbContext> factory, ProviderAccountResolver accounts, IPlatformClock clock)
        => (_factory, _accounts, _clock) = (factory, accounts, clock);

    public async Task<ExternalMetadataSnapshotRecord> CaptureSnapshotAsync(ProtocolExecutionContext context, ExternalSnapshotInput input, CancellationToken cancellationToken = default)
    {
        var (principal, actor) = PersistenceGuard.Require(context, input.LibraryScopeId);
        ValidateHash(input.ExternalIdHash, nameof(input.ExternalIdHash));
        ValidateHash(input.PayloadSha256, nameof(input.PayloadSha256));
        if (input.SnapshotVersion <= 0 || string.IsNullOrWhiteSpace(input.ProviderRevision) || string.IsNullOrWhiteSpace(input.ResourceKind))
            throw new ArgumentException("Snapshot version, provider revision, and resource kind are required.", nameof(input));
        PersistenceGuard.ValidateSafeJson(input.PayloadJson, nameof(input.PayloadJson));
        var account = await _accounts.ResolveAsync(new ProviderAccountResolutionRequest(
            principal, input.ProviderId, "metadata", input.ProviderAccountId, input.LibraryScopeId), cancellationToken)
            ?? throw new UnauthorizedAccessException("The provider account is unavailable.");
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var existing = await db.ExternalMetadataSnapshots.AsNoTracking().SingleOrDefaultAsync(item =>
            item.TenantId == actor.TenantId && item.ProviderAccountId == account.Account.Id &&
            item.ResourceKind == input.ResourceKind && item.ExternalIdHash == input.ExternalIdHash &&
            item.SnapshotVersion == input.SnapshotVersion, cancellationToken);
        if (existing != null)
        {
            if (!existing.PayloadSha256.Equals(input.PayloadSha256, StringComparison.Ordinal) ||
                existing.OwnerUserId != actor.EffectiveUserId || existing.LibraryScopeId != input.LibraryScopeId)
                throw new InvalidOperationException("The snapshot version already exists with different immutable content or scope.");
            return existing;
        }

        var record = new ExternalMetadataSnapshotRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = actor.TenantId,
            OwnerUserId = actor.EffectiveUserId!.Value,
            ProviderAccountId = account.Account.Id,
            ProviderTrackIdentityId = input.ProviderTrackIdentityId,
            SourceJobId = input.SourceJobId,
            LibraryScopeId = input.LibraryScopeId,
            BackendInstanceId = context.BackendInstanceId,
            BackendPrincipalId = context.VerifiedBackendPrincipalId,
            Protocol = context.Protocol.ToString().ToLowerInvariant(),
            ProviderId = account.Account.ProviderId,
            ResourceKind = input.ResourceKind.Trim().ToLowerInvariant(),
            ExternalIdHash = input.ExternalIdHash,
            SnapshotVersion = input.SnapshotVersion,
            ProviderRevision = input.ProviderRevision.Trim(),
            PayloadJson = input.PayloadJson,
            PayloadSha256 = input.PayloadSha256,
            CorrelationId = context.CorrelationId,
            RetrievedAt = _clock.UtcNow
        };
        db.ExternalMetadataSnapshots.Add(record);
        await db.SaveChangesAsync(cancellationToken);
        return record;
    }

    public async Task<TrackMatchRecord> RecordDecisionAsync(ProtocolExecutionContext context, MatchDecisionInput input, CancellationToken cancellationToken = default)
    {
        var actor = context.RequireActor();
        if (input.DecisionVersion <= 0 || input.Confidence is < 0 or > 1 || input.Threshold is < 0 or > 1 || string.IsNullOrWhiteSpace(input.PolicyVersion))
            throw new ArgumentException("The match decision is incomplete.", nameof(input));
        PersistenceGuard.ValidateSafeJson(input.CandidateResultsJson, nameof(input.CandidateResultsJson));
        PersistenceGuard.ValidateSafeJson(input.ReasonsJson, nameof(input.ReasonsJson));
        PersistenceGuard.ValidateSafeJson(input.WarningsJson, nameof(input.WarningsJson));
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var snapshot = await OwnedSnapshot(db, actor, input.ExternalSnapshotId, cancellationToken);
        PersistenceGuard.RequireLibrary(context, snapshot.LibraryScopeId);
        if (input.State is TrackMatchState.Accepted or TrackMatchState.Pinned && !input.LibraryTrackId.HasValue ||
            input.State is TrackMatchState.Unresolved or TrackMatchState.Suggested or TrackMatchState.Rejected or TrackMatchState.Ambiguous && input.LibraryTrackId.HasValue)
            throw new ArgumentException("The selected library track does not match the decision state.", nameof(input));
        if (input.State == TrackMatchState.Accepted && input.Confidence < input.Threshold)
            throw new ArgumentException("A match below its acceptance threshold cannot be accepted for automatic action.", nameof(input));
        if (input.LibraryTrackId.HasValue && !await db.LibraryTracks.AnyAsync(item => item.Id == input.LibraryTrackId && item.TenantId == actor.TenantId && item.OwnerUserId == snapshot.OwnerUserId && item.LibraryScopeId == snapshot.LibraryScopeId, cancellationToken))
            throw new UnauthorizedAccessException("The selected library track is outside the snapshot scope.");
        var existing = await db.TrackMatches.AsNoTracking().SingleOrDefaultAsync(item => item.TenantId == actor.TenantId && item.OwnerUserId == snapshot.OwnerUserId && item.LibraryScopeId == snapshot.LibraryScopeId && item.ExternalSnapshotId == snapshot.Id && item.DecisionVersion == input.DecisionVersion, cancellationToken);
        if (existing != null)
        {
            if (existing.State != input.State || existing.LibraryTrackId != input.LibraryTrackId ||
                existing.CanonicalRecordingId != input.CanonicalRecordingId || existing.PolicyVersion != input.PolicyVersion.Trim() ||
                existing.Confidence != input.Confidence || existing.Threshold != input.Threshold)
                throw new InvalidOperationException("The match decision version already exists with different content.");
            return existing;
        }
        var record = new TrackMatchRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = actor.TenantId,
            OwnerUserId = snapshot.OwnerUserId,
            ExternalSnapshotId = snapshot.Id,
            LibraryTrackId = input.LibraryTrackId,
            CanonicalRecordingId = input.CanonicalRecordingId,
            LibraryScopeId = snapshot.LibraryScopeId,
            State = input.State,
            Confidence = input.Confidence,
            Threshold = input.Threshold,
            DecisionVersion = input.DecisionVersion,
            PolicyVersion = input.PolicyVersion.Trim(),
            CandidateResultsJson = input.CandidateResultsJson,
            ReasonsJson = input.ReasonsJson,
            WarningsJson = input.WarningsJson,
            CorrelationId = context.CorrelationId,
            DecidedAt = _clock.UtcNow
        };
        db.TrackMatches.Add(record);
        await db.SaveChangesAsync(cancellationToken);
        return record;
    }

    public async Task<ManualTrackOverrideRecord> SetOverrideAsync(ProtocolExecutionContext context, ManualOverrideInput input, CancellationToken cancellationToken = default)
    {
        var actor = context.RequireActor();
        PersistenceGuard.RequireLibrary(context, input.LibraryScopeId);
        if (string.IsNullOrWhiteSpace(input.Reason) || input.Decision == ManualOverrideDecision.Pin != input.LibraryTrackId.HasValue)
            throw new ArgumentException("Pin requires a local track; Reject must not select one.", nameof(input));
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var snapshot = await OwnedSnapshot(db, actor, input.ExternalSnapshotId, cancellationToken);
        if (snapshot.LibraryScopeId != input.LibraryScopeId) throw new UnauthorizedAccessException("The override library scope does not match the snapshot.");
        if (input.LibraryTrackId.HasValue && !await db.LibraryTracks.AnyAsync(item => item.Id == input.LibraryTrackId && item.TenantId == actor.TenantId && item.OwnerUserId == snapshot.OwnerUserId && item.LibraryScopeId == input.LibraryScopeId, cancellationToken))
            throw new UnauthorizedAccessException("The pinned library track is outside the override scope.");
        var active = await db.ManualTrackOverrides.SingleOrDefaultAsync(item => item.TenantId == actor.TenantId && item.OwnerUserId == snapshot.OwnerUserId && item.LibraryScopeId == input.LibraryScopeId && item.ExternalSnapshotId == snapshot.Id && item.RevokedAt == null, cancellationToken);
        var version = await db.ManualTrackOverrides.Where(item => item.TenantId == actor.TenantId && item.OwnerUserId == snapshot.OwnerUserId && item.ExternalSnapshotId == snapshot.Id).Select(item => (int?)item.DecisionVersion).MaxAsync(cancellationToken) ?? 0;
        if (active != null)
        {
            if (active.Decision == input.Decision && active.LibraryTrackId == input.LibraryTrackId && active.Reason == input.Reason.Trim()) return active;
            active.RevokedAt = _clock.UtcNow;
            active.Revision++;
            await db.SaveChangesAsync(cancellationToken);
        }
        var record = new ManualTrackOverrideRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = actor.TenantId,
            OwnerUserId = snapshot.OwnerUserId,
            ExternalSnapshotId = snapshot.Id,
            LibraryTrackId = input.LibraryTrackId,
            LibraryScopeId = input.LibraryScopeId,
            Decision = input.Decision,
            Reason = input.Reason.Trim(),
            DecisionVersion = version + 1,
            CreatedAt = _clock.UtcNow
        };
        db.ManualTrackOverrides.Add(record);
        await db.SaveChangesAsync(cancellationToken);
        return record;
    }

    public async Task RevokeOverrideAsync(ProtocolExecutionContext context, Guid overrideId, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var actor = context.RequireActor();
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var record = await db.ManualTrackOverrides.SingleOrDefaultAsync(item => item.Id == overrideId && item.TenantId == actor.TenantId, cancellationToken) ?? throw new KeyNotFoundException("Override not found.");
        PersistenceGuard.RequireOwner(actor, record.OwnerUserId);
        PersistenceGuard.RequireLibrary(context, record.LibraryScopeId);
        if (record.Revision != expectedRevision) throw new DbUpdateConcurrencyException("The override changed before revocation.");
        if (record.RevokedAt == null) { record.RevokedAt = _clock.UtcNow; record.Revision++; await db.SaveChangesAsync(cancellationToken); }
    }

    public async Task<ManualTrackOverrideRecord?> GetActiveOverrideAsync(ProtocolExecutionContext context, Guid externalSnapshotId, CancellationToken cancellationToken = default)
    {
        var actor = context.RequireActor();
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var snapshot = await OwnedSnapshot(db, actor, externalSnapshotId, cancellationToken);
        PersistenceGuard.RequireLibrary(context, snapshot.LibraryScopeId);
        return await db.ManualTrackOverrides.AsNoTracking().SingleOrDefaultAsync(item => item.TenantId == actor.TenantId && item.OwnerUserId == snapshot.OwnerUserId && item.LibraryScopeId == snapshot.LibraryScopeId && item.ExternalSnapshotId == snapshot.Id && item.RevokedAt == null, cancellationToken);
    }

    private static async Task<ExternalMetadataSnapshotRecord> OwnedSnapshot(AllstarrDbContext db, ProviderActorContext actor, Guid id, CancellationToken cancellationToken)
    {
        var record = await db.ExternalMetadataSnapshots.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id && item.TenantId == actor.TenantId, cancellationToken) ?? throw new KeyNotFoundException("Snapshot not found.");
        PersistenceGuard.RequireOwner(actor, record.OwnerUserId);
        return record;
    }

    private static void ValidateHash(string value, string name)
    {
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)) || value != value.ToLowerInvariant()) throw new ArgumentException("A normalized SHA-256 value is required.", name);
    }
}

public sealed record PlaylistLinkInput(Guid ProviderAccountId, string SourceProviderId, string SourcePlaylistId, string SourcePlaylistIdHash, string LibraryScopeId, string TargetProtocol, string TargetBackendInstanceId, PlaylistLinkMode Mode, PlaylistMaterializationMode MaterializationMode, string RuleVersion, string PolicyVersion, Guid? ScheduleId = null, string? TargetPlaylistId = null, Guid? TargetCredentialReferenceId = null, bool MirrorStaleEntries = false, bool PreserveManualEntries = true, bool SyncName = true, bool SyncDescription = true, bool SyncArtwork = true);
public sealed record PlaylistLinkUpdate(long ExpectedRevision, PlaylistLinkMode Mode, PlaylistMaterializationMode MaterializationMode, string RuleVersion, string PolicyVersion, Guid? ScheduleId, string? TargetPlaylistId, bool MirrorStaleEntries, bool PreserveManualEntries, bool SyncName, bool SyncDescription, bool SyncArtwork, Guid? TargetCredentialReferenceId = null);
public sealed record PlaylistSourceEntryInput(int Position, Guid ExternalMetadataSnapshotId, string SourceEntryIdHash);
public sealed record PlaylistSourceSnapshotInput(int SnapshotVersion, string ProviderRevision, string? ETag, string Name, string? Description, string? ArtworkReferenceKey, string PayloadSha256, IReadOnlyList<PlaylistSourceEntryInput> Entries, Guid? SourceJobId = null);
public sealed record PersistedPlaylistPreviewEntry(int Position, Guid ExternalSnapshotId, TrackMatchState State, Guid? LibraryTrackId, ManualOverrideDecision? Override);
public sealed record PlaylistPreview(Guid LinkId, Guid SnapshotId, string Name, string? Description, string? ArtworkReferenceKey, IReadOnlyList<PersistedPlaylistPreviewEntry> Entries);
public sealed record PlaylistRunInput(Guid SnapshotId, long Generation, string IdempotencyKey, string RuleVersion, PlaylistMaterializationMode MaterializationMode, PlaylistSyncState State, string? TargetRevisionBefore, Guid? ScheduleId = null, Guid? JobId = null);
public sealed record PlaylistRunEntryInput(Guid SourceEntryId, Guid? TrackMatchId, Guid? LibraryTrackId, int SourcePosition, int? TargetPosition, PlaylistEntryOutcome Outcome, string? OutcomeCode, string DetailsJson);

public interface IPlaylistPersistenceService
{
    Task<PlaylistLinkRecord> CreateLinkAsync(ProtocolExecutionContext context, PlaylistLinkInput input, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PlaylistLinkRecord>> ListLinksAsync(ProtocolExecutionContext context, string libraryScopeId, CancellationToken cancellationToken = default);
    Task<PlaylistLinkRecord> GetLinkAsync(ProtocolExecutionContext context, Guid linkId, CancellationToken cancellationToken = default);
    Task<PlaylistLinkRecord> UpdateLinkAsync(ProtocolExecutionContext context, Guid linkId, PlaylistLinkUpdate update, CancellationToken cancellationToken = default);
    Task<PlaylistSourceSnapshotRecord> CaptureSourceSnapshotAsync(ProtocolExecutionContext context, Guid linkId, PlaylistSourceSnapshotInput input, CancellationToken cancellationToken = default);
    Task<PlaylistPreview> ReadPreviewAsync(ProtocolExecutionContext context, Guid linkId, Guid snapshotId, CancellationToken cancellationToken = default);
    Task<PlaylistSyncRunRecord> RecordRunAsync(ProtocolExecutionContext context, Guid linkId, PlaylistRunInput input, IReadOnlyList<PlaylistRunEntryInput> results, CancellationToken cancellationToken = default);
}

public sealed class PlaylistPersistenceService : IPlaylistPersistenceService
{
    private readonly IDbContextFactory<AllstarrDbContext> _factory;
    private readonly ProviderAccountResolver _accounts;
    private readonly IPlatformClock _clock;
    public PlaylistPersistenceService(IDbContextFactory<AllstarrDbContext> factory, ProviderAccountResolver accounts, IPlatformClock clock) => (_factory, _accounts, _clock) = (factory, accounts, clock);

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
            LibraryScopeId = input.LibraryScopeId,
            SourceProviderId = account.Account.ProviderId,
            SourcePlaylistId = PersistenceGuard.Required(input.SourcePlaylistId, nameof(input.SourcePlaylistId)),
            SourcePlaylistIdHash = input.SourcePlaylistIdHash,
            TargetProtocol = PersistenceGuard.Required(input.TargetProtocol, nameof(input.TargetProtocol)).ToLowerInvariant(),
            TargetBackendInstanceId = PersistenceGuard.Required(input.TargetBackendInstanceId, nameof(input.TargetBackendInstanceId)),
            TargetPlaylistId = input.TargetPlaylistId,
            TargetCredentialReferenceId = input.TargetCredentialReferenceId,
            Mode = input.Mode,
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

    public async Task<IReadOnlyList<PlaylistLinkRecord>> ListLinksAsync(ProtocolExecutionContext context, string libraryScopeId, CancellationToken cancellationToken = default)
    {
        var (_, actor) = PersistenceGuard.Require(context, libraryScopeId); await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var query = db.PlaylistLinks.AsNoTracking().Where(item => item.TenantId == actor.TenantId && item.LibraryScopeId == libraryScopeId);
        if (actor.Kind != ProviderActorKind.Administrator) query = query.Where(item => item.OwnerUserId == actor.EffectiveUserId);
        return await query.OrderBy(item => item.CreatedAt).ToListAsync(cancellationToken);
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
        record.Mode = update.Mode; record.MaterializationMode = update.MaterializationMode; record.RuleVersion = update.RuleVersion.Trim(); record.PolicyVersion = update.PolicyVersion.Trim(); record.ScheduleId = update.ScheduleId; record.TargetPlaylistId = update.TargetPlaylistId; record.TargetCredentialReferenceId = update.TargetCredentialReferenceId; record.MirrorStaleEntries = update.MirrorStaleEntries; record.PreserveManualEntries = update.PreserveManualEntries; record.SyncName = update.SyncName; record.SyncDescription = update.SyncDescription; record.SyncArtwork = update.SyncArtwork; record.UpdatedAt = _clock.UtcNow; record.Revision++;
        await db.SaveChangesAsync(cancellationToken); return record;
    }

    public async Task<PlaylistSourceSnapshotRecord> CaptureSourceSnapshotAsync(ProtocolExecutionContext context, Guid linkId, PlaylistSourceSnapshotInput input, CancellationToken cancellationToken = default)
    {
        var actor = context.RequireActor(); if (input.SnapshotVersion <= 0 || input.Entries.Select((entry, index) => entry.Position == index).Any(valid => !valid)) throw new ArgumentException("Snapshot entries must use contiguous source order.", nameof(input));
        if (input.Entries.Any(entry => entry.SourceEntryIdHash.Length != 64 || entry.SourceEntryIdHash.Any(character => !Uri.IsHexDigit(character)))) throw new ArgumentException("Every source entry requires a SHA-256 identity hash.", nameof(input));
        PersistenceGuard.Required(input.ProviderRevision, nameof(input.ProviderRevision)); PersistenceGuard.Required(input.Name, nameof(input.Name)); PersistenceGuard.ValidateStableReference(input.ArtworkReferenceKey, nameof(input.ArtworkReferenceKey));
        if (input.PayloadSha256.Length != 64) throw new ArgumentException("A payload hash is required.", nameof(input));
        await using var db = await _factory.CreateDbContextAsync(cancellationToken); var link = await db.PlaylistLinks.SingleOrDefaultAsync(item => item.Id == linkId && item.TenantId == actor.TenantId, cancellationToken) ?? throw new KeyNotFoundException("Playlist link not found."); PersistenceGuard.RequireOwner(actor, link.OwnerUserId); PersistenceGuard.RequireLibrary(context, link.LibraryScopeId);
        var existing = await db.PlaylistSourceSnapshots.AsNoTracking().SingleOrDefaultAsync(item => item.TenantId == actor.TenantId && item.PlaylistLinkId == link.Id && item.SnapshotVersion == input.SnapshotVersion, cancellationToken); if (existing != null) { if (existing.PayloadSha256 != input.PayloadSha256) throw new InvalidOperationException("The immutable playlist snapshot version already has different content."); return existing; }
        var ids = input.Entries.Select(item => item.ExternalMetadataSnapshotId).Distinct().ToArray(); var owned = await db.ExternalMetadataSnapshots.CountAsync(item => ids.Contains(item.Id) && item.TenantId == actor.TenantId && item.OwnerUserId == link.OwnerUserId && item.LibraryScopeId == link.LibraryScopeId && item.ProviderAccountId == link.ProviderAccountId, cancellationToken); if (owned != ids.Length) throw new UnauthorizedAccessException("A source entry snapshot is outside the playlist scope or account.");
        var snapshot = new PlaylistSourceSnapshotRecord { Id = Guid.CreateVersion7(), TenantId = actor.TenantId, OwnerUserId = link.OwnerUserId, PlaylistLinkId = link.Id, ProviderAccountId = link.ProviderAccountId, SourceJobId = input.SourceJobId, SnapshotVersion = input.SnapshotVersion, ProviderRevision = input.ProviderRevision.Trim(), ETag = input.ETag, Name = input.Name.Trim(), Description = input.Description?.Trim(), ArtworkReferenceKey = input.ArtworkReferenceKey, PayloadSha256 = input.PayloadSha256, CorrelationId = context.CorrelationId, RetrievedAt = _clock.UtcNow };
        db.PlaylistSourceSnapshots.Add(snapshot); await db.SaveChangesAsync(cancellationToken);
        db.PlaylistSourceEntries.AddRange(input.Entries.Select(entry => new PlaylistSourceEntryRecord { Id = Guid.CreateVersion7(), TenantId = actor.TenantId, PlaylistSourceSnapshotId = snapshot.Id, ExternalMetadataSnapshotId = entry.ExternalMetadataSnapshotId, SourcePosition = entry.Position, SourceEntryIdHash = entry.SourceEntryIdHash })); await db.SaveChangesAsync(cancellationToken); return snapshot;
    }

    public async Task<PlaylistPreview> ReadPreviewAsync(ProtocolExecutionContext context, Guid linkId, Guid snapshotId, CancellationToken cancellationToken = default)
    {
        var actor = context.RequireActor(); await using var db = await _factory.CreateDbContextAsync(cancellationToken); var link = await db.PlaylistLinks.AsNoTracking().SingleOrDefaultAsync(item => item.Id == linkId && item.TenantId == actor.TenantId, cancellationToken) ?? throw new KeyNotFoundException("Playlist link not found."); PersistenceGuard.RequireOwner(actor, link.OwnerUserId); PersistenceGuard.RequireLibrary(context, link.LibraryScopeId); var snapshot = await db.PlaylistSourceSnapshots.AsNoTracking().SingleOrDefaultAsync(item => item.Id == snapshotId && item.TenantId == actor.TenantId && item.PlaylistLinkId == link.Id, cancellationToken) ?? throw new KeyNotFoundException("Playlist snapshot not found.");
        var entries = await db.PlaylistSourceEntries.AsNoTracking().Where(item => item.TenantId == actor.TenantId && item.PlaylistSourceSnapshotId == snapshot.Id).OrderBy(item => item.SourcePosition).ToListAsync(cancellationToken); var result = new List<PersistedPlaylistPreviewEntry>(entries.Count);
        foreach (var entry in entries) { var manual = await db.ManualTrackOverrides.AsNoTracking().Where(item => item.TenantId == actor.TenantId && item.OwnerUserId == link.OwnerUserId && item.LibraryScopeId == link.LibraryScopeId && item.ExternalSnapshotId == entry.ExternalMetadataSnapshotId && item.RevokedAt == null).SingleOrDefaultAsync(cancellationToken); var match = await db.TrackMatches.AsNoTracking().Where(item => item.TenantId == actor.TenantId && item.OwnerUserId == link.OwnerUserId && item.LibraryScopeId == link.LibraryScopeId && item.ExternalSnapshotId == entry.ExternalMetadataSnapshotId).OrderByDescending(item => item.DecisionVersion).FirstOrDefaultAsync(cancellationToken); result.Add(new PersistedPlaylistPreviewEntry(entry.SourcePosition, entry.ExternalMetadataSnapshotId, manual?.Decision == ManualOverrideDecision.Pin ? TrackMatchState.Pinned : manual?.Decision == ManualOverrideDecision.Reject ? TrackMatchState.Rejected : match?.State ?? TrackMatchState.Unresolved, manual?.LibraryTrackId ?? match?.LibraryTrackId, manual?.Decision)); }
        return new PlaylistPreview(link.Id, snapshot.Id, snapshot.Name, snapshot.Description, snapshot.ArtworkReferenceKey, result);
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

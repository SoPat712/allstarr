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
    public async Task<ManualTrackOverrideRecord> SetOverrideAsync(
        ProtocolExecutionContext context,
        ManualOverrideInput input,
        CancellationToken cancellationToken = default)
    {
        var actor = context.RequireActor();

        if (string.IsNullOrWhiteSpace(input.Reason) ||
            input.Decision == ManualOverrideDecision.Pin != input.LibraryTrackId.HasValue)
            throw new ArgumentException(
                "Pin requires a local track; Reject must not select one.",
                nameof(input));

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var snapshot = await OwnedSnapshotAsync(db, actor, input.ExternalSnapshotId, cancellationToken);
        var latestDecision = await db.TrackMatches.AsNoTracking()
            .Where(item => item.ExternalSnapshotId == snapshot.Id)
            .OrderByDescending(item => item.DecisionVersion)
            .FirstOrDefaultAsync(cancellationToken);
        var overrideTrackId = input.Decision == ManualOverrideDecision.Pin
            ? input.LibraryTrackId
            : TrackMatchOverridePolicy.TopCandidateLibraryTrackId(
                  latestDecision?.CandidateResultsJson) ?? latestDecision?.LibraryTrackId;
        var access = await libraryAccess.ResolveAsync(context, cancellationToken);
        if (overrideTrackId.HasValue &&
            !await LibraryTrackAccess.Query(db, context, access).AnyAsync(item =>
                    item.Id == overrideTrackId && item.BackendInstanceId == snapshot.BackendInstanceId,
                cancellationToken))
            throw new UnauthorizedAccessException(
                "The selected library track is unavailable to this viewer.");

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var record = await ReplaceOverrideAsync(db, snapshot,
            actor.EffectiveUserId ?? throw new UnauthorizedAccessException("A user owner is required."),
            input.Decision, overrideTrackId, null, null, input.Reason.Trim(), input.ExpectedAuthority, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return record;
    }

    public async Task RevokeOverrideAsync(
        ProtocolExecutionContext context,
        Guid overrideId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var actor = context.RequireActor();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var record = await db.ManualTrackOverrides.SingleOrDefaultAsync(item =>
            item.Id == overrideId,
            cancellationToken) ?? throw new KeyNotFoundException("Override not found.");
        if (!record.OwnerUserId.HasValue || record.OwnerUserId != actor.EffectiveUserId)
            throw new UnauthorizedAccessException("Only the owner may clear a personal choice here.");

        if (record.Revision != expectedRevision)
            throw new DbUpdateConcurrencyException(
                "The override changed before revocation.");
        if (record.RevokedAt != null) return;
        record.RevokedAt = clock.UtcNow;
        record.Revision++;
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<ManualTrackAuthorityCommandResult> ClearManualAuthorityAsync(
        TrackMatchActor actor,
        Guid externalSnapshotId,
        ManualTrackAuthorityKind kind,
        Guid authorityId,
        long expectedRevision,
        string correlationId,
        CancellationToken cancellationToken = default,
        string authorityScope = "personal") =>
        ReleaseManualAuthorityAsync(
            actor,
            externalSnapshotId,
            kind,
            authorityId,
            expectedRevision,
            correlationId,
            "manual-authority.delete",
            authorityScope,
            cancellationToken);

    public async Task<TrackRematchCommandResult> RematchManualAuthorityAsync(
        ProtocolExecutionContext context,
        Guid externalSnapshotId,
        ManualTrackAuthorityKind kind,
        Guid authorityId,
        long expectedRevision,
        string correlationId,
        CancellationToken cancellationToken = default,
        string authorityScope = "personal")
    {
        var actor = context.RequireActor();
        var matchActor = new TrackMatchActor(

            actor.EffectiveUserId ?? throw new UnauthorizedAccessException("A user owner is required."),
            actor.Kind == ProviderActorKind.Administrator);
        var released = await ReleaseManualAuthorityAsync(
            matchActor,
            externalSnapshotId,
            kind,
            authorityId,
            expectedRevision,
            correlationId,
            "manual-authority.rematch",
            authorityScope,
            cancellationToken);
        if (!released.Succeeded)
            return new(false, released.Failure, released.Error);

        return await CoalesceRematchAsync(
            matchActor,
            externalSnapshotId,
            () => RematchSnapshotAsync(
                matchActor,
                externalSnapshotId,
                correlationId,
                ManualTrackAuthorityPolicy.RematchPolicyVersion,
                context,
                released.ReleasedProviderIdentityId,
                ConcurrentWriteRetries,
                cancellationToken),
            cancellationToken);
    }

    private async Task<ManualTrackAuthorityCommandResult> ReleaseManualAuthorityAsync(
        TrackMatchActor actor,
        Guid externalSnapshotId,
        ManualTrackAuthorityKind kind,
        Guid authorityId,
        long expectedRevision,
        string correlationId,
        string auditAction,
        string authorityScope,
        CancellationToken cancellationToken)
    {
        if (expectedRevision < 0)
            return new(false, TrackMatchCommandFailure.Invalid, "ExpectedRevision is invalid");

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.Users.AnyAsync(user => user.Id == actor.UserId && user.Enabled, cancellationToken))
            return new(false, TrackMatchCommandFailure.Forbidden, "The user is unavailable");
        var snapshot = await db.ExternalMetadataSnapshots.SingleOrDefaultAsync(item =>
            item.Id == externalSnapshotId,
            cancellationToken);
        if (snapshot == null)
            return new(false, TrackMatchCommandFailure.NotFound, "Track snapshot was not found");
        if (!actor.IsAdministrator && snapshot.OwnerUserId != actor.UserId)
            return new(false, TrackMatchCommandFailure.Forbidden, "Track snapshot is outside your account");

        if (authorityScope is not ("personal" or "household"))
            return new(false, TrackMatchCommandFailure.Invalid, "AuthorityScope must be personal or household");
        if (authorityScope == "household" && !actor.IsAdministrator)
            return new(false, TrackMatchCommandFailure.Forbidden, "Household choices require administrator permissions");
        var ownerId = authorityScope == "personal" ? (Guid?)actor.UserId : null;
        var record = await ManualTrackOverrides.ForSource(db, snapshot).SingleOrDefaultAsync(item =>
            item.Id == authorityId && item.OwnerUserId == ownerId, cancellationToken);
        if (record == null)
            return new(false, TrackMatchCommandFailure.NotFound, "Manual decision was not found in this scope");
        var actualKind = record.Decision == ManualOverrideDecision.Reject
            ? ManualTrackAuthorityKind.Rejection
            : record.TargetProviderId != null ? ManualTrackAuthorityKind.ProviderMatch : ManualTrackAuthorityKind.LocalMatch;
        if (actualKind != kind || record.RevokedAt.HasValue || record.Revision != expectedRevision)
            return new(false, TrackMatchCommandFailure.Conflict, "The manual decision changed; refresh and try again");
        record.RevokedAt = clock.UtcNow;
        record.Revision++;

        db.AuditEvents.Add(new AuditEventRecord
        {
            Id = Guid.CreateVersion7(),

            ActorUserId = actor.UserId,
            Category = "track-match",
            Action = auditAction,
            Outcome = "released",
            CorrelationId = correlationId,
            DetailsJson = JsonSerializer.Serialize(new
            {
                externalSnapshotId,
                authorityId,
                authorityKind = kind.ToString().ToLowerInvariant(),
                authorityScope,
                expectedRevision
            }),
            CreatedAt = clock.UtcNow
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new(false, TrackMatchCommandFailure.Conflict,
                "The manual decision changed; refresh and try again");
        }
        return new(
            true,
            ExternalSnapshotId: snapshot.Id);
    }

    public async Task<ManualTrackOverrideRecord?> GetActiveOverrideAsync(
        ProtocolExecutionContext context,
        Guid externalSnapshotId,
        CancellationToken cancellationToken = default)
    {
        var actor = context.RequireActor();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var snapshot = await OwnedSnapshotAsync(db, actor, externalSnapshotId, cancellationToken);

        return (await ManualTrackOverrides.ReadAsync(db, snapshot,
            actor.EffectiveUserId ?? throw new UnauthorizedAccessException("A user owner is required."),
            cancellationToken)).Effective;
    }

    public async Task<ManualTrackOverrideRecord?> FindOverrideAsync(
        Guid overrideId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.ManualTrackOverrides.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == overrideId,
            cancellationToken);
    }
}

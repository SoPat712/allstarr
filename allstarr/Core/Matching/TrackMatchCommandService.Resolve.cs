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
using allstarr.Services.Spotify;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Matching;

public sealed partial class TrackMatchCommandService
{
    public async Task<TrackMatchCommandResult> ClearSpotifyAsync(
        TrackMatchActor actor,
        string spotifyId,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        spotifyId = spotifyId.Trim();
        if (spotifyId.Length is < 3 or > 128)
            return TrackMatchCommandResult.Fail(TrackMatchCommandFailure.Invalid, "Spotify track id is invalid");

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var identityIds = await db.ProviderTrackIdentities.AsNoTracking()
            .Where(item => item.ProviderId == "spotify" &&
                           item.ExternalId == spotifyId)
            .Select(item => item.Id)
            .ToArrayAsync(cancellationToken);
        if (identityIds.Length == 0)
            return TrackMatchCommandResult.Fail(TrackMatchCommandFailure.NotFound, "Spotify track is not indexed");

        var snapshots = db.ExternalMetadataSnapshots
            .Where(item => item.ProviderTrackIdentityId.HasValue &&
                           identityIds.Contains(item.ProviderTrackIdentityId.Value));
        if (!actor.IsAdministrator)
            snapshots = snapshots.Where(item => item.OwnerUserId == actor.UserId);
        var snapshot = await snapshots.OrderByDescending(item => item.RetrievedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (snapshot == null)
            return TrackMatchCommandResult.Fail(TrackMatchCommandFailure.NotFound, "Spotify track has no match snapshot");

        var activeOverrides = await ManualTrackOverrides.ForSource(db, snapshot)
            .Where(item => item.OwnerUserId == actor.UserId && item.RevokedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var activeOverride in activeOverrides)
        {
            activeOverride.RevokedAt = clock.UtcNow;
            activeOverride.Revision++;
        }
        await db.SaveChangesAsync(cancellationToken);
        return TrackMatchCommandResult.Success(snapshot.Id);
    }

    public async Task<TrackMatchCommandResult> ResolveSpotifyAsync(
        TrackMatchActor actor,
        string spotifyId,
        ResolveTrackMatchCommand command,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        spotifyId = spotifyId.Trim();
        if (spotifyId.Length is < 3 or > 128)
            return TrackMatchCommandResult.Fail(TrackMatchCommandFailure.Invalid, "Spotify track id is invalid");

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var identityIds = await db.ProviderTrackIdentities.AsNoTracking()
            .Where(item => item.ProviderId == "spotify" &&
                           item.ExternalId == spotifyId)
            .Select(item => item.Id)
            .ToArrayAsync(cancellationToken);
        if (identityIds.Length == 0)
            return TrackMatchCommandResult.Fail(TrackMatchCommandFailure.NotFound, "Spotify track is not indexed");

        var snapshots = db.ExternalMetadataSnapshots.AsNoTracking()
            .Where(item => item.ProviderTrackIdentityId.HasValue &&
                           identityIds.Contains(item.ProviderTrackIdentityId.Value));
        if (!actor.IsAdministrator)
            snapshots = snapshots.Where(item => item.OwnerUserId == actor.UserId);
        var snapshotId = await snapshots.OrderByDescending(item => item.RetrievedAt)
            .Select(item => (Guid?)item.Id)
            .FirstOrDefaultAsync(cancellationToken);
        return snapshotId.HasValue
            ? await ResolveSnapshotAsync(actor, snapshotId.Value, command, correlationId, cancellationToken)
            : TrackMatchCommandResult.Fail(TrackMatchCommandFailure.NotFound, "Spotify track has no match snapshot");
    }

    public async Task<TrackMatchCommandResult> ResolveSnapshotAsync(
        TrackMatchActor actor,
        Guid externalSnapshotId,
        ResolveTrackMatchCommand command,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        var targetType = command.TargetType?.Trim().ToLowerInvariant() ?? string.Empty;
        if (targetType is not ("local" or "provider" or "reject"))
            return TrackMatchCommandResult.Fail(TrackMatchCommandFailure.Invalid, "TargetType must be local, provider, or reject");
        if (command.AuthorityScope is not ("personal" or "household"))
            return TrackMatchCommandResult.Fail(TrackMatchCommandFailure.Invalid, "AuthorityScope must be personal or household");
        if (command.AuthorityScope == "household" && !actor.IsAdministrator)
            return TrackMatchCommandResult.Fail(TrackMatchCommandFailure.Forbidden, "Household choices require administrator permissions");

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.Users.AnyAsync(user => user.Id == actor.UserId && user.Enabled, cancellationToken))
            return TrackMatchCommandResult.Fail(TrackMatchCommandFailure.Forbidden, "The user is unavailable");
        var snapshot = await db.ExternalMetadataSnapshots.SingleOrDefaultAsync(
            item => item.Id == externalSnapshotId, cancellationToken);
        if (snapshot == null)
            return TrackMatchCommandResult.Fail(TrackMatchCommandFailure.NotFound, "Track snapshot was not found");
        if (!actor.IsAdministrator && snapshot.OwnerUserId != actor.UserId)
            return TrackMatchCommandResult.Fail(TrackMatchCommandFailure.Forbidden, "Track snapshot is outside your account");

        Guid? libraryTrackId = null;
        string? providerId = null, externalId = null;
        if (targetType == "local")
        {
            var localQuery = (await AccessibleTracksAsync(db, actor, cancellationToken))
                .Where(item => item.BackendInstanceId == snapshot.BackendInstanceId);
            LibraryTrackRecord? localTrack;
            if (command.LibraryTrackId.HasValue)
                localTrack = await localQuery.SingleOrDefaultAsync(item => item.Id == command.LibraryTrackId.Value, cancellationToken);
            else if (!string.IsNullOrWhiteSpace(command.BackendItemId))
                localTrack = await localQuery.OrderBy(item => item.BackendLibraryId).ThenBy(item => item.Id)
                    .FirstOrDefaultAsync(item => item.BackendItemId == command.BackendItemId, cancellationToken);
            else
                return TrackMatchCommandResult.Fail(TrackMatchCommandFailure.Invalid, "LibraryTrackId or BackendItemId is required for a local match");
            if (localTrack == null)
                return TrackMatchCommandResult.Fail(TrackMatchCommandFailure.NotFound, "Local track was not found");
            libraryTrackId = localTrack.Id;
        }
        else if (targetType == "provider")
        {
            providerId = command.ExternalProvider?.Trim().ToLowerInvariant();
            externalId = command.ExternalId?.Trim();
            if (string.IsNullOrWhiteSpace(providerId) || providerId.Length > 100 ||
                string.IsNullOrWhiteSpace(externalId) || externalId.Length > 500)
                return TrackMatchCommandResult.Fail(TrackMatchCommandFailure.Invalid, "ExternalProvider and ExternalId are required for a provider match");
            var access = playableSearch == null ? null : await libraryAccess.ResolveUserAsync(actor.UserId, cancellationToken);
            if (!ExternalTrackPlaybackPolicy.CanUseForPlayback(providerId, externalId) ||
                playableSearch != null && (access?.Context?.Actor is not { } viewer ||
                    !await playableSearch.CanUseProviderAsync(viewer, providerId, cancellationToken)))
                return TrackMatchCommandResult.Fail(TrackMatchCommandFailure.Invalid, "That provider cannot supply playback audio");
        }
        else
        {
            var latest = await db.TrackMatches.AsNoTracking()
                .Where(item => item.ExternalSnapshotId == snapshot.Id)
                .OrderByDescending(item => item.DecisionVersion).FirstOrDefaultAsync(cancellationToken);
            libraryTrackId = TrackMatchOverridePolicy.TopCandidateLibraryTrackId(latest?.CandidateResultsJson) ?? latest?.LibraryTrackId;
        }

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var ownerId = command.AuthorityScope == "personal" ? (Guid?)actor.UserId : null;
            await ReplaceOverrideAsync(db, snapshot, ownerId,
                targetType == "reject" ? ManualOverrideDecision.Reject : ManualOverrideDecision.Pin,
                libraryTrackId, providerId, externalId,
                CleanReason(command.Reason, targetType == "reject" ? "Rejected during manual review" : "Selected during manual review"),
                command.ExpectedAuthority, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception exception) when (DbErrors.IsUniqueViolation(exception) || DbErrors.IsTransientConflict(exception))
        {
            return TrackMatchCommandResult.Fail(TrackMatchCommandFailure.Conflict, "The selected authority changed; refresh and try again");
        }
        return TrackMatchCommandResult.Success(snapshot.Id);
    }
}

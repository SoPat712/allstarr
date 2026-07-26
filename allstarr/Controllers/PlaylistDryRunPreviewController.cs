using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Playlists.Sources;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Filters;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/playlist-preview")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class PlaylistDryRunPreviewController(
    IProviderRegistry providers,
    ProviderPlaylistSnapshotCollector collector,
    ProviderAccountResolver accountResolver,
    ILibraryIndexService libraryIndex,
    TrackMatchDecisionEngine matcher,
    IDbContextFactory<AllstarrDbContext> contextFactory) : ControllerBase
{
    private const int MaximumPreviewTracks = 2_000;
    private const int MaximumReturnedEntries = 200;

    [HttpPost]
    public async Task<IActionResult> Preview(
        [FromBody] PlaylistDryRunPreviewRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetAdministrator(out var session, out var authError)) return authError!;
        if (!session.TenantId.HasValue || !session.AllstarrUserId.HasValue)
            return Conflict(new { error = "The administrator session is not linked to an Allstarr user." });
        if (request.ProviderAccountId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.PlaylistId) ||
            string.IsNullOrWhiteSpace(request.LibraryScopeId))
            return BadRequest(new { error = "A source account, source playlist, and target library are required." });

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        await using var db = await contextFactory.CreateDbContextAsync(deadline.Token);
        var identity = await db.BackendIdentities.AsNoTracking()
            .Where(item => item.TenantId == session.TenantId.Value &&
                           item.UserId == session.AllstarrUserId.Value)
            .OrderByDescending(item => item.LastSeenAt)
            .FirstOrDefaultAsync(deadline.Token);
        if (identity == null)
            return Conflict(new { error = "No verified backend identity is available for this administrator." });

        var protocol = identity.BackendType.ToLowerInvariant() switch
        {
            "jellyfin" => ProtocolKind.Jellyfin,
            "subsonic" => ProtocolKind.Subsonic,
            _ => ProtocolKind.Unknown
        };
        if (protocol == ProtocolKind.Unknown)
            return BadRequest(new { error = "The active backend does not support playlist preview." });

        var account = await db.ProviderAccounts.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == request.ProviderAccountId && item.Enabled, deadline.Token);
        if (account == null)
            return NotFound(new { error = "The selected playlist account is unavailable." });
        var principal = new AllstarrPrincipal(
            session.TenantId.Value,
            session.AllstarrUserId.Value,
            identity.BackendType,
            identity.BackendInstanceId,
            identity.PrincipalId,
            identity.DisplayName ?? session.UserName,
            true);
        ResolvedProviderAccount? resolvedAccount;
        try
        {
            resolvedAccount = await accountResolver.ResolveAsync(
                new ProviderAccountResolutionRequest(
                    principal,
                    account.ProviderId,
                    "playlist",
                    account.Id,
                    request.LibraryScopeId.Trim()),
                deadline.Token);
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "The selected playlist account is outside the signed-in user and library scope."
            });
        }

        if (resolvedAccount == null)
            return NotFound(new { error = "The selected playlist account is unavailable." });
        account = resolvedAccount.Account;

        if (!providers.TryGetCapability<IProviderPlaylistCapability>(
                account.ProviderId, ProviderCapabilityKind.Playlist, out var playlistCapability) ||
            playlistCapability == null)
            return BadRequest(new { error = "The selected account provider cannot read playlists." });

        var correlationId = HttpContext.TraceIdentifier.Length <= 100
            ? HttpContext.TraceIdentifier
            : HttpContext.TraceIdentifier[..100];
        var execution = new ProtocolExecutionContext(
            protocol,
            identity.BackendInstanceId,
            identity.PrincipalId,
            principal,
            correlationId,
            DateTimeOffset.UtcNow.AddSeconds(60),
            deadline.Token,
            libraryScopeId: request.LibraryScopeId.Trim());
        var actor = execution.RequireActor();
        var accountContext = new ProviderAccountContext(
            account.Id,
            account.ProviderId,
            account.Scope,
            account.Revision,
            account.Enabled,
            account.TenantId,
            account.OwnerUserId,
            account.LibraryScopeId,
            "playlist-dry-run",
            account.SecretReferenceId);
        var providerExecution = new ProviderExecutionContext(
            actor,
            account.ProviderId,
            accountContext,
            new ProviderLibraryContext(actor.TenantId, request.LibraryScopeId.Trim()),
            new ProviderExecutionPolicy(
                new ProviderQualityPolicy(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true),
                ProviderExplicitContentPolicy.Allow,
                allowFallback: false,
                allowSharedAccount: account.Scope == ProviderAccountScope.Global,
                allowManagedDownloads: false,
                [account.ProviderId]),
            "playlist-dry-run-source",
            correlationId,
            DateTimeOffset.UtcNow.AddSeconds(60),
            deadline.Token);

        try
        {
            var collection = await collector.CollectAsync(
                playlistCapability,
                providerExecution,
                new ProviderPlaylistSnapshotRequest(
                    new ProviderExternalResourceId(
                        account.ProviderId,
                        ProviderResourceKind.Playlist,
                        request.PlaylistId.Trim()),
                    PageSize: 100));
            if (!collection.IsSuccess || collection.Snapshot == null)
                return UnprocessableEntity(new
                {
                    error = "The source playlist could not be read.",
                    providerError = collection.Error?.Kind.ToString(),
                    pagesRead = collection.PagesRead
                });

            var snapshot = collection.Snapshot;
            if (snapshot.Entries.Count > MaximumPreviewTracks)
                return StatusCode(StatusCodes.Status413PayloadTooLarge, new
                {
                    error = $"Preview is limited to {MaximumPreviewTracks:N0} tracks.",
                    sourceTracks = snapshot.Entries.Count
                });

            var candidates = await libraryIndex.GetMatchCandidatesAsync(
                execution,
                request.LibraryScopeId.Trim(),
                deadline.Token);
            var candidateIndex = new TrackMatchCandidateIndex(candidates);
            var candidateById = candidates.ToDictionary(item => item.LibraryTrackId);
            var enabledProviderIds = (await db.ProviderAccounts.AsNoTracking()
                    .Where(item => item.Enabled &&
                        (item.Scope == ProviderAccountScope.Global ||
                         item.TenantId == actor.TenantId &&
                         (item.Scope == ProviderAccountScope.Library && item.LibraryScopeId == request.LibraryScopeId.Trim() ||
                          item.Scope == ProviderAccountScope.User && item.OwnerUserId == actor.EffectiveUserId)))
                    .Select(item => item.ProviderId)
                    .Distinct()
                    .ToListAsync(deadline.Token))
                .ToHashSet(StringComparer.Ordinal);
            var playableProviderIds = providers.FindByCapability(ProviderCapabilityKind.Streaming, includeNonOperational: false)
                .Concat(providers.FindByCapability(ProviderCapabilityKind.Download, includeNonOperational: false))
                .Select(item => item.Id)
                .Where(enabledProviderIds.Contains)
                .ToHashSet(StringComparer.Ordinal);
            var canonicalIds = snapshot.Entries.Where(item => item.CanonicalRecordingId.HasValue)
                .Select(item => item.CanonicalRecordingId!.Value).Distinct().ToArray();
            var providerIdentities = await db.ProviderTrackIdentities.AsNoTracking()
                .Where(item => item.TenantId == actor.TenantId &&
                    canonicalIds.Contains(item.CanonicalRecordingId) &&
                    (item.Verification == ProviderIdentityVerification.Verified ||
                     item.Verification == ProviderIdentityVerification.Pinned))
                .ToListAsync(deadline.Token);
            var playableByCanonical = providerIdentities
                .Where(item => playableProviderIds.Contains(item.ProviderId))
                .GroupBy(item => item.CanonicalRecordingId)
                .ToDictionary(group => group.Key,
                    group => group.Select(item => item.ProviderId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
            var scope = new TrackMatchScope(
                actor.TenantId,
                actor.EffectiveUserId!.Value,
                identity.BackendInstanceId,
                request.LibraryScopeId.Trim(),
                account.Id,
                PolicyVersion: 1,
                SourceSnapshotVersion: 1);
            var decisions = snapshot.Entries.Select(entry =>
                {
                    var local = Decide(entry, account.ProviderId, scope, candidateIndex, matcher);
                    var providerMatches = entry.CanonicalRecordingId.HasValue &&
                                          playableByCanonical.TryGetValue(entry.CanonicalRecordingId.Value, out var routes)
                        ? routes
                        : [];
                    return local with { ProviderMatchIds = providerMatches };
                })
                .ToArray();
            var accepted = decisions.Count(item => item.Decision.State == TrackMatchReviewState.Accepted);
            var providerMatched = decisions.Count(item => item.ProviderMatchIds.Count > 0);
            var suggested = decisions.Count(item => item.Decision.State == TrackMatchReviewState.Suggested);
            var ambiguous = decisions.Count(item => item.Decision.State == TrackMatchReviewState.Ambiguous);
            var unresolved = decisions.Count(item =>
                item.Decision.State != TrackMatchReviewState.Accepted &&
                item.ProviderMatchIds.Count == 0);
            var duplicateLocalIds = decisions
                .Where(item => item.Decision.State == TrackMatchReviewState.Accepted &&
                               item.Decision.SelectedLibraryTrackId.HasValue)
                .GroupBy(item => item.Decision.SelectedLibraryTrackId!.Value)
                .Sum(group => Math.Max(0, group.Count() - 1));

            return Ok(new
            {
                dryRun = true,
                writesPerformed = false,
                source = new
                {
                    providerId = snapshot.ProviderId,
                    providerAccountId = snapshot.ProviderAccountId,
                    playlistId = request.PlaylistId.Trim(),
                    snapshot.Name,
                    snapshot.Description,
                    artworkUrl = $"/api/admin/playlist-sources/{account.Id}/playlists/{Uri.EscapeDataString(request.PlaylistId.Trim())}/artwork",
                    tracks = snapshot.Entries.Count,
                    collection.PagesRead
                },
                target = new
                {
                    protocol = identity.BackendType,
                    backendInstanceId = identity.BackendInstanceId,
                    libraryScopeId = request.LibraryScopeId.Trim(),
                    playlistId = string.IsNullOrWhiteSpace(request.TargetPlaylistId) ? null : request.TargetPlaylistId.Trim()
                },
                summary = new
                {
                    total = decisions.Length,
                    localMatches = accepted,
                    providerMatches = providerMatched,
                    suggested,
                    ambiguous,
                    unresolved,
                    duplicateLocalTracks = duplicateLocalIds,
                    estimatedAdds = decisions.Count(item =>
                        item.Decision.State == TrackMatchReviewState.Accepted ||
                        item.ProviderMatchIds.Count > 0) - duplicateLocalIds,
                    estimatedSkips = unresolved + duplicateLocalIds
                },
                providerRouteEvaluation = "verified-identity-and-enabled-account",
                warnings = new[]
                {
                    "Provider matches require a verified identity and enabled streaming or download account; final route health is checked at playback.",
                    "The target is not mutated and may change before the first sync."
                },
                entries = decisions.Take(MaximumReturnedEntries).Select(item =>
                {
                    var selected = item.Decision.SelectedLibraryTrackId.HasValue &&
                                   candidateById.TryGetValue(item.Decision.SelectedLibraryTrackId.Value, out var candidate)
                        ? candidate
                        : null;
                    return new
                    {
                        position = item.Entry.SourcePosition,
                        item.Entry.Title,
                        artists = item.Entry.Artists,
                        item.Entry.Album,
                        durationMilliseconds = item.Entry.DurationMilliseconds,
                        state = item.Decision.State == TrackMatchReviewState.Accepted
                            ? "accepted"
                            : item.ProviderMatchIds.Count > 0
                                ? "provider-match"
                                : item.Decision.State.ToString().ToLowerInvariant(),
                        providerId = item.ProviderMatchIds.FirstOrDefault(),
                        providerIds = item.ProviderMatchIds,
                        confidence = Math.Round(item.Decision.Confidence, 4),
                        libraryTrackId = selected?.LibraryTrackId,
                        backendItemId = selected?.BackendItemId,
                        reasons = item.Decision.Reasons,
                        warnings = item.Decision.Warnings
                    };
                }),
                returnedEntries = Math.Min(decisions.Length, MaximumReturnedEntries),
                entriesTruncated = decisions.Length > MaximumReturnedEntries,
                measuredAt = DateTimeOffset.UtcNow
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return StatusCode(499, new { error = "The preview was canceled." });
        }
        catch (OperationCanceledException)
        {
            return StatusCode(StatusCodes.Status504GatewayTimeout, new
            {
                error = "The no-write preview exceeded its 60-second limit."
            });
        }
    }

    private static PreviewDecision Decide(
        CollectedPlaylistSourceEntry entry,
        string providerId,
        TrackMatchScope scope,
        TrackMatchCandidateIndex candidates,
        TrackMatchDecisionEngine matcher)
    {
        if (string.IsNullOrWhiteSpace(entry.Title) || entry.Artists.Count == 0)
            return new(entry, new TrackMatchDecision(
                TrackMatchReviewState.Unresolved,
                null,
                null,
                0,
                [],
                [],
                ["source_metadata_incomplete"],
                scope.PolicyVersion,
                scope.SourceSnapshotVersion));

        var source = new ExternalTrackMatchSnapshot(
            entry.SourceEntryIdHash,
            providerId,
            entry.ProviderTrackIdHash,
            entry.Title,
            string.Join(", ", entry.Artists),
            entry.Album,
            null,
            entry.DurationMilliseconds,
            entry.Isrc,
            null,
            entry.IsExplicit);
        return new(entry, matcher.Decide(scope, source, candidates.Select(source)));
    }

    private bool TryGetAdministrator(out AdminAuthSession session, out IActionResult? error)
    {
        session = null!;
        error = null;
        if (!HttpContext.Items.TryGetValue(AdminAuthSessionService.HttpContextSessionItemKey, out var value) ||
            value is not AdminAuthSession current)
        {
            error = Unauthorized(new { error = "Authentication required" });
            return false;
        }
        if (!current.IsAdministrator)
        {
            error = StatusCode(StatusCodes.Status403Forbidden, new { error = "Administrator access required" });
            return false;
        }
        session = current;
        return true;
    }

    private sealed record PreviewDecision(
        CollectedPlaylistSourceEntry Entry,
        TrackMatchDecision Decision,
        IReadOnlyList<string> ProviderMatchIds = null!)
    {
        public IReadOnlyList<string> ProviderMatchIds { get; init; } = ProviderMatchIds ?? [];
    }
}

public sealed class PlaylistDryRunPreviewRequest
{
    public Guid ProviderAccountId { get; set; }
    public string PlaylistId { get; set; } = string.Empty;
    public string LibraryScopeId { get; set; } = string.Empty;
    public string? TargetPlaylistId { get; set; }
}

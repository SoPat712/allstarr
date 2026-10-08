using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Playlists.Sources;
using allstarr.Core.Playlists.Targets;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Playlists;

public sealed record ProviderPlaylistUpdateChange(
    string Kind,
    int? FromPosition,
    int? ToPosition,
    string Title,
    string Artist);

public sealed record ProviderPlaylistUpdateSkip(
    int Position,
    string Title,
    string Artist,
    string Reason);

public sealed class ProviderPlaylistUpdatePlan
{
    public required Guid PlaylistLinkId { get; init; }
    public required long LinkRevision { get; init; }
    public required string ProviderId { get; init; }
    public required string ProviderName { get; init; }
    public required string SourcePlaylistName { get; init; }
    public required string BackendPlaylistName { get; init; }
    public required string BackendProtocol { get; init; }
    public required string SourceVersion { get; init; }
    public required string ConfirmationId { get; init; }
    public required int CurrentCount { get; init; }
    public required int IncludedCount { get; init; }
    public required int DuplicateCount { get; init; }
    public required IReadOnlyList<ProviderPlaylistUpdateChange> Changes { get; init; }
    public required IReadOnlyList<ProviderPlaylistUpdateSkip> Skipped { get; init; }
    public required bool ApplySupported { get; init; }
    public required string Message { get; init; }

    public int AddedCount => Changes.Count(item => item.Kind == "add");
    public int RemovedCount => Changes.Count(item => item.Kind == "remove");
    public int MovedCount => Changes.Count(item => item.Kind == "move");
    public bool RequiresChange => Changes.Count > 0;
    public bool CanApply => ApplySupported && RequiresChange;

    internal string TargetFingerprint { get; init; } = string.Empty;
    internal string CurrentFingerprint { get; init; } = string.Empty;
    internal string DesiredFingerprint { get; init; } = string.Empty;
    internal ProviderPlaylistSummary Source { get; init; } = null!;
    internal IReadOnlyList<ProviderPlaylistUpdateTrack> DesiredTracks { get; init; } = [];
    internal ProviderRouteCandidate<IProviderPlaylistCapability> Candidate { get; init; } = null!;
}

public sealed record ProviderPlaylistUpdateApplyResult(
    bool Applied,
    IReadOnlyList<string> Warnings);

public sealed class ProviderPlaylistUpdateException(
    string code,
    string safeMessage,
    bool retryable = false,
    bool forbidden = false,
    TimeSpan? retryAfter = null) : Exception(safeMessage)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
    public bool Forbidden { get; } = forbidden;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

internal sealed record ProviderPlaylistUpdateTrack(
    ProviderExternalResourceId Id,
    string Title,
    string Artist);

internal sealed record ProviderPlaylistUpdateDiff(
    IReadOnlyList<ProviderPlaylistUpdateChange> Changes,
    int DuplicateCount);

internal static class ProviderPlaylistUpdateDiffPlanner
{
    private readonly record struct Occurrence(string Key, int Number);
    private readonly record struct CommonTrack(Occurrence Token, int CurrentPosition);

    public static ProviderPlaylistUpdateDiff Build(
        IReadOnlyList<ProviderPlaylistUpdateTrack> current,
        IReadOnlyList<ProviderPlaylistUpdateTrack> desired)
    {
        var currentTokens = Tokenize(current);
        var desiredTokens = Tokenize(desired);
        var currentPositions = currentTokens
            .Select((token, position) => (token, position))
            .ToDictionary(item => item.token, item => item.position);
        var desiredSet = desiredTokens.ToHashSet();
        var common = desiredTokens
            .Where(currentPositions.ContainsKey)
            .Select(token => new CommonTrack(token, currentPositions[token]))
            .ToArray();
        var unchanged = LongestIncreasingSubsequence(common);
        var changes = new List<ProviderPlaylistUpdateChange>();

        for (var position = 0; position < desired.Count; position++)
        {
            var token = desiredTokens[position];
            if (!currentPositions.TryGetValue(token, out var from))
            {
                changes.Add(Change("add", null, position, desired[position]));
            }
            else if (!unchanged.Contains(token))
            {
                changes.Add(Change("move", from, position, desired[position]));
            }
        }

        for (var position = 0; position < current.Count; position++)
        {
            if (!desiredSet.Contains(currentTokens[position]))
                changes.Add(Change("remove", position, null, current[position]));
        }

        var duplicateCount = desired.Count - desired.Select(item => Key(item.Id)).Distinct(StringComparer.Ordinal).Count();
        return new(changes, duplicateCount);
    }

    private static ProviderPlaylistUpdateChange Change(
        string kind,
        int? from,
        int? to,
        ProviderPlaylistUpdateTrack track) => new(
            kind,
            from,
            to,
            track.Title,
            track.Artist);

    private static Occurrence[] Tokenize(IReadOnlyList<ProviderPlaylistUpdateTrack> tracks)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        return tracks.Select(track =>
        {
            var key = Key(track.Id);
            var number = counts.GetValueOrDefault(key);
            counts[key] = number + 1;
            return new Occurrence(key, number);
        }).ToArray();
    }

    private static HashSet<Occurrence> LongestIncreasingSubsequence(IReadOnlyList<CommonTrack> tracks)
    {
        if (tracks.Count == 0) return [];
        var tails = new int[tracks.Count];
        var previous = Enumerable.Repeat(-1, tracks.Count).ToArray();
        var length = 0;
        for (var index = 0; index < tracks.Count; index++)
        {
            var low = 0;
            var high = length;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (tracks[tails[middle]].CurrentPosition < tracks[index].CurrentPosition) low = middle + 1;
                else high = middle;
            }
            if (low > 0) previous[index] = tails[low - 1];
            tails[low] = index;
            if (low == length) length++;
        }

        var result = new HashSet<Occurrence>();
        for (var index = tails[length - 1]; index >= 0; index = previous[index])
            result.Add(tracks[index].Token);
        return result;
    }

    private static string Key(ProviderExternalResourceId id) =>
        $"{id.ProviderId}\u001f{id.ResourceKind}\u001f{id.Catalog ?? "default"}\u001f{id.Value}";
}

public sealed class ProviderPlaylistUpdateService(
    IDbContextFactory<AllstarrDbContext> contextFactory,
    IProviderRegistry registry,
    IProviderRouter providerRouter,
    IBackendPlaylistTargetResolver targetResolver,
    IPlatformClock clock)
{
    private const int MaximumEntries = 100_000;
    private const int MaximumPages = 1_000;

    public bool CanReplaceSource(string providerId) =>
        registry.TryGet(providerId, out var provider) &&
        provider!.Capabilities.SingleOrDefault(item => item.Capability == ProviderCapabilityKind.Playlist)
            ?.Hooks.Contains("mutatePlaylist", StringComparer.Ordinal) == true &&
        registry.TryGetCapability<IProviderPlaylistCapability>(
            providerId,
            ProviderCapabilityKind.Playlist,
            out var capability) &&
        capability!.MutationSupport.CanReplaceExisting;

    public async Task<ProviderPlaylistUpdatePlan> PreviewAsync(
        ProviderActorContext actor,
        Guid playlistLinkId,
        string libraryScopeId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (playlistLinkId == Guid.Empty) throw new ArgumentException("A playlist link is required.", nameof(playlistLinkId));
        if (string.IsNullOrWhiteSpace(libraryScopeId)) throw new ArgumentException("A library is required.", nameof(libraryScopeId));

        PlaylistLinkRecord link;
        ProviderAccountRecord account;
        BackendIdentityRecord backendIdentity;
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            link = await db.PlaylistLinks.AsNoTracking().SingleOrDefaultAsync(item =>
                item.Id == playlistLinkId && item.TenantId == actor.TenantId,
                cancellationToken) ?? throw new KeyNotFoundException("Playlist link not found.");
            if (actor.EffectiveUserId != link.OwnerUserId)
                throw new ProviderPlaylistUpdateException(
                    "playlist-owner-required",
                    "Only the playlist owner can update its source playlist.",
                    forbidden: true);
            if (!link.LibraryScopeId.Equals(libraryScopeId.Trim(), StringComparison.Ordinal))
                throw new ProviderPlaylistUpdateException(
                    "playlist-library-denied",
                    "The selected playlist belongs to another library.",
                    forbidden: true);
            if (string.IsNullOrWhiteSpace(link.TargetPlaylistId))
                throw new ProviderPlaylistUpdateException(
                    "backend-playlist-required",
                    "Choose a Jellyfin or Subsonic playlist before updating the source playlist.");

            account = await db.ProviderAccounts.AsNoTracking().SingleOrDefaultAsync(item =>
                item.Id == link.ProviderAccountId,
                cancellationToken) ?? throw new ProviderPlaylistUpdateException(
                    "provider-account-unavailable",
                    "The selected source account is unavailable.",
                    forbidden: true);
            if (!MatchesSavedAccount(link, account))
                throw new ProviderPlaylistUpdateException(
                    "provider-account-unavailable",
                    "The selected source account is unavailable.",
                    forbidden: true);

            var targetProtocols = TargetProtocols(link.TargetProtocol);
            backendIdentity = await db.BackendIdentities.AsNoTracking()
                .Where(item =>
                    item.TenantId == link.TenantId &&
                    item.UserId == link.OwnerUserId &&
                    item.BackendInstanceId == link.TargetBackendInstanceId &&
                    targetProtocols.Contains(item.BackendType))
                .OrderByDescending(item => item.LastSeenAt)
                .FirstOrDefaultAsync(cancellationToken) ?? throw new ProviderPlaylistUpdateException(
                    "backend-identity-unavailable",
                    "The selected Jellyfin or Subsonic identity is unavailable.",
                    forbidden: true);
        }

        var candidate = await RouteAsync(actor, link, account, correlationId, cancellationToken);
        var sourceId = new ProviderExternalResourceId(
            link.SourceProviderId,
            ProviderResourceKind.Playlist,
            link.SourcePlaylistId);
        var target = targetResolver.Resolve(link.TargetProtocol);
        var targetContext = new BackendPlaylistTargetContext(
            link.TargetBackendInstanceId,
            backendIdentity.PrincipalId,
            link.TargetCredentialReferenceId?.ToString(),
            link.TenantId);
        var sourceTask = ReadSourceAsync(candidate, sourceId, null, cancellationToken);
        var targetTask = target.ReadAsync(targetContext, link.TargetPlaylistId!, cancellationToken);
        await Task.WhenAll(sourceTask, targetTask);
        var source = await sourceTask;
        var targetResult = await targetTask;
        if (!targetResult.IsSuccess || targetResult.Value == null)
            throw TargetFailure(targetResult.Status, targetResult.ErrorCode);

        var providerName = Safe(candidate.Provider.DisplayName, link.SourceProviderId);
        var mapped = await MapTargetAsync(link, account, source, targetResult.Value, providerName, cancellationToken);
        var currentTracks = source.Tracks.Select(ToUpdateTrack).ToArray();
        var diff = ProviderPlaylistUpdateDiffPlanner.Build(currentTracks, mapped.Tracks);
        var currentFingerprint = HashTrackSequence(currentTracks.Select(item => item.Id));
        var desiredFingerprint = HashTrackSequence(mapped.Tracks.Select(item => item.Id));
        var confirmation = HashText(string.Join('\n',
            "provider-source-update-v1",
            link.Id.ToString("N"),
            link.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            source.Summary.SourceRevision,
            targetResult.Value.Fingerprint,
            desiredFingerprint));
        var supportsReplace = candidate.Descriptor.Hooks.Contains("mutatePlaylist", StringComparer.Ordinal) &&
                              candidate.Implementation.MutationSupport.CanReplaceExisting;
        var message = !supportsReplace
            ? $"{candidate.Provider.DisplayName} cannot replace this playlist, so Allstarr will not change it."
            : diff.Changes.Count == 0
                ? $"{candidate.Provider.DisplayName} already has the same songs in the same order."
                : $"Allstarr will update {source.Summary.Name} in {candidate.Provider.DisplayName} after you confirm.";

        return new ProviderPlaylistUpdatePlan
        {
            PlaylistLinkId = link.Id,
            LinkRevision = link.Revision,
            ProviderId = link.SourceProviderId,
            ProviderName = providerName,
            SourcePlaylistName = Safe(source.Summary.Name, "Source playlist"),
            BackendPlaylistName = Safe(targetResult.Value.Name, "Selected playlist"),
            BackendProtocol = link.TargetProtocol,
            SourceVersion = HashText(source.Summary.SourceRevision)[..12],
            ConfirmationId = confirmation,
            CurrentCount = source.Tracks.Count,
            IncludedCount = mapped.Tracks.Count,
            DuplicateCount = diff.DuplicateCount,
            Changes = diff.Changes,
            Skipped = mapped.Skipped,
            ApplySupported = supportsReplace,
            Message = Safe(message, "The source playlist cannot be updated."),
            TargetFingerprint = targetResult.Value.Fingerprint,
            CurrentFingerprint = currentFingerprint,
            DesiredFingerprint = desiredFingerprint,
            Source = source.Summary,
            DesiredTracks = mapped.Tracks,
            Candidate = candidate
        };
    }

    public async Task<ProviderPlaylistUpdateApplyResult> ApplyAsync(
        ProviderPlaylistUpdatePlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.ApplySupported)
            throw new ProviderPlaylistUpdateException(
                "provider-source-update-unsupported",
                $"{plan.ProviderName} cannot replace this playlist.");
        if (!plan.RequiresChange) return new(false, []);
        cancellationToken.ThrowIfCancellationRequested();

        var context = plan.Candidate.Context;
        var mutationContext = new ProviderExecutionContext(
            context.Actor,
            context.ProviderId,
            context.Account,
            context.Library,
            context.Policy,
            "provider-source-update",
            context.CorrelationId,
            clock.UtcNow.AddMinutes(5),
            cancellationToken,
            plan.ConfirmationId);
        var outcome = await plan.Candidate.Implementation.MutatePlaylistAsync(
            mutationContext,
            new ProviderPlaylistMutationRequest(
                plan.ProviderId,
                plan.Source.Name,
                plan.DesiredTracks.Select(item => item.Id),
                ProviderPlaylistConflictBehavior.FailIfChanged,
                plan.Source.Id,
                plan.Source.SourceRevision,
                plan.Source.Description));
        if (!outcome.IsSuccess)
            throw ProviderFailure(outcome.Error!, "provider-source-update-failed", retryPermanentFailure: true);

        var receipt = outcome.RequireValue();
        if (receipt.PlaylistId != plan.Source.Id || receipt.TrackCount != plan.DesiredTracks.Count)
            throw new ProviderPlaylistUpdateException(
                "provider-source-verification-mismatch",
                "The source service did not confirm the requested playlist contents.",
                retryable: true);
        var verified = await ReadSourceAsync(
            plan.Candidate,
            plan.Source.Id,
            null,
            cancellationToken);
        if (HashTrackSequence(verified.Tracks.Select(item => item.TrackId)) != plan.DesiredFingerprint)
            throw new ProviderPlaylistUpdateException(
                "provider-source-verification-mismatch",
                "The source playlist did not match the confirmed order after the update.",
                retryable: true);
        return new(receipt.Applied, receipt.Warnings);
    }

    private async Task<ProviderRouteCandidate<IProviderPlaylistCapability>> RouteAsync(
        ProviderActorContext actor,
        PlaylistLinkRecord link,
        ProviderAccountRecord account,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var policy = new ProviderExecutionPolicy(
            new ProviderQualityPolicy(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, false),
            ProviderExplicitContentPolicy.Allow,
            allowFallback: false,
            allowSharedAccount: account.Scope == ProviderAccountScope.Global,
            allowManagedDownloads: false,
            allowedProviderIds: [link.SourceProviderId]);
        var plan = await providerRouter.PlanAsync<IProviderPlaylistCapability>(new ProviderRouteRequest(
            ProviderCapabilityKind.Playlist,
            actor,
            policy,
            "provider-source-update-preview",
            correlationId,
            clock.UtcNow.AddMinutes(2),
            [link.SourceProviderId],
            [new ProviderRouteProviderState(
                link.SourceProviderId,
                requestedAccountId: account.Id,
                expectedAccountRevision: account.Revision)],
            new ProviderLibraryContext(link.TenantId, link.LibraryScopeId),
            cancellationToken: cancellationToken));
        var candidate = plan.Candidates.FirstOrDefault();
        if (candidate == null ||
            candidate.Provider.Id != link.SourceProviderId ||
            candidate.Provider.Id != account.ProviderId ||
            candidate.Descriptor.Capability != ProviderCapabilityKind.Playlist ||
            candidate.Implementation.Capability != ProviderCapabilityKind.Playlist ||
            candidate.Implementation.ProviderId != link.SourceProviderId ||
            candidate.Implementation.ProviderId != account.ProviderId ||
            candidate.Context.ProviderId != link.SourceProviderId ||
            candidate.Context.ProviderId != account.ProviderId ||
            !MatchesRoutedAccount(account, candidate.Context.Account) ||
            candidate.Context.Library is not { } routedLibrary ||
            routedLibrary.TenantId != link.TenantId ||
            routedLibrary.ScopeId != link.LibraryScopeId ||
            candidate.Context.Actor.TenantId != actor.TenantId ||
            candidate.Context.Actor.EffectiveUserId != actor.EffectiveUserId)
            throw new ProviderPlaylistUpdateException(
                "provider-route-unavailable",
                "The selected source account cannot update this playlist right now.",
                retryable: true,
                forbidden: plan.Decision.Candidates.Any(item =>
                    item.ProviderId == link.SourceProviderId &&
                    item.ReasonCode.Contains("authorized", StringComparison.Ordinal)));
        return candidate;
    }

    private static bool MatchesSavedAccount(
        PlaylistLinkRecord link,
        ProviderAccountRecord account) =>
        account.Enabled && account.ProviderId == link.SourceProviderId &&
        account.Scope switch
        {
            ProviderAccountScope.User =>
                account.TenantId == link.TenantId &&
                account.OwnerUserId == link.OwnerUserId &&
                account.LibraryScopeId == null,
            ProviderAccountScope.Library =>
                account.TenantId == link.TenantId &&
                account.OwnerUserId == null &&
                account.LibraryScopeId == link.LibraryScopeId,
            ProviderAccountScope.Global =>
                account.TenantId == null &&
                account.OwnerUserId == null &&
                account.LibraryScopeId == null,
            _ => false
        };

    private static bool MatchesRoutedAccount(
        ProviderAccountRecord account,
        ProviderAccountContext? routed) =>
        routed is { Enabled: true } &&
        routed.AccountId == account.Id &&
        routed.ProviderId == account.ProviderId &&
        routed.Scope == account.Scope &&
        routed.Revision == account.Revision &&
        routed.TenantId == account.TenantId &&
        routed.OwnerUserId == account.OwnerUserId &&
        routed.LibraryScopeId == account.LibraryScopeId &&
        routed.SecretReferenceId == account.SecretReferenceId;

    private async Task<ProviderPlaylistSourceState> ReadSourceAsync(
        ProviderRouteCandidate<IProviderPlaylistCapability> candidate,
        ProviderExternalResourceId playlistId,
        string? expectedRevision,
        CancellationToken cancellationToken)
    {
        var tracks = new List<ProviderPlaylistTrack>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        ProviderPlaylistSummary? summary = null;
        string? snapshotVersion = null;
        string? cursor = null;
        var pageCount = 0;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++pageCount > MaximumPages)
                throw new ProviderPlaylistUpdateException(
                    "provider-playlist-too-large",
                    "The source playlist is too large to update safely.");
            var outcome = await candidate.Implementation.GetPlaylistTracksAsync(
                candidate.Context,
                new ProviderPlaylistTracksRequest(
                    playlistId,
                    new ProviderPageRequest(200, cursor),
                    summary?.SourceRevision ?? expectedRevision));
            if (!outcome.IsSuccess)
                throw ProviderFailure(outcome.Error!, "provider-source-read-failed");
            var page = outcome.RequireValue();
            if (page.Playlist.Id != playlistId ||
                page.Tracks.Items.Count > 200 ||
                page.Tracks.ProviderId != playlistId.ProviderId ||
                summary != null &&
                (page.Playlist.SourceRevision != summary.SourceRevision ||
                 page.Playlist.Name != summary.Name ||
                 page.Playlist.Description != summary.Description) ||
                snapshotVersion != null && page.Tracks.SnapshotVersion != snapshotVersion)
                throw InvalidProviderPage();
            summary ??= page.Playlist;
            snapshotVersion ??= page.Tracks.SnapshotVersion;
            if (tracks.Count + page.Tracks.Items.Count > MaximumEntries)
                throw new ProviderPlaylistUpdateException(
                    "provider-playlist-too-large",
                    "The source playlist is too large to update safely.");
            var expectedPosition = tracks.Count;
            foreach (var track in page.Tracks.Items)
            {
                if (track.Position != expectedPosition++ || track.TrackId.ProviderId != playlistId.ProviderId)
                    throw InvalidProviderPage();
                tracks.Add(track);
            }
            cursor = page.Tracks.NextCursor;
            if (cursor != null && !cursors.Add(cursor)) throw InvalidProviderPage();
            if (page.Tracks.IsPartial != (cursor != null)) throw InvalidProviderPage();
        } while (cursor != null);

        if (summary == null || summary.TrackCount.HasValue && summary.TrackCount != tracks.Count)
            throw InvalidProviderPage();
        return new(summary, tracks);
    }

    private async Task<ProviderPlaylistTargetMapping> MapTargetAsync(
        PlaylistLinkRecord link,
        ProviderAccountRecord account,
        ProviderPlaylistSourceState source,
        BackendPlaylistSnapshot target,
        string providerName,
        CancellationToken cancellationToken)
    {
        var backendIds = target.Members.Select(item => item.BackendItemId).Distinct(StringComparer.Ordinal).ToArray();
        var libraryTracks = new List<LibraryTrackRecord>();
        var targetProtocols = TargetProtocols(link.TargetProtocol);
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            foreach (var chunk in backendIds.Chunk(500))
                libraryTracks.AddRange(await db.LibraryTracks.AsNoTracking().Where(item =>
                    item.TenantId == link.TenantId &&
                    item.OwnerUserId == link.OwnerUserId &&
                    item.LibraryScopeId == link.LibraryScopeId &&
                    item.BackendInstanceId == link.TargetBackendInstanceId &&
                    targetProtocols.Contains(item.Protocol) &&
                    chunk.Contains(item.BackendItemId)).ToListAsync(cancellationToken));
        }
        var libraryByBackendId = libraryTracks
            .GroupBy(item => item.BackendItemId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(item => item.UpdatedAt).First(),
                StringComparer.Ordinal);
        var canonicalIds = libraryTracks
            .Where(item => item.CanonicalRecordingId.HasValue)
            .Select(item => item.CanonicalRecordingId!.Value)
            .Distinct()
            .ToArray();
        var sourceHashes = source.Tracks
            .Select(item => ProviderPlaylistSnapshotCollector.HashResource(item.TrackId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var identities = new List<ProviderTrackIdentityRecord>();
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            foreach (var chunk in canonicalIds.Chunk(500))
                identities.AddRange(await db.ProviderTrackIdentities.AsNoTracking().Where(item =>
                    item.TenantId == link.TenantId &&
                    item.ProviderId == link.SourceProviderId &&
                    item.ResourceKind == ProviderResourceKind.Track &&
                    chunk.Contains(item.CanonicalRecordingId) &&
                    (item.Verification == ProviderIdentityVerification.Verified ||
                     item.Verification == ProviderIdentityVerification.Pinned)).ToListAsync(cancellationToken));
            foreach (var chunk in sourceHashes.Chunk(500))
                identities.AddRange(await db.ProviderTrackIdentities.AsNoTracking().Where(item =>
                    item.TenantId == link.TenantId &&
                    item.ProviderId == link.SourceProviderId &&
                    item.ResourceKind == ProviderResourceKind.Track &&
                    chunk.Contains(item.ExternalIdHash) &&
                    (item.Verification == ProviderIdentityVerification.Verified ||
                     item.Verification == ProviderIdentityVerification.Pinned)).ToListAsync(cancellationToken));
        }
        identities = identities.DistinctBy(item => item.Id).ToList();

        var sourceByCanonical = source.Tracks
            .Select(track =>
            {
                var hash = ProviderPlaylistSnapshotCollector.HashResource(track.TrackId);
                var identity = PreferredIdentity(
                    identities.Where(item => item.ExternalIdHash == hash),
                    account.Id,
                    allowHashedSourceIdentity: true);
                return (track, identity?.CanonicalRecordingId);
            })
            .Where(item => item.CanonicalRecordingId.HasValue)
            .GroupBy(item => item.CanonicalRecordingId!.Value)
            .ToDictionary(group => group.Key, group => group.First().track.TrackId);
        var identityByCanonical = identities
            .Where(item => item.VerificationMethod != "source-snapshot-hash")
            .GroupBy(item => item.CanonicalRecordingId)
            .Select(group => (group.Key, Identity: PreferredIdentity(group, account.Id, allowHashedSourceIdentity: false)))
            .Where(item => item.Identity != null)
            .ToDictionary(item => item.Key, item => item.Identity!);
        var desired = new List<ProviderPlaylistUpdateTrack>(target.Members.Count);
        var skipped = new List<ProviderPlaylistUpdateSkip>();

        for (var position = 0; position < target.Members.Count; position++)
        {
            var member = target.Members[position];
            if (!libraryByBackendId.TryGetValue(member.BackendItemId, out var library))
            {
                skipped.Add(new(position, $"Song {position + 1}", "Unknown artist", "Allstarr has not indexed this song."));
                continue;
            }
            var title = Safe(library.Title, $"Song {position + 1}");
            var artist = Safe(library.Artist, "Unknown artist");
            if (!library.CanonicalRecordingId.HasValue)
            {
                skipped.Add(new(position, title, artist, "This song has no confirmed match."));
                continue;
            }

            ProviderExternalResourceId? providerTrack = null;
            if (identityByCanonical.TryGetValue(library.CanonicalRecordingId.Value, out var identity))
                providerTrack = ToResource(identity);
            providerTrack ??= sourceByCanonical.GetValueOrDefault(library.CanonicalRecordingId.Value);
            if (providerTrack == null)
            {
                skipped.Add(new(position, title, artist,
                    $"Allstarr could not identify this song in {providerName}."));
                continue;
            }
            desired.Add(new(providerTrack, title, artist));
        }
        return new(desired, skipped);
    }

    private static ProviderTrackIdentityRecord? PreferredIdentity(
        IEnumerable<ProviderTrackIdentityRecord> values,
        Guid providerAccountId,
        bool allowHashedSourceIdentity)
    {
        var eligible = values.Where(item =>
            item.Scope is ProviderIdentityScope.Account or ProviderIdentityScope.Catalog &&
            (item.Scope != ProviderIdentityScope.Account || item.ProviderAccountId == providerAccountId) &&
            (allowHashedSourceIdentity || item.VerificationMethod != "source-snapshot-hash"));
        var account = eligible.Where(item => item.Scope == ProviderIdentityScope.Account).ToArray();
        return (account.Length > 0 ? account : eligible.Where(item => item.Scope == ProviderIdentityScope.Catalog))
            .OrderByDescending(item => item.Verification == ProviderIdentityVerification.Pinned)
            .ThenByDescending(item => item.DecisionVersion)
            .ThenByDescending(item => item.VerifiedAt)
            .FirstOrDefault();
    }

    private static ProviderExternalResourceId? ToResource(ProviderTrackIdentityRecord identity)
    {
        try
        {
            var resource = new ProviderExternalResourceId(
                identity.ProviderId,
                ProviderResourceKind.Track,
                identity.ExternalId,
                identity.CatalogNamespace == "default" ? null : identity.CatalogNamespace);
            return ProviderPlaylistSnapshotCollector.HashResource(resource) == identity.ExternalIdHash
                ? resource
                : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static ProviderPlaylistUpdateTrack ToUpdateTrack(ProviderPlaylistTrack track) => new(
        track.TrackId,
        Safe(track.Metadata?.Title, $"Song {track.Position + 1}"),
        Safe(track.Metadata?.Artists.FirstOrDefault()?.Name, "Unknown artist"));

    private static string HashTrackSequence(IEnumerable<ProviderExternalResourceId> ids)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var id in ids)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(
                $"{id.ProviderId}\u001f{id.ResourceKind}\u001f{id.Catalog ?? "default"}\u001f{id.Value}\n"));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static ProviderPlaylistUpdateException ProviderFailure(
        ProviderError error,
        string code,
        bool retryPermanentFailure = false) => new(
            code,
            error.Kind switch
            {
                ProviderErrorKind.RateLimited => "The source service asked Allstarr to try again later.",
                ProviderErrorKind.Unauthorized or ProviderErrorKind.AccountNeedsReauthentication =>
                    "Reconnect the selected source account before trying again.",
                ProviderErrorKind.Forbidden => "The selected source account cannot change this playlist.",
                ProviderErrorKind.NotSupported => "The source service cannot replace this playlist.",
                _ => "The source playlist could not be updated safely."
            },
            error.Kind is ProviderErrorKind.RateLimited or ProviderErrorKind.TransientFailure or ProviderErrorKind.Canceled ||
            retryPermanentFailure && error.Kind == ProviderErrorKind.PermanentFailure,
            error.Kind is ProviderErrorKind.Unauthorized or ProviderErrorKind.Forbidden or
                ProviderErrorKind.AccountNeedsReauthentication or ProviderErrorKind.AccountNeedsConfiguration,
            error.RetryAfter);

    private static ProviderPlaylistUpdateException TargetFailure(
        BackendPlaylistTargetStatus status,
        string? errorCode) => new(
            errorCode ?? "backend-playlist-unavailable",
            status switch
            {
                BackendPlaylistTargetStatus.NotFound => "The selected Jellyfin or Subsonic playlist no longer exists.",
                BackendPlaylistTargetStatus.Unauthorized => "The selected Jellyfin or Subsonic account cannot read this playlist.",
                _ => "The selected Jellyfin or Subsonic playlist could not be read safely."
            },
            status is BackendPlaylistTargetStatus.BackendFailure or BackendPlaylistTargetStatus.Cancelled,
            status == BackendPlaylistTargetStatus.Unauthorized);

    private static ProviderPlaylistUpdateException InvalidProviderPage() => new(
        "provider-source-response-invalid",
        "The source service returned an inconsistent playlist, so Allstarr will not change it.");

    private static string[] TargetProtocols(string protocol) => protocol.Trim().ToLowerInvariant() switch
    {
        "jellyfin" => ["jellyfin"],
        "subsonic" or "opensubsonic" or "navidrome" => ["subsonic", "opensubsonic", "navidrome"],
        _ => [protocol.Trim().ToLowerInvariant()]
    };

    private static string Safe(string? value, string fallback) =>
        SafeOperationalText.Sanitize(value, 300) ?? fallback;

    private static string HashText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record ProviderPlaylistSourceState(
        ProviderPlaylistSummary Summary,
        IReadOnlyList<ProviderPlaylistTrack> Tracks);

    private sealed record ProviderPlaylistTargetMapping(
        IReadOnlyList<ProviderPlaylistUpdateTrack> Tracks,
        IReadOnlyList<ProviderPlaylistUpdateSkip> Skipped);
}

public sealed record ProviderPlaylistUpdateJobPayload(
    Guid PlaylistLinkId,
    long ExpectedLinkRevision,
    string ConfirmationId,
    string TargetFingerprint,
    string DesiredFingerprint);

public sealed class ProviderPlaylistUpdateJobHandler(
    IDbContextFactory<AllstarrDbContext> contextFactory,
    ProviderPlaylistUpdateService updates,
    IPlatformClock clock) : IDurableJobHandler
{
    public string JobType => "playlist.provider-source-update";

    public async Task<DurableJobCompletion> ExecuteAsync(
        DurableJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        ProviderPlaylistUpdateJobPayload? payload;
        try { payload = context.Claim.Payload.Deserialize<ProviderPlaylistUpdateJobPayload>(); }
        catch (JsonException) { payload = null; }
        if (!Valid(payload) ||
            !context.Claim.TenantId.HasValue ||
            !context.Claim.OwnerUserId.HasValue ||
            !context.Claim.ProviderAccountId.HasValue ||
            string.IsNullOrWhiteSpace(context.Claim.LibraryScopeId))
            return DurableJobCompletion.Failure(
                "provider-source-update-payload-invalid",
                "The confirmed source playlist update is invalid.");

        PlaylistLinkRecord? link;
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            link = await db.PlaylistLinks.AsNoTracking().SingleOrDefaultAsync(item =>
                item.Id == payload!.PlaylistLinkId &&
                item.TenantId == context.Claim.TenantId &&
                item.OwnerUserId == context.Claim.OwnerUserId &&
                item.ProviderAccountId == context.Claim.ProviderAccountId &&
                item.LibraryScopeId == context.Claim.LibraryScopeId,
                cancellationToken);
        }
        if (link == null)
            return DurableJobCompletion.Failure(
                "provider-source-update-scope-invalid",
                "The confirmed source playlist update is outside its saved account or library.");

        ProviderPlaylistUpdatePlan? plan = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await context.ReportProgressAsync(new(
                "playlist.provider-source-update.preview",
                "Checking the two selected playlists.",
                Provider: link.SourceProviderId), cancellationToken);
            var actor = new ProviderActorContext(
                link.TenantId,
                ProviderActorKind.SystemJob,
                null,
                durableJobId: context.Claim.JobId,
                actingForUserId: link.OwnerUserId);
            plan = await updates.PreviewAsync(
                actor,
                link.Id,
                link.LibraryScopeId,
                context.Claim.CorrelationId,
                cancellationToken);
            if (plan.LinkRevision != payload!.ExpectedLinkRevision)
                return await FailAsync(
                    context,
                    link,
                    plan,
                    "provider-source-update-link-changed",
                    "The playlist settings changed after confirmation.",
                    cancellationToken);
            if (plan.TargetFingerprint != payload.TargetFingerprint)
                return await FailAsync(
                    context,
                    link,
                    plan,
                    "provider-source-update-target-changed",
                    "The Jellyfin or Subsonic playlist changed after confirmation.",
                    cancellationToken);

            if (plan.CurrentFingerprint == payload.DesiredFingerprint)
            {
                await AuditAsync(context, link, plan, "succeeded", applied: false, [], null, cancellationToken);
                await context.ReportProgressAsync(new(
                    "playlist.provider-source-update.complete",
                    $"{plan.ProviderName} already has the confirmed songs in the confirmed order.",
                    plan.IncludedCount,
                    plan.IncludedCount,
                    plan.ProviderId,
                    plan.SourcePlaylistName), cancellationToken);
                return DurableJobCompletion.Success();
            }
            if (plan.ConfirmationId != payload.ConfirmationId ||
                plan.DesiredFingerprint != payload.DesiredFingerprint)
                return await FailAsync(
                    context,
                    link,
                    plan,
                    "provider-source-update-source-changed",
                    "The source playlist changed after confirmation.",
                    cancellationToken);
            if (!plan.CanApply)
                return await FailAsync(
                    context,
                    link,
                    plan,
                    "provider-source-update-unavailable",
                    plan.Message,
                    cancellationToken);

            await context.ReportProgressAsync(new(
                "playlist.provider-source-update.apply",
                $"Updating {plan.SourcePlaylistName} in {plan.ProviderName}.",
                0,
                plan.IncludedCount,
                plan.ProviderId,
                plan.SourcePlaylistName), cancellationToken);
            var result = await updates.ApplyAsync(plan, cancellationToken);
            await AuditAsync(context, link, plan, "succeeded", result.Applied, result.Warnings, null, cancellationToken);
            await context.ReportProgressAsync(new(
                "playlist.provider-source-update.complete",
                $"Verified {plan.SourcePlaylistName} in {plan.ProviderName}.",
                plan.IncludedCount,
                plan.IncludedCount,
                plan.ProviderId,
                plan.SourcePlaylistName), cancellationToken);
            return DurableJobCompletion.Success();
        }
        catch (ProviderPlaylistUpdateException exception)
        {
            await AuditAsync(
                context,
                link,
                plan,
                exception.Retryable ? "retry" : "failed",
                applied: false,
                [],
                exception.Code,
                cancellationToken);
            return exception.Retryable
                ? DurableJobCompletion.Retry(exception.Code, exception.Message, exception.RetryAfter)
                : DurableJobCompletion.Failure(exception.Code, exception.Message);
        }
    }

    private async Task<DurableJobCompletion> FailAsync(
        DurableJobExecutionContext context,
        PlaylistLinkRecord link,
        ProviderPlaylistUpdatePlan plan,
        string code,
        string message,
        CancellationToken cancellationToken)
    {
        await AuditAsync(context, link, plan, "conflict", applied: false, [], code, cancellationToken);
        return DurableJobCompletion.Failure(code, message);
    }

    private async Task AuditAsync(
        DurableJobExecutionContext context,
        PlaylistLinkRecord link,
        ProviderPlaylistUpdatePlan? plan,
        string outcome,
        bool applied,
        IReadOnlyList<string> warnings,
        string? errorCode,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.AuditEvents.Add(new AuditEventRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = link.TenantId,
            ActorUserId = context.Claim.OwnerUserId,
            Category = "playlist",
            Action = "provider-source-update",
            Outcome = outcome,
            CorrelationId = context.Claim.CorrelationId,
            DetailsJson = JsonSerializer.Serialize(new
            {
                playlistLinkId = link.Id,
                jobId = context.Claim.JobId,
                attempt = context.Claim.AttemptNumber,
                providerId = link.SourceProviderId,
                currentCount = plan?.CurrentCount,
                includedCount = plan?.IncludedCount,
                skippedCount = plan?.Skipped.Count,
                addedCount = plan?.AddedCount,
                removedCount = plan?.RemovedCount,
                movedCount = plan?.MovedCount,
                applied,
                warnings,
                errorCode
            }),
            CreatedAt = clock.UtcNow
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private static bool Valid(ProviderPlaylistUpdateJobPayload? payload) =>
        payload is
        {
            PlaylistLinkId: var linkId,
            ExpectedLinkRevision: >= 0,
            ConfirmationId: var confirmation,
            TargetFingerprint: var target,
            DesiredFingerprint: var desired
        } &&
        linkId != Guid.Empty &&
        HexHash(confirmation) &&
        HexHash(target) &&
        HexHash(desired);

    private static bool HexHash(string value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

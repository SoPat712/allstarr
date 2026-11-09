using System.Collections.Concurrent;
using allstarr.Core.Protocols;
using allstarr.Core.Capabilities;
using allstarr.Core.Storage;
using allstarr.Services.Local;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Matching;

public sealed record LibraryRecordingLookup(
    string PublicId, Guid? RecordingId, string? Isrc, string? ProviderId, string? ExternalId);

public sealed partial class LibraryIndexService
{
    public async Task<string?> ResolveKnownTrackAsync(ProtocolExecutionContext context, string publicId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (context.Actor == null) return null;
            var recording = await _identities.ResolveRecordingAsync(context.Actor, publicId, cancellationToken);
            if (recording == null) return null;
            var parsed = LocalLibraryService.ParseExternalResource(publicId);
            if (parsed.provider == "allstarr") parsed = LocalLibraryService.ParseExternalResource(recording.PublicId);
            var matches = await FindKnownRecordingTracksAsync(context,
                [new(publicId, recording.Id, recording.Isrc, parsed.provider, parsed.externalId)], cancellationToken);
            return matches.GetValueOrDefault(publicId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || context.CancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            _logger?.LogWarning("Native recording lookup unavailable ({ErrorType}); retaining the external route", error.GetType().Name);
            return null;
        }
    }

    public async Task<IReadOnlyDictionary<string, string>> FindKnownRecordingTracksAsync(
        ProtocolExecutionContext context, IReadOnlyList<LibraryRecordingLookup> recordings,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (context.Principal == null || recordings.Count == 0) return result;
            EnsureStorageReady();
            recordings = recordings.Select(item => item.ProviderId == null ? item : item with
            { ProviderId = _registry?.ResolveProviderId(item.ProviderId) ?? item.ProviderId }).ToArray();
            var policy = context.Policy?.UserId == context.Principal.UserId ? context.Policy :
                _effectivePolicies == null ? null :
                    await _effectivePolicies.ResolveForUserAsync(context.Principal.UserId, cancellationToken);
            var access = await _libraryAccess.ResolveAsync(context, cancellationToken);
            if (!access.Succeeded || access.LibraryIds.Length == 0) return result;
            await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
            await RequireUserAsync(db, context.Principal, cancellationToken);
            var sources = recordings.Where(item => item.ProviderId != null && item.ExternalId != null)
                .Select(item => (ProviderId: item.ProviderId!, ExternalIdHash: CanonicalCatalogKeys.Hash(item.ExternalId!))).ToArray();
            var ids = recordings.Where(item => item.RecordingId.HasValue).Select(item => item.RecordingId!.Value).ToArray();
            var isrcs = recordings.Select(item => CleanIsrc(item.Isrc)).OfType<string>().ToArray();
            var knownIdentities = ids.Length == 0 ? [] : await db.ProviderTrackIdentities.AsNoTracking().Where(item =>
                    ids.Contains(item.CanonicalRecordingId) && item.ResourceKind == ProviderResourceKind.Track &&
                    (item.Verification == ProviderIdentityVerification.Verified || item.Verification == ProviderIdentityVerification.Pinned) &&
                    item.VerificationMethod != CatalogIdentityPolicy.SupersededVerificationMethod &&
                    (item.Scope == ProviderIdentityScope.Catalog || item.Scope == ProviderIdentityScope.Account &&
                     db.ProviderAccounts.Any(account => account.Id == item.ProviderAccountId && account.Enabled &&
                         (account.OwnerUserId == null || account.OwnerUserId == context.Principal.UserId))))
                .ToArrayAsync(cancellationToken);
            var knownIdentitiesBySource = knownIdentities.ToLookup(item => (item.ProviderId, item.ExternalIdHash));
            var directSources = sources.ToHashSet();
            var candidateSources = sources.Concat(knownIdentities.Select(item => (item.ProviderId, item.ExternalIdHash))).ToArray();
            var hashes = candidateSources.Select(item => item.ExternalIdHash).Distinct().ToArray();
            var providers = candidateSources.Select(item => item.ProviderId).Distinct().ToArray();
            var storedSnapshots = (await db.ExternalMetadataSnapshots.AsNoTracking().Where(item =>
                    item.OwnerUserId == context.Principal.UserId && item.Protocol == context.Protocol.ToString().ToLowerInvariant() &&
                    item.BackendInstanceId == context.BackendInstanceId && item.BackendPrincipalId == context.VerifiedBackendPrincipalId &&
                    item.ResourceKind == "track" && hashes.Contains(item.ExternalIdHash) && providers.Contains(item.ProviderId) &&
                    (item.ProviderAccountId == null || db.ProviderAccounts.Any(account => account.Id == item.ProviderAccountId &&
                        account.Enabled && (account.OwnerUserId == null || account.OwnerUserId == context.Principal.UserId))))
                .OrderByDescending(item => item.RetrievedAt).ThenByDescending(item => item.SnapshotVersion)
                .ToArrayAsync(cancellationToken))
                .Where(item => directSources.Contains((item.ProviderId, item.ExternalIdHash)) ||
                    knownIdentitiesBySource[(item.ProviderId, item.ExternalIdHash)].Any(identity =>
                        (!item.ProviderTrackIdentityId.HasValue || identity.Id == item.ProviderTrackIdentityId) &&
                        (identity.Scope == ProviderIdentityScope.Catalog || identity.ProviderAccountId == item.ProviderAccountId)))
                .ToArray();
            var snapshots = storedSnapshots.GroupBy(item => (item.ProviderId, item.ExternalIdHash))
                .ToDictionary(group => group.Key, group => group.First());
            var identityIds = storedSnapshots.Select(item => item.ProviderTrackIdentityId).OfType<Guid>().Distinct().ToArray();
            var snapshotHashes = storedSnapshots.Select(item => item.ExternalIdHash).Distinct().ToArray();
            var snapshotProviders = storedSnapshots.Select(item => item.ProviderId).Distinct().ToArray();
            var identitiesBySource = (await db.ProviderTrackIdentities.AsNoTracking().Where(item =>
                    item.ResourceKind == ProviderResourceKind.Track &&
                    (item.Verification == ProviderIdentityVerification.Verified || item.Verification == ProviderIdentityVerification.Pinned) &&
                    item.VerificationMethod != CatalogIdentityPolicy.SupersededVerificationMethod &&
                    (identityIds.Contains(item.Id) || snapshotHashes.Contains(item.ExternalIdHash) && snapshotProviders.Contains(item.ProviderId)) &&
                    (item.Scope == ProviderIdentityScope.Catalog || item.Scope == ProviderIdentityScope.Account &&
                     db.ProviderAccounts.Any(account => account.Id == item.ProviderAccountId && account.Enabled &&
                         (account.OwnerUserId == null || account.OwnerUserId == context.Principal.UserId))))
                .ToArrayAsync(cancellationToken)).ToLookup(item => (item.ProviderId, item.ExternalIdHash));
            var snapshotsByRecording = new Dictionary<Guid, ExternalMetadataSnapshotRecord>();
            var requestedRecordingIds = ids.ToHashSet();
            foreach (var snapshot in storedSnapshots.GroupBy(item => (item.ProviderId, item.ExternalIdHash, item.ProviderAccountId))
                         .Select(group => group.First()))
            {
                var matchingIds = identitiesBySource[(snapshot.ProviderId, snapshot.ExternalIdHash)]
                    .Where(item => (!snapshot.ProviderTrackIdentityId.HasValue || item.Id == snapshot.ProviderTrackIdentityId) &&
                        (item.Scope == ProviderIdentityScope.Catalog || item.ProviderAccountId == snapshot.ProviderAccountId))
                    .Select(item => item.CanonicalRecordingId).Distinct().Take(2).ToArray();
                if (matchingIds.Length == 1 && requestedRecordingIds.Contains(matchingIds[0]))
                    snapshotsByRecording.TryAdd(matchingIds[0], snapshot);
            }
            var overrideSources = sources.Concat(storedSnapshots.Select(item => (item.ProviderId, item.ExternalIdHash))).Distinct().ToArray();
            var overrides = (await ManualTrackOverrides.LoadAsync(db, context.Principal.UserId, overrideSources, cancellationToken))
                .GroupBy(item => (item.SourceProviderId, item.SourceExternalIdHash))
                .ToDictionary(group => group.Key, group => ManualTrackOverrides.Select(group, context.Principal.UserId).Effective!);
            var pinnedIds = overrides.Values.Where(item => item.Decision == ManualOverrideDecision.Pin && item.LibraryTrackId.HasValue)
                .Select(item => item.LibraryTrackId!.Value).ToArray();
            var engine = new TrackMatchDecisionEngine(new TrackMatchPolicy
            {
                LocalPriorityWindow = policy?.LocalPreferenceWindow ?? .07,
                ExplicitContent = policy?.Preferences.ExplicitFilter == "CleanOnly"
                    ? ProviderExplicitContentPolicy.CleanOnly : ProviderExplicitContentPolicy.Allow
            });
            LibraryTrackRecord[] tracks;
            TrackMatchCandidateSet candidates;
            if (snapshots.Count == 0)
            {
                tracks = PreferredFirst(await LibraryTrackAccess.Query(db, context, access).Where(item => pinnedIds.Contains(item.Id) ||
                    item.CanonicalRecordingId.HasValue && ids.Contains(item.CanonicalRecordingId.Value) ||
                    item.Isrc != null && isrcs.Contains(item.Isrc)).ToArrayAsync(cancellationToken));
                candidates = engine.PrepareCandidates(tracks.Select(LocalCopyPreference.ToCandidate));
            }
            else (tracks, candidates) = await SharedLibraryAsync(db, context, access, engine, cancellationToken);
            ProviderUseMode Mode(string providerId) => policy?.GetProviderMode(providerId) ??
                (_registry?.TryGet(providerId, out var descriptor) == true
                    ? descriptor!.DefaultUseMode : ProviderUseMode.Automatic);
            foreach (var recording in recordings)
            {
                if (recording.ProviderId is { } providerId && Mode(providerId) == ProviderUseMode.Off) continue;
                var snapshot = recording.ProviderId != null && recording.ExternalId != null
                    ? snapshots.GetValueOrDefault((recording.ProviderId, CanonicalCatalogKeys.Hash(recording.ExternalId))) : null;
                snapshot ??= recording.RecordingId is { } knownId ? snapshotsByRecording.GetValueOrDefault(knownId) : null;
                var manual = recording.ProviderId != null && recording.ExternalId != null
                    ? overrides.GetValueOrDefault((recording.ProviderId, CanonicalCatalogKeys.Hash(recording.ExternalId))) : null;
                manual ??= snapshot == null ? null : overrides.GetValueOrDefault((snapshot.ProviderId, snapshot.ExternalIdHash));
                LibraryTrackRecord? track;
                if (manual != null)
                {
                    // A rejection or provider pin must never be replaced by an automatic local copy.
                    track = manual.Decision == ManualOverrideDecision.Pin && manual.LibraryTrackId.HasValue
                        ? tracks.FirstOrDefault(item => item.Id == manual.LibraryTrackId) : null;
                }
                else
                {
                    if (recording.ProviderId is { } automaticProvider && Mode(automaticProvider) != ProviderUseMode.Automatic)
                        continue;
                    var isrc = CleanIsrc(recording.Isrc);
                    track = recording.RecordingId.HasValue
                        ? tracks.FirstOrDefault(item => item.CanonicalRecordingId == recording.RecordingId &&
                            (isrc == null || string.IsNullOrWhiteSpace(item.Isrc) || CleanIsrc(item.Isrc) == isrc)) : null;
                    track ??= isrc == null ? null : tracks.FirstOrDefault(item => item.Isrc == isrc &&
                        (!recording.RecordingId.HasValue || !item.CanonicalRecordingId.HasValue || item.CanonicalRecordingId == recording.RecordingId));
                    if (snapshot != null &&
                        TrackSnapshotMetadata.MatchingSource(snapshot) is { } source)
                    {
                        if (CleanIsrc(recording.Isrc) is { } requestedIsrc && CleanIsrc(source.Isrc) is { } snapshotIsrc &&
                            requestedIsrc != snapshotIsrc) continue;
                        source = source with { Isrc = source.Isrc ?? CleanIsrc(recording.Isrc) };
                        var scope = new TrackMatchScope(context.Principal.UserId, context.BackendInstanceId,
                            snapshot.ProviderAccountId, 2, snapshot.SnapshotVersion, access.LibraryIds.ToHashSet(StringComparer.Ordinal));
                        var match = engine.Decide(scope, source, candidates);
                        track = match.State == TrackMatchReviewState.Accepted
                            ? tracks.First(item => item.Id == match.SelectedLibraryTrackId)
                            : track != null && LocalCopyPreference.KnownRecordingMatches(source, LocalCopyPreference.ToCandidate(track)) &&
                              (policy?.Preferences.ExplicitFilter != "CleanOnly" ||
                               TrackMatchSignals.Explicitness(LocalCopyPreference.ToCandidate(track)) == false) ? track : null;
                    }

                }
                if (track != null) result.TryAdd(recording.PublicId, track.BackendItemId);
            }
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || context.CancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            _logger?.LogWarning("Native recording lookup unavailable ({ErrorType}); retaining external results", error.GetType().Name);
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    // Matching stored songs against the whole library read and indexed every accessible song on each item details
    // request, most of a second for a large library, so the indexed copy is kept until the library changes.
    private static readonly TimeSpan SharedLibraryLifetime = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, SharedLibrary> _sharedLibraries = new(StringComparer.Ordinal);
    private sealed record SharedLibrary(string Fingerprint, DateTimeOffset LoadedAt, LibraryTrackRecord[] Tracks, TrackMatchCandidateSet Candidates);

    private async Task<(LibraryTrackRecord[] Tracks, TrackMatchCandidateSet Candidates)> SharedLibraryAsync(AllstarrDbContext db,
        ProtocolExecutionContext context, BackendLibraryAccess access, TrackMatchDecisionEngine engine, CancellationToken cancellationToken)
    {
        var key = $"{context.Protocol}|{context.BackendInstanceId}|{string.Join(',', access.LibraryIds.Order(StringComparer.Ordinal))}";
        var fingerprint = await LibraryTrackFingerprint.ReadAsync(db, cancellationToken);
        var now = _clock.UtcNow;
        if (_sharedLibraries.TryGetValue(key, out var shared) && shared.Fingerprint == fingerprint &&
            now - shared.LoadedAt < SharedLibraryLifetime) return (shared.Tracks, shared.Candidates);
        var tracks = PreferredFirst(await LibraryTrackAccess.Query(db, context, access).ToArrayAsync(cancellationToken));
        var candidates = engine.PrepareCandidates(tracks.Select(LocalCopyPreference.ToCandidate));
        if (_sharedLibraries.Count >= 16) _sharedLibraries.Clear();
        _sharedLibraries[key] = new(fingerprint, now, tracks, candidates);
        return (tracks, candidates);
    }

    private static LibraryTrackRecord[] PreferredFirst(IEnumerable<LibraryTrackRecord> tracks) => tracks
        .OrderByDescending(item => LocalCopyPreference.FormatRank(Path.GetExtension(item.FilePath)))
        .ThenByDescending(item => item.SourceModifiedAt)
        .ThenBy(item => item.BackendLibraryId).ThenBy(item => item.BackendItemId).ThenBy(item => item.Id).ToArray();

    private static string? CleanIsrc(string? value)
    {
        var normalized = value?.Trim().Replace("-", "", StringComparison.Ordinal).ToUpperInvariant();
        return normalized?.Length == 12 ? normalized : null;
    }
}

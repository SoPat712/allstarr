using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using allstarr.Services.Local;
using allstarr.Services.Spotify;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.ClientApi;

public sealed class ItemMatchReader(
    IDbContextFactory<AllstarrDbContext> factory,
    ItemLyricsReader items,
    ILocalLibraryService localLibrary,
    TrackMatchCommandService commands,
    IProtocolProviderGateway providers,
    IProviderRegistry registry,
    ITrackIdentityService identities,
    IEffectiveProviderPolicyResolver policies, LibraryProviderResolver libraryProvider)
{
    public async Task<ClientItemMatch?> ReadAsync(HttpContext http, ProtocolExecutionContext execution, string itemId) =>
        (await LoadAsync(http, execution, itemId))?.Value;

    // Item details need only the state, confidence and who decided, so the candidates' ISRCs, icons and revision are skipped.
    public async Task<ClientItemMatch?> ReadSummaryAsync(HttpContext http, ProtocolExecutionContext execution, string itemId,
        TrackMatchLibraryMemo libraryMemo) =>
        (await LoadAsync(http, execution, itemId, libraryMemo, summary: true))?.Value;

    public async Task<ClientItemMatch?> SetAsync(HttpContext http, ProtocolExecutionContext execution, string itemId, SetClientMatchRequest request)
    {
        var match = await LoadAsync(http, execution, itemId);
        if (match == null) return null;
        RequireRevision(match, request.ExpectedRevision);
        var candidate = match.Candidates.SingleOrDefault(item => item.Value.CandidateId == request.CandidateId)
            ?? throw new ArgumentException("The candidate is unavailable.");
        var result = await commands.ResolveSnapshotAsync(Actor(execution), match.Snapshot.Id,
            new(candidate.LibraryTrackId.HasValue ? "local" : "provider", candidate.LibraryTrackId,
                ExternalProvider: candidate.ProviderId, ExternalId: candidate.ExternalId, AuthorityScope: "personal",
                ExpectedAuthority: Stamp(match.Personal), ExpectedMatchRevision: request.ExpectedRevision),
            http.TraceIdentifier, execution.CancellationToken);
        RequireSuccess(result.Succeeded, result.Failure);
        return await ReadAsync(http, execution, itemId);
    }

    public async Task<ClientItemMatch?> RejectAsync(HttpContext http, ProtocolExecutionContext execution, string itemId, RejectClientMatchRequest request)
    {
        var match = await LoadAsync(http, execution, itemId);
        if (match == null) return null;
        RequireRevision(match, request.ExpectedRevision);
        var result = await commands.ResolveSnapshotAsync(Actor(execution), match.Snapshot.Id,
            new("reject", AuthorityScope: "personal", ExpectedAuthority: Stamp(match.Personal), ExpectedMatchRevision: request.ExpectedRevision),
            http.TraceIdentifier, execution.CancellationToken);
        RequireSuccess(result.Succeeded, result.Failure);
        return await ReadAsync(http, execution, itemId);
    }

    public async Task<ClientItemMatch?> ClearAsync(HttpContext http, ProtocolExecutionContext execution, string itemId, string expectedRevision)
    {
        var match = await LoadAsync(http, execution, itemId);
        if (match == null) return null;
        RequireRevision(match, expectedRevision);
        if (match.Personal == null) return match.Value;
        var result = await commands.ClearClientMatchAsync(Actor(execution), match.Snapshot.Id, match.Personal,
            expectedRevision, http.TraceIdentifier, execution.CancellationToken);
        RequireSuccess(result.Succeeded, result.Failure);
        return await ReadAsync(http, execution, itemId);
    }

    private async Task<Match?> LoadAsync(HttpContext http, ProtocolExecutionContext execution, string itemId,
        TrackMatchLibraryMemo? libraryMemo = null, bool summary = false)
    {
        var actor = Actor(execution);
        var token = execution.CancellationToken;
        await using var db = await factory.CreateDbContextAsync(token);
        var query = db.ExternalMetadataSnapshots.AsNoTracking().Where(item => item.OwnerUserId == actor.UserId &&
            item.ResourceKind == "track" && item.ProviderId != "allstarr" &&
            item.Protocol == execution.Protocol.ToString().ToLowerInvariant() && item.BackendInstanceId == execution.BackendInstanceId &&
            item.BackendPrincipalId == execution.VerifiedBackendPrincipalId);
        var (external, provider, kind, externalId) = localLibrary.ParseExternalId(itemId);
        if (external && kind != "song") return null;
        var requestedProvider = provider;
        if (external) provider = registry.ResolveProviderId(provider!);
        ExternalMetadataSnapshotRecord? snapshot = null;
        if (external && provider != "allstarr")
        {
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(externalId!)));
            snapshot = await query.Where(item => (item.ProviderId == provider || item.ProviderId == requestedProvider) && item.ExternalIdHash == hash)
                .OrderByDescending(item => item.RetrievedAt).ThenByDescending(item => item.SnapshotVersion)
                .ThenByDescending(item => item.Id).FirstOrDefaultAsync(token);
        }
        var recording = external
            ? (await identities.ResolveRecordingAsync(execution.RequireActor(), itemId, token))?.Id
            : await items.ResolveRecordingIdAsync(http, execution, itemId);
        if (recording.HasValue)
        {
            // The recording's decisions and identities are found first, so only their snapshots are read rather than
            // every song the listener has.
            var decided = (await db.TrackMatches.AsNoTracking().Where(decision => decision.OwnerUserId == actor.UserId &&
                    decision.CanonicalRecordingId == recording).Select(decision => decision.ExternalSnapshotId).ToArrayAsync(token)).ToHashSet();
            var linked = await db.ProviderTrackIdentities.AsNoTracking().Where(identity => identity.CanonicalRecordingId == recording)
                .Select(identity => new { identity.Id, identity.ProviderId, identity.ExternalIdHash }).ToArrayAsync(token);
            var decidedIds = decided.ToArray();
            var identityIds = linked.Select(identity => (Guid?)identity.Id).ToArray();
            var hashes = linked.Select(identity => identity.ExternalIdHash).Distinct(StringComparer.Ordinal).ToArray();
            var recorded = (await query.Where(item => decidedIds.Contains(item.Id) || identityIds.Contains(item.ProviderTrackIdentityId) ||
                    hashes.Contains(item.ExternalIdHash)).ToArrayAsync(token))
                .Where(item => decided.Contains(item.Id) || linked.Any(identity => identity.Id == item.ProviderTrackIdentityId ||
                    identity.ProviderId == item.ProviderId && identity.ExternalIdHash == item.ExternalIdHash))
                .OrderByDescending(item => decided.Contains(item.Id)).ThenByDescending(item => item.RetrievedAt)
                .ThenByDescending(item => item.SnapshotVersion).ThenByDescending(item => item.Id).FirstOrDefault();
            if (recorded != null && (snapshot == null || decided.Contains(recorded.Id))) snapshot = recorded;
        }
        if (snapshot == null) return null;
        var revision = summary ? "" : await TrackMatchRevision.ReadAsync(db, snapshot, actor.UserId, token);
        var review = await commands.GetClientReviewDataAsync(actor, snapshot.Id, token, libraryMemo);
        snapshot = review.Snapshots.OrderByDescending(item => item.RetrievedAt).ThenByDescending(item => item.SnapshotVersion)
            .ThenByDescending(item => item.Id).FirstOrDefault();
        if (snapshot == null) return null;
        var decision = review.LatestDecisions.OrderByDescending(item => item.DecidedAt).ThenByDescending(item => item.DecisionVersion).FirstOrDefault();
        var layers = ManualTrackOverrides.Select(review.ActiveOverrides.DistinctBy(item => item.Id), actor.UserId);
        var sourceIdentity = review.ProviderIdentities.FirstOrDefault(identity => identity.Id == snapshot.ProviderTrackIdentityId) ??
            review.ProviderIdentities.FirstOrDefault(identity => identity.ProviderId == snapshot.ProviderId && identity.ExternalIdHash == snapshot.ExternalIdHash);
        var order = await providers.GetPlayableProviderOrderAsync(execution.RequireActor(), token);
        var policy = await policies.ResolveForUserAsync(actor.UserId, token);
        var sourceSong = TrackIdentityService.ReadDisplaySong(snapshot.PayloadJson, snapshot.ProviderId, externalId ?? "stored");
        if (policy.Preferences.ExplicitFilter == "CleanOnly" && (sourceSong == null || !policy.Includes(sourceSong))) return null;
        var projection = TrackRouteProjector.Project(snapshot, decision, layers.Effective, sourceIdentity,
            review.LibraryTracks, review.ProviderIdentities, order, policy);
        var playableProviders = order.Select(ExternalTrackPlaybackPolicy.Normalize).ToHashSet(StringComparer.Ordinal);
        var rows = TrackMatchReviewCandidates.Build(snapshot, decision, projection,
            review.LibraryTracks.ToDictionary(item => item.Id), playableProviders,
            review.LibraryTracks.Select(item => item.Id).ToHashSet());
        List<Candidate> candidates = [];
        ClientMatchCandidate? current = null;
        foreach (var row in rows)
        {
            var json = JsonSerializer.SerializeToElement(row);
            var local = json.GetProperty("isLocal").GetBoolean();
            Guid? libraryId = local && Guid.TryParse(Text(json, "libraryTrackId"), out var id) ? id : null;
            var routes = json.GetProperty("providerTrackIds");
            var route = !local && routes.ValueKind == JsonValueKind.Object ? routes.EnumerateObject()
                .FirstOrDefault(pair => playableProviders.Contains(ExternalTrackPlaybackPolicy.Normalize(pair.Name))) : default;
            var library = local && !summary ? await libraryProvider.ResolveAsync(http, execution) : null;
            var providerId = local ? library?.Id ?? "library" : route.Name;
            var targetId = local ? null : route.Value.GetString();
            if (string.IsNullOrEmpty(providerId) || !local && string.IsNullOrWhiteSpace(targetId)) continue;
            if (policy.Preferences.ExplicitFilter == "CleanOnly")
            {
                if (local)
                {
                    var localRecording = review.LibraryTracks.FirstOrDefault(item => item.Id == libraryId)?.CanonicalRecordingId;
                    if (!await db.CanonicalRecordings.AnyAsync(item => item.Id == localRecording && item.IsExplicit == false, token)) continue;
                }
                else if (await providers.GetStoredSongAsync(execution, providerId, targetId!) is not { } song || !policy.Includes(song)) continue;
            }
            var key = local ? $"local:{libraryId}" : $"{providerId}:{targetId}";
            registry.TryGet(providerId, out var descriptor);
            var icon = summary ? null : (await ProviderArtworkResolver.ResolveIconsAsync(http.RequestServices, [providerId], token)).GetValueOrDefault(providerId);
            var isrc = Text(json, "candidateIsrc");
            if (string.IsNullOrWhiteSpace(isrc) && !summary)
                isrc = local ? review.LibraryTracks.FirstOrDefault(item => item.Id == libraryId)?.Isrc
                    : await ReadStoredIsrcAsync(db, execution, snapshot, review.ProviderIdentities, providerId, targetId!, token);
            var candidate = new ClientMatchCandidate(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key))),
                Text(json, "title"), Text(json, "artist"), Text(json, "album"), Milliseconds(json), isrc,
                library ?? icon?.Provider(providerId, descriptor?.DisplayName ?? execution.Protocol.ToString()) ??
                    new(providerId, descriptor?.DisplayName ?? execution.Protocol.ToString(), null), !local,
                Number(json, "confidence"), Strings(json, "reasons"));
            candidates.Add(new(candidate, libraryId, local ? null : providerId, targetId));
            if (json.GetProperty("isCurrent").GetBoolean() && projection.ReviewState != TrackMatchState.Rejected) current = candidate;
        }
        var state = projection.ReviewState;
        var confidence = Finite(decision?.Confidence);
        var nativeSource = external && provider != "allstarr" && provider == registry.ResolveProviderId(snapshot.ProviderId) &&
            CanonicalCatalogKeys.Hash(externalId!) == snapshot.ExternalIdHash;
        if (current == null && nativeSource && decision == null && layers.Effective == null &&
            playableProviders.Contains(ExternalTrackPlaybackPolicy.Normalize(provider)) && policy.IsAutomatic(provider!))
        {
            var metadata = TrackIdentityService.ReadDisplaySong(snapshot.PayloadJson, provider!, externalId!);
            if (metadata != null && policy.Includes(metadata))
            {
                registry.TryGet(provider!, out var descriptor);
                var icon = summary ? null : (await ProviderArtworkResolver.ResolveIconsAsync(http.RequestServices, [provider!], token)).GetValueOrDefault(provider!);
                current = new(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{provider}:{externalId}"))),
                    metadata.Title, metadata.Artist, metadata.Album,
                    metadata.PreciseDuration.HasValue ? (long)metadata.PreciseDuration.Value.TotalMilliseconds : metadata.Duration * 1000L,
                    summary ? null : await ReadStoredIsrcAsync(db, execution, snapshot, review.ProviderIdentities, provider!, externalId!, token),
                    icon?.Provider(provider!, descriptor?.DisplayName ?? provider!) ?? new(provider!, descriptor?.DisplayName ?? provider!, null),
                    true, 1, ["same_song"]);
                candidates.Insert(0, new(current, null, provider, externalId));
                state = TrackMatchState.Accepted;
            }
        }
        if (current is { IsExternal: true } && state is TrackMatchState.Accepted or TrackMatchState.Pinned &&
            decision?.CanonicalRecordingId == null && external && provider != "allstarr" &&
            candidates.Any(item => item.Value.CandidateId == current.CandidateId && item.ExternalId == externalId &&
                registry.ResolveProviderId(item.ProviderId!) == provider))
        {
            current = current with { Confidence = 1, Reasons = current.Reasons.Append("same_song").Distinct(StringComparer.Ordinal).ToArray() };
            for (var index = 0; index < candidates.Count; index++)
                if (candidates[index].Value.CandidateId == current.CandidateId) candidates[index] = candidates[index] with { Value = current };
            confidence = 1;
        }
        if (!summary && revision != await TrackMatchRevision.ReadAsync(db, snapshot, actor.UserId, token))
            throw new DbUpdateConcurrencyException("The match changed.");
        return new(new(state.ToString().ToLowerInvariant(), confidence, layers.EffectiveScope,
            current, candidates.Select(item => item.Value).DistinctBy(item => item.CandidateId).ToArray(), revision),
            snapshot, layers.Personal, candidates.DistinctBy(item => item.Value.CandidateId).ToArray());
    }

    private async Task<string?> ReadStoredIsrcAsync(AllstarrDbContext db, ProtocolExecutionContext execution,
        ExternalMetadataSnapshotRecord source, IReadOnlyList<ProviderTrackIdentityRecord> links, string providerId, string externalId,
        CancellationToken token)
    {
        providerId = registry.ResolveProviderId(providerId);
        var storedProviders = links.Select(item => item.ProviderId).Append(source.ProviderId).Append(providerId)
            .Where(item => registry.ResolveProviderId(item) == providerId).Distinct(StringComparer.Ordinal).ToArray();
        var hash = CanonicalCatalogKeys.Hash(externalId);
        var snapshots = await db.ExternalMetadataSnapshots.AsNoTracking().Where(item => item.OwnerUserId == source.OwnerUserId &&
            item.Protocol == source.Protocol && item.BackendInstanceId == source.BackendInstanceId &&
            item.BackendPrincipalId == execution.VerifiedBackendPrincipalId && storedProviders.Contains(item.ProviderId) && item.ExternalIdHash == hash &&
            (item.ResourceKind == "track" || item.ResourceKind == "playback-metadata") &&
            (item.ProviderAccountId == null || db.ProviderAccounts.Any(account => account.Id == item.ProviderAccountId && account.Enabled &&
                (account.OwnerUserId == null || account.OwnerUserId == source.OwnerUserId))))
            .OrderByDescending(item => item.RetrievedAt).ThenByDescending(item => item.SnapshotVersion).ToArrayAsync(token);
        foreach (var snapshot in snapshots)
        {
            try
            {
                using var payload = JsonDocument.Parse(snapshot.PayloadJson);
                var isrc = Text(payload.RootElement, "Isrc") ?? Text(payload.RootElement, "isrc") ?? Text(payload.RootElement, "ISRC");
                if (!string.IsNullOrWhiteSpace(isrc)) return isrc;
            }
            catch (JsonException) { }
        }
        var recordingIds = links.Where(item => registry.ResolveProviderId(item.ProviderId) == providerId && item.ExternalId == externalId &&
            CatalogIdentityPolicy.IsPlaybackEligible(item)).Select(item => item.CanonicalRecordingId).Distinct().ToArray();
        return await db.RecordingIdentifiers.AsNoTracking().Where(item => recordingIds.Contains(item.RecordingId) &&
            item.Kind == RecordingIdentifierKinds.Isrc).OrderBy(item => item.CreatedAt).Select(item => item.Value).FirstOrDefaultAsync(token);
    }

    private static TrackMatchActor Actor(ProtocolExecutionContext execution) => new(execution.RequireActor().EffectiveUserId!.Value, false);
    private static ManualAuthorityRevision? Stamp(ManualTrackOverrideRecord? authority) => authority == null ? null : new(authority.Id, authority.Revision);
    private static void RequireRevision(Match match, string revision)
    {
        if (match.Value.Revision != revision) throw new DbUpdateConcurrencyException("The match changed.");
    }
    private static void RequireSuccess(bool succeeded, TrackMatchCommandFailure failure)
    {
        if (succeeded) return;
        if (failure == TrackMatchCommandFailure.Conflict) throw new DbUpdateConcurrencyException("The match changed.");
        if (failure is TrackMatchCommandFailure.NotFound or TrackMatchCommandFailure.Forbidden) throw new KeyNotFoundException();
        throw new ArgumentException("The match selection is invalid.");
    }
    private static string? Text(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static double? Finite(double? value) => value.HasValue && double.IsFinite(value.Value) ? Math.Clamp(value.Value, 0, 1) : null;
    private static double? Number(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? Finite(number) : null;
    private static long? Milliseconds(JsonElement item) => item.GetProperty("durationMilliseconds") is { ValueKind: JsonValueKind.Number } value && value.TryGetDouble(out var number) && double.IsFinite(number) && number >= 0 && number < long.MaxValue ? (long)Math.Round(number) : null;
    private static string[] Strings(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Where(part => part.ValueKind == JsonValueKind.String).Select(part => part.GetString()!).ToArray() : [];
    private sealed record Candidate(ClientMatchCandidate Value, Guid? LibraryTrackId, string? ProviderId, string? ExternalId);
    private sealed record Match(ClientItemMatch Value, ExternalMetadataSnapshotRecord Snapshot, ManualTrackOverrideRecord? Personal, IReadOnlyList<Candidate> Candidates);
}

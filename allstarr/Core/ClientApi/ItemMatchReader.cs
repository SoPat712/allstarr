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
    IEffectiveProviderPolicyResolver policies, LibraryProviderResolver libraryProvider)
{
    public async Task<ClientItemMatch?> ReadAsync(HttpContext http, ProtocolExecutionContext execution, string itemId) =>
        (await LoadAsync(http, execution, itemId))?.Value;

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

    private async Task<Match?> LoadAsync(HttpContext http, ProtocolExecutionContext execution, string itemId)
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
        ExternalMetadataSnapshotRecord? snapshot = null;
        if (external && provider != "allstarr")
        {
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(externalId!)));
            snapshot = await query.Where(item => item.ProviderId == provider && item.ExternalIdHash == hash)
                .OrderByDescending(item => item.RetrievedAt).ThenByDescending(item => item.SnapshotVersion)
                .ThenByDescending(item => item.Id).FirstOrDefaultAsync(token);
        }
        if (snapshot == null)
        {
            var recording = await items.ResolveRecordingIdAsync(http, execution, itemId);
            if (!recording.HasValue) return null;
            query = query.Where(item => db.TrackMatches.Any(decision => decision.ExternalSnapshotId == item.Id &&
                    decision.OwnerUserId == actor.UserId && decision.CanonicalRecordingId == recording) ||
                db.ProviderTrackIdentities.Any(identity => identity.Id == item.ProviderTrackIdentityId && identity.CanonicalRecordingId == recording));
            snapshot = await query.OrderByDescending(item => item.RetrievedAt).ThenByDescending(item => item.SnapshotVersion)
                .ThenByDescending(item => item.Id).FirstOrDefaultAsync(token);
        }
        if (snapshot == null) return null;
        var revision = await TrackMatchRevision.ReadAsync(db, snapshot, actor.UserId, token);
        var review = await commands.GetClientReviewDataAsync(actor, snapshot.Id, token);
        snapshot = review.Snapshots.OrderByDescending(item => item.RetrievedAt).ThenByDescending(item => item.SnapshotVersion)
            .ThenByDescending(item => item.Id).FirstOrDefault();
        if (snapshot == null) return null;
        var decision = review.LatestDecisions.OrderByDescending(item => item.DecidedAt).ThenByDescending(item => item.DecisionVersion).FirstOrDefault();
        var layers = ManualTrackOverrides.Select(review.ActiveOverrides.DistinctBy(item => item.Id), actor.UserId);
        var sourceIdentity = review.ProviderIdentities.FirstOrDefault(identity => identity.Id == snapshot.ProviderTrackIdentityId) ??
            review.ProviderIdentities.FirstOrDefault(identity => identity.ProviderId == snapshot.ProviderId && identity.ExternalIdHash == snapshot.ExternalIdHash);
        var order = await providers.GetPlayableProviderOrderAsync(execution.RequireActor(), token);
        var policy = await policies.ResolveForUserAsync(actor.UserId, token);
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
            var library = local ? await libraryProvider.ResolveAsync(http, execution) : null;
            var providerId = local ? library!.Id : route.Name;
            var targetId = local ? null : route.Value.GetString();
            if (string.IsNullOrEmpty(providerId) || !local && string.IsNullOrWhiteSpace(targetId)) continue;
            var key = local ? $"local:{libraryId}" : $"{providerId}:{targetId}";
            registry.TryGet(providerId, out var descriptor);
            var icon = (await ProviderArtworkResolver.ResolveIconsAsync(http.RequestServices, [providerId], token)).GetValueOrDefault(providerId);
            var candidate = new ClientMatchCandidate(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key))),
                Text(json, "title"), Text(json, "artist"), Text(json, "album"), Milliseconds(json), Text(json, "candidateIsrc"),
                library ?? icon?.Provider(providerId, descriptor?.DisplayName ?? execution.Protocol.ToString()) ??
                    new(providerId, descriptor?.DisplayName ?? execution.Protocol.ToString(), null), !local,
                Number(json, "confidence"), Strings(json, "reasons"));
            candidates.Add(new(candidate, libraryId, local ? null : providerId, targetId));
            if (json.GetProperty("isCurrent").GetBoolean() && projection.ReviewState != TrackMatchState.Rejected) current = candidate;
        }
        if (revision != await TrackMatchRevision.ReadAsync(db, snapshot, actor.UserId, token))
            throw new DbUpdateConcurrencyException("The match changed.");
        return new(new(projection.ReviewState.ToString().ToLowerInvariant(), Finite(decision?.Confidence), layers.EffectiveScope,
            current, candidates.Select(item => item.Value).DistinctBy(item => item.CandidateId).ToArray(), revision),
            snapshot, layers.Personal, candidates.DistinctBy(item => item.Value.CandidateId).ToArray());
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

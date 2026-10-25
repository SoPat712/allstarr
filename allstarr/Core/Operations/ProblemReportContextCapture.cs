using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Operations;

public sealed partial class ProblemReportService
{
    private async Task<ProblemReportContext> CaptureAsync(AllstarrDbContext db, AdminAuthSession session,
        UserRecord actor, ProblemReportSelector? selector, CancellationToken token)
    {
        var now = clock.UtcNow;
        if (selector == null) return new(1, now, null, null, null, null, null, []);
        var owner = actor;
        PlaybackActivityState? activity = null;
        if (selector.Playback is { } selected)
        {
            if (selected.SubjectUserId != actor.Id && !actor.IsAdmin) throw ProblemReportException.Missing();
            owner = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == selected.SubjectUserId && item.Enabled &&
                item.BackendType == actor.BackendType && item.BackendInstanceId == actor.BackendInstanceId, token)
                ?? throw ProblemReportException.Missing();
            activity = playbackSources.SelectMany(source => source.GetActivePlaybackStates(TimeSpan.FromMinutes(5)))
                .Where(item => item.UserId == owner.Id && item.DeviceId == selected.DeviceId && SameItem(item.ItemId, selector.ItemId!) &&
                    item.LastActivity.ToUniversalTime() >= now.AddMinutes(-5).UtcDateTime && item.LastActivity.ToUniversalTime() <= now.UtcDateTime)
                .OrderByDescending(item => item.LastActivity).FirstOrDefault() ?? throw ProblemReportException.Missing();
        }

        var snapshotQuery = db.ExternalMetadataSnapshots.AsNoTracking().Where(item => item.ResourceKind == "track" &&
            item.Protocol == actor.BackendType && item.BackendInstanceId == actor.BackendInstanceId);
        var metadataOwner = owner;
        ExternalMetadataSnapshotRecord? snapshot;
        var external = selector.ItemId == null ? null : ExternalPlaybackMetadataResolver.ParseTrackIdentity(selector.ItemId);
        if (selector.ExternalSnapshotId.HasValue)
        {
            snapshot = await snapshotQuery.SingleOrDefaultAsync(item => item.Id == selector.ExternalSnapshotId &&
                (actor.IsAdmin || item.OwnerUserId == actor.Id), token) ?? throw ProblemReportException.Missing();
            var snapshotOwner = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == snapshot.OwnerUserId && item.Enabled &&
                item.BackendType == snapshot.Protocol && item.BackendInstanceId == snapshot.BackendInstanceId &&
                item.BackendPrincipalId == snapshot.BackendPrincipalId, token) ?? throw ProblemReportException.Missing();
            metadataOwner = snapshotOwner;
            if (snapshot.ProviderAccountId.HasValue && await CurrentAccountAsync(db, snapshot.ProviderAccountId.Value, snapshot.ProviderId, snapshotOwner.Id, token) == null)
                throw ProblemReportException.Missing();
        }
        else if (external != null && external.Value.Provider != "allstarr")
        {
            var provider = NormalizeProvider(external.Value.Provider);
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(external.Value.ExternalId)));
            var candidates = await snapshotQuery.Where(item => item.OwnerUserId == owner.Id && item.BackendPrincipalId == owner.BackendPrincipalId &&
                item.ExternalIdHash == hash).OrderByDescending(item => item.RetrievedAt).Take(32).ToArrayAsync(token);
            snapshot = null;
            foreach (var candidate in candidates.Where(item => NormalizeProvider(item.ProviderId) == provider))
                if (!candidate.ProviderAccountId.HasValue || await CurrentAccountAsync(db, candidate.ProviderAccountId.Value, candidate.ProviderId, owner.Id, token) != null)
                { snapshot = candidate; break; }
            if (snapshot == null && activity == null && !await CanReferenceProviderAsync(db, owner, provider, token))
                throw ProblemReportException.Missing();
        }
        else snapshot = null;

        var decision = snapshot == null ? null : await db.TrackMatches.AsNoTracking().Where(item =>
            item.OwnerUserId == snapshot.OwnerUserId && item.ExternalSnapshotId == snapshot.Id)
            .OrderByDescending(item => item.DecisionVersion).FirstOrDefaultAsync(token);
        ProviderTrackIdentityRecord? identity = null;
        if (snapshot?.ProviderTrackIdentityId is { } identityId)
            identity = await db.ProviderTrackIdentities.AsNoTracking().SingleOrDefaultAsync(item => item.Id == identityId &&
                item.ProviderId == snapshot.ProviderId && item.ExternalIdHash == snapshot.ExternalIdHash &&
                (item.ProviderAccountId == null || item.ProviderAccountId == snapshot.ProviderAccountId), token);
        var authority = snapshot == null ? new ManualTrackOverrideLayers(null, null) :
            await ManualTrackOverrides.ReadAsync(db, snapshot, owner.Id, token);

        LibraryTrackRecord? library = null;
        var nativeSelection = external == null && selector.ItemId != null;
        if (nativeSelection)
        {
            var access = await libraryAccess.ResolveUserAsync(actor.Id, token);
            if (access.Context?.Principal?.UserId != actor.Id || access.Context.BackendInstanceId != actor.BackendInstanceId ||
                access.Context.VerifiedBackendPrincipalId != actor.BackendPrincipalId ||
                access.Context.Protocol.ToString().ToLowerInvariant() != actor.BackendType) throw ProblemReportException.Missing();
            var accessible = LibraryTrackAccess.Query(db, access);
            library = await accessible.FirstOrDefaultAsync(item => item.BackendItemId == selector.ItemId, token);
            if (library == null && external == null && snapshot == null) throw ProblemReportException.Missing();
        }
        Guid? canonical = library?.CanonicalRecordingId ?? decision?.CanonicalRecordingId ?? identity?.CanonicalRecordingId;
        ProblemReportContext? canonicalContext = null;
        if (external?.Provider == "allstarr")
        {
            var recording = await db.CanonicalRecordings.AsNoTracking().SingleOrDefaultAsync(item => item.PublicId == selector.ItemId, token);
            if (recording == null) throw ProblemReportException.Missing();
            // Resolve only evidence belonging to the viewer; a public recording ID is not account access.
            var ownedSnapshot = await snapshotQuery.Where(item => item.OwnerUserId == owner.Id && item.BackendPrincipalId == owner.BackendPrincipalId &&
                db.TrackMatches.Any(match => match.ExternalSnapshotId == item.Id && match.OwnerUserId == owner.Id && match.CanonicalRecordingId == recording.Id))
                .OrderByDescending(item => item.RetrievedAt).Select(item => (Guid?)item.Id).FirstOrDefaultAsync(token);
            if (ownedSnapshot.HasValue)
            {
                canonicalContext = await CaptureAsync(db, session, actor, new(ExternalSnapshotId: ownedSnapshot), token);
                canonical = canonicalContext.Song?.CanonicalRecordingId;
            }
            else throw ProblemReportException.Missing();
        }

        var metadata = snapshot == null ? (null, null, null, (long?)null) : Metadata(snapshot.PayloadJson);
        var catalogProvider = snapshot?.ProviderId ?? external?.Provider ?? actor.BackendType;
        var song = canonicalContext?.Song is { } canonicalSong ? canonicalSong with { ItemId = selector.ItemId } : new ProblemReportSong(selector.ItemId, snapshot?.Id, canonical, nativeSelection ? library?.Id : null,
            ProblemReportText.Clean(nativeSelection ? library?.Title : metadata.Item1), ProblemReportText.Clean(nativeSelection ? library?.Artist : metadata.Item2),
            ProblemReportText.Clean(nativeSelection ? library?.Album : metadata.Item3), nativeSelection ? library?.DurationMilliseconds : metadata.Item4,
            SafeProvider(catalogProvider));
        CachedReportSong? cached = null;
        var cacheExternalId = external is { Provider: not "allstarr" } selectedExternal ? selectedExternal.ExternalId : identity?.ExternalId;
        if (library == null && cacheExternalId != null && (song.Title == null || song.Artist == null || song.Album == null || song.DurationMilliseconds == null))
        {
            cached = await CachedSongAsync(db, metadataOwner, catalogProvider, cacheExternalId, token);
            if (cached != null) song = song with
            {
                Title = song.Title ?? ProblemReportText.Clean(cached.Facts.Title),
                Artist = song.Artist ?? ProblemReportText.Clean(cached.Facts.Artist),
                Album = song.Album ?? ProblemReportText.Clean(cached.Facts.Album),
                DurationMilliseconds = song.DurationMilliseconds ?? cached.Facts.DurationMilliseconds,
                CanonicalRecordingId = song.CanonicalRecordingId ?? cached.Facts.CanonicalRecordingId
            };
        }
        ProblemReportMatch? match = canonicalContext?.Match;
        if (snapshot != null && (decision != null || authority.Effective != null))
        {
            var savedState = authority.Effective?.Decision switch
            {
                ManualOverrideDecision.Pin => "pinned",
                ManualOverrideDecision.Reject => "rejected",
                _ => decision?.State.ToString().ToLowerInvariant() ?? "unresolved"
            };
            match = new(decision?.Id, savedState, decision?.DecisionVersion,
                decision != null && double.IsFinite(decision.Confidence) ? Math.Clamp(decision.Confidence, 0, 1) : null,
                authority.EffectiveScope, decision == null ? [] : ProblemReportText.ReasonArray(decision.ReasonsJson));
        }

        ProblemReportPlayback? playback = null;
        PlaybackStreamSource? stream = null;
        if (activity != null)
        {
            stream = playbackDeliveries?.StreamFor(owner.Id, activity.DeviceId, activity.ItemId);
            if (stream != null && (!stream.Matches(owner.BackendType, owner.BackendInstanceId) ||
                stream.OpenedAt > now || stream.OpenedAt > new DateTimeOffset(activity.LastActivity.ToUniversalTime()) ||
                stream.AccountId.HasValue && (await CurrentAccountAsync(db, stream.AccountId.Value, stream.ProviderId, owner.Id, token))?.Revision != stream.AccountRevision))
                stream = null;
            var native = external == null && library != null;
            playback = new(stream == null ? native ? actor.BackendType : null : SafeProvider(stream.ProviderId), stream != null || native,
                stream?.AccountId, stream?.AccountRevision, stream?.OpenedAt, new DateTimeOffset(activity.LastActivity.ToUniversalTime()),
                stream?.Cached, ProblemReportText.Rung(stream?.SeekRung), native ? "native-local-library" : ProblemReportText.Route(stream?.SelectionReason),
                activity.PositionTicks >= 0 ? activity.PositionTicks : null, ProblemReportText.Clean(activity.Client, 200));
        }
        IReadOnlyList<ProblemReportCheck> checks = canonicalContext?.Checks ?? [];
        if (stream != null)
            checks = await ChecksAsync(db, stream.ProviderId, stream.AccountId, stream.AccountRevision, now, token);
        else if (snapshot != null && activity == null)
        {
            var account = snapshot.ProviderAccountId.HasValue
                ? await CurrentAccountAsync(db, snapshot.ProviderAccountId.Value, snapshot.ProviderId, snapshot.OwnerUserId, token) : null;
            if (!snapshot.ProviderAccountId.HasValue || account != null)
                checks = await ChecksAsync(db, snapshot.ProviderId, account?.Id, account?.Revision, now, token);
        }
        else if (cached != null)
            checks = await ChecksAsync(db, cached.ProviderId, cached.AccountId, cached.AccountRevision, now, token);
        return new(1, now, activity == null ? null : owner.Id,
            activity == null ? null : ProblemReportText.Clean(owner.DisplayName, 200), song, match, playback, checks);
    }

    private async Task<CachedReportSong?> CachedSongAsync(AllstarrDbContext db, UserRecord owner,
        string provider, string externalId, CancellationToken token)
    {
        if (providerGateway is not ProtocolProviderGateway gateway || registry?.TryGet(provider, out var descriptor) != true ||
            descriptor!.Capabilities.FirstOrDefault(item => item.Capability == ProviderCapabilityKind.Metadata && item.HasUsableImplementation) is not { } capability)
            return null;
        var cacheActor = new ProviderActorContext(owner.IsAdmin ? ProviderActorKind.Administrator : ProviderActorKind.User,
            owner.Id, new(owner.BackendType, owner.BackendInstanceId, owner.BackendPrincipalId));
        if (capability.AccountRequirement is ProviderAccountRequirement.None or ProviderAccountRequirement.Optional &&
            await gateway.ReadCachedSongFactsAsync(cacheActor, descriptor.Id, externalId) is { } publicFacts)
            return new(publicFacts, descriptor.Id, null, null);
        if (capability.AccountRequirement == ProviderAccountRequirement.None) return null;
        var principal = new AllstarrPrincipal(owner.Id, owner.BackendType, owner.BackendInstanceId,
            owner.BackendPrincipalId, owner.DisplayName, owner.IsAdmin);
        var candidates = await reportAccounts.ResolveCandidatesAsync(new(principal, descriptor.Id, "metadata",
            AllowedScopes: capability.AllowedAccountScopes), token);
        foreach (var candidate in candidates.Take(32))
        {
            var account = await CurrentAccountAsync(db, candidate.Account.Id, descriptor.Id, owner.Id, token);
            if (account == null || account.Revision != candidate.Account.Revision) continue;
            var context = new ProviderAccountContext(account.Id, account.ProviderId, account.Scope, account.Revision,
                account.Enabled, account.OwnerUserId);
            if (await gateway.ReadCachedSongFactsAsync(cacheActor, descriptor.Id, externalId, context) is { } facts)
                return new(facts, descriptor.Id, account.Id, account.Revision);
        }
        return null;
    }

    private sealed record CachedReportSong(ProtocolCachedSongFacts Facts, string ProviderId, Guid? AccountId, long? AccountRevision);

    private async Task<IReadOnlyList<ProblemReportCheck>> ChecksAsync(AllstarrDbContext db, string providerId, Guid? accountId, long? accountRevision,
        DateTimeOffset now, CancellationToken token)
    {
        var results = new List<ProblemReportCheck>();
        foreach (var capability in new[] { "metadata", "streaming", "download", "lyrics" })
        {
            var key = accountId.HasValue ? ProviderRuntimeStatusKey.CreateManaged(providerId, capability, accountId.Value) :
                ProviderRuntimeStatusKey.CreateAccountFree(providerId, capability);
            var observed = health?.GetForDisplay(key, accountRevision);
            ProblemReportCheck? check = observed != null && observed.AccountRevision == accountRevision &&
                observed.ObservedAt >= now.AddDays(-30) && observed.ObservedAt <= now && observed.Health != ProviderHealthState.Testing
                ? new(SafeProvider(key.Provider)!, capability, observed.Health.ToString().ToLowerInvariant(), observed.ObservedAt, ProblemReportText.Reason(observed.ReasonCode)) : null;
            var subject = $"{key.Provider}/{capability}/{key.ProviderAccountId?.ToString("N") ?? "public"}";
            var rows = await db.AuditEvents.AsNoTracking().Where(item => item.Category == ProviderCheckHistory.Category && item.SubjectId == subject &&
                item.CreatedAt >= now.AddDays(-30) && item.CreatedAt <= now).OrderByDescending(item => item.CreatedAt).Take(3).ToArrayAsync(token);
            foreach (var row in rows)
            {
                if (row.DetailsJson.Length > 16384) continue;
                try
                {
                    var result = JsonSerializer.Deserialize<ProviderCheckResult>(row.DetailsJson);
                    if (result?.Key != key || result.Revision != accountRevision || result.Status.TestedAt is not { } tested ||
                        tested > now || tested < now.AddDays(-30) || result.Status.Health == ProviderHealthState.Testing) continue;
                    if (check == null || tested > check.CheckedAt)
                        check = new(SafeProvider(key.Provider)!, capability, result.Status.Health.ToString().ToLowerInvariant(), tested, ProblemReportText.Reason(result.Status.ReasonCode));
                    break;
                }
                catch (JsonException) { }
            }
            if (check != null) results.Add(check);
        }
        return results.OrderByDescending(item => item.CheckedAt).Take(6).ToArray();
    }

    private async Task<bool> CanReferenceProviderAsync(AllstarrDbContext db, UserRecord owner, string provider, CancellationToken token)
    {
        if (registry?.TryGet(provider, out var descriptor) != true) return false;
        if (descriptor!.Capabilities.Any(item => item.Capability == ProviderCapabilityKind.Metadata && item.HasUsableImplementation &&
            item.AccountRequirement is ProviderAccountRequirement.None or ProviderAccountRequirement.Optional)) return true;
        var accounts = await db.ProviderAccounts.AsNoTracking().Where(item => item.Enabled && item.ProviderId == descriptor.Id &&
            (item.OwnerUserId == null || item.OwnerUserId == owner.Id)).Take(32).ToArrayAsync(token);
        foreach (var account in accounts)
            if (await CurrentAccountAsync(db, account.Id, account.ProviderId, owner.Id, token) != null) return true;
        return false;
    }

    private async Task<ProviderAccountRecord?> CurrentAccountAsync(AllstarrDbContext db, Guid id, string provider, Guid owner, CancellationToken token)
    {
        var account = await db.ProviderAccounts.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, token);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == owner && item.Enabled, token);
        if (account == null || user == null || NormalizeProvider(account.ProviderId) != NormalizeProvider(provider)) return null;
        try
        {
            var principal = new AllstarrPrincipal(user.Id, user.BackendType, user.BackendInstanceId,
                user.BackendPrincipalId, user.DisplayName, user.IsAdmin);
            var resolved = await reportAccounts.ResolveAsync(new(principal, account.ProviderId, "metadata", account.Id), token);
            return resolved?.Account.Revision == account.Revision ? account : null;
        }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string NormalizeProvider(string provider) => ProviderRuntimeStatusKey.CreateAccountFree(provider, "metadata").Provider;
    private static string? SafeProvider(string? value) => ProblemReportText.IsReference(value, 100) ? value!.ToLowerInvariant() : null;
    private static bool SameItem(string left, string right)
    {
        if (left == right) return true;
        var one = ExternalPlaybackMetadataResolver.ParseTrackIdentity(left);
        var two = ExternalPlaybackMetadataResolver.ParseTrackIdentity(right);
        return one != null && two != null && NormalizeProvider(one.Value.Provider) == NormalizeProvider(two.Value.Provider) && one.Value.ExternalId == two.Value.ExternalId;
    }
    private static (string?, string?, string?, long?) Metadata(string json)
    {
        if (json.Length > 131072) return default;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return default;
            long? duration = null;
            foreach (var property in root.EnumerateObject())
                if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt64(out var number) && number > 0 && number <= 604800000)
                {
                    if (property.Name.Equals("durationMilliseconds", StringComparison.OrdinalIgnoreCase)) duration = number;
                    else if (duration == null && property.Name.Equals("durationSeconds", StringComparison.OrdinalIgnoreCase) && number <= 604800) duration = number * 1000;
                }
            return (Text(root, "title", "name"), TrackSnapshotMetadata.Artist(root), Text(root, "album", "albumTitle"), duration);
        }
        catch (JsonException) { return default; }
    }
    private static string? Text(JsonElement root, params string[] names) => root.EnumerateObject().Where(item =>
        names.Contains(item.Name, StringComparer.OrdinalIgnoreCase) && item.Value.ValueKind == JsonValueKind.String).Select(item => item.Value.GetString()).FirstOrDefault();
}

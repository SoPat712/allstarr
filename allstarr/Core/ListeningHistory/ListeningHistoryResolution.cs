using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Music;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Intelligence;

internal sealed record ListeningHistoryResolutionPayload(IntelligenceScope Scope, int Cursor = 0);

public sealed class ListeningHistoryResolution(
    IDbContextFactory<AllstarrDbContext> factory,
    DurableJobQueue jobs,
    TrackMatchPlayableSearch matches,
    TrackMatchDecisionEngine matcher,
    IProtocolProviderGateway providers,
    IBackendLibraryAccessResolver libraryAccess,
    ITrackIdentityService identities,
    PersonalMixService mixes,
    IPlatformClock clock,
    JobPayloadPolicy payloadPolicy,
    TimeProvider? timeProvider = null) : IDurableJobHandler, IDurableJobRecoveryHandler
{
    public const string JobTypeName = "listening-history.resolve";
    private const string ResourceKind = "history-resolution";
    private const int ProviderLimit = 100;
    public string JobType => JobTypeName;

    private static IQueryable<ListeningEventRecord> Unresolved(AllstarrDbContext db, IntelligenceScope scope) =>
        db.ListeningEvents.Where(item => item.OwnerUserId == scope.OwnerUserId && item.Protocol == scope.Protocol &&
            item.BackendInstanceId == scope.BackendInstanceId && item.SourceKind == "import" && item.CanonicalRecordingId == null);

    private static IQueryable<ExternalMetadataSnapshotRecord> Receipts(AllstarrDbContext db, IntelligenceScope scope) =>
        db.ExternalMetadataSnapshots.Where(item => item.OwnerUserId == scope.OwnerUserId && item.Protocol == scope.Protocol &&
            item.BackendInstanceId == scope.BackendInstanceId && item.ProviderId == "allstarr" && item.ResourceKind == ResourceKind);

    private static string MetadataKey(string? title, string? artist) =>
        FuzzyMatcher.NormalizeTitleForMatching(title ?? "", artist ?? "") + ":" + FuzzyMatcher.NormalizeForMatching(artist ?? "");

    private static string SongKey(ListeningEventRecord play) => ListeningHistoryImportService.Hash(
        play.ProviderId == "spotify" && play.ProviderTrackReference?.StartsWith("spotify:track:", StringComparison.Ordinal) == true
            ? "spotify:" + ListeningHistoryImportService.Hash(play.ProviderTrackReference[14..])
            : MetadataKey(play.Title, play.Artist));

    internal static async Task EnqueueAsync(IDbContextFactory<AllstarrDbContext> factory, DurableJobQueue jobs,
        IntelligenceScope scope, DateTimeOffset now, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        if (!await IntelligencePolicyService.OwnsBackendAsync(db, scope, token)) return;
        var plays = await Unresolved(db, scope).AsNoTracking().ToArrayAsync(token);
        var recent = await Receipts(db, scope).Where(item => item.RetrievedAt > now.AddDays(-7))
            .Select(item => item.ExternalIdHash).ToHashSetAsync(token);
        if (!plays.Any(play => !recent.Contains(SongKey(play)))) return;
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        if (await db.Jobs.AnyAsync(item => item.OwnerUserId == scope.OwnerUserId && item.Type == JobTypeName &&
            (item.State == DurableJobState.Pending || item.State == DurableJobState.Running || item.State == DurableJobState.RetryScheduled), token)) return;
        var previous = await db.Jobs.Where(item => item.OwnerUserId == scope.OwnerUserId && item.Type == JobTypeName)
            .OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id).Select(item => (Guid?)item.Id).FirstOrDefaultAsync(token);
        await jobs.EnqueueInExistingTransactionAsync(db, new DurableJobEnqueueRequest<ListeningHistoryResolutionPayload>(
            JobTypeName, $"history:{scope.Protocol}:{scope.BackendInstanceId}:{previous}", new(scope), scope.OwnerUserId,
            MaxDeferrals: 10000), token);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }

    public Task RecoverAsync(CancellationToken token) => EnqueuePendingAsync(factory, jobs, clock.UtcNow, token);

    internal static async Task EnqueuePendingAsync(IDbContextFactory<AllstarrDbContext> factory,
        DurableJobQueue jobs, DateTimeOffset now, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var listeners = await db.Users.AsNoTracking().Where(user => user.Enabled && !user.IsSynthetic &&
            db.ListeningEvents.Any(play => play.OwnerUserId == user.Id && play.Protocol == user.BackendType &&
                play.BackendInstanceId == user.BackendInstanceId && play.SourceKind == "import" && play.CanonicalRecordingId == null))
            .Select(user => new IntelligenceScope(user.Id, user.BackendType, user.BackendInstanceId)).ToArrayAsync(token);
        foreach (var scope in listeners) await EnqueueAsync(factory, jobs, scope, now, token);
    }

    public async Task<DurableJobCompletion> ExecuteAsync(DurableJobExecutionContext execution, CancellationToken token)
    {
        var saved = execution.Claim.Payload.Deserialize<ListeningHistoryResolutionPayload>();
        if (saved == null || saved.Scope.OwnerUserId != execution.Claim.OwnerUserId)
            return DurableJobCompletion.Failure("history_resolution_scope_invalid", "The saved listener is unavailable.");
        await using var db = await factory.CreateDbContextAsync(token);
        if (!await IntelligencePolicyService.OwnsBackendAsync(db, saved.Scope, token))
            return DurableJobCompletion.Failure("history_resolution_listener_unavailable", "The saved listener is unavailable.");
        var user = await db.Users.AsNoTracking().SingleAsync(item => item.Id == saved.Scope.OwnerUserId, token);
        ProtocolExecutionContext Execution() => new(Enum.Parse<ProtocolKind>(user.BackendType, true), user.BackendInstanceId,
            user.BackendPrincipalId, new(user.Id, user.BackendType, user.BackendInstanceId, user.BackendPrincipalId, user.DisplayName, user.IsAdmin),
            execution.Claim.JobId.ToString("N"), clock.UtcNow.AddMinutes(10), token);
        var context = Execution();
        var access = await libraryAccess.ResolveAsync(context, token);
        if (!access.Succeeded)
            return DurableJobCompletion.Defer("history_resolution_library_unavailable", "Waiting for the listener's library access.", TimeSpan.FromMinutes(1));
        var tracks = await LibraryTrackAccess.Query(db, context, access).ToArrayAsync(token);
        var local = tracks.GroupBy(track => MetadataKey(track.Title, track.Artist)).ToDictionary(group => group.Key, group => group.ToArray());
        var recent = await Receipts(db, saved.Scope).Where(item => item.RetrievedAt > clock.UtcNow.AddDays(-7))
            .Select(item => item.ExternalIdHash).ToHashSetAsync(token);
        var groups = (await Unresolved(db, saved.Scope).ToArrayAsync(token)).GroupBy(SongKey)
            .Where(group => !recent.Contains(group.Key)).OrderByDescending(group => group.Count(item => item.State == ListeningEventState.Completed))
            .ThenBy(group => group.Key, StringComparer.Ordinal).ToArray();
        var scope = new TrackMatchScope(user.Id, user.BackendInstanceId, null, 2, 1, access.LibraryIds.ToHashSet());
        var remaining = new List<IGrouping<string, ListeningEventRecord>>();
        foreach (var group in groups)
        {
            var play = group.First();
            var source = Source(group.Key, play);
            var candidates = local.GetValueOrDefault(MetadataKey(play.Title, play.Artist)) ?? [];
            if (candidates.Length == 0) { remaining.Add(group); continue; }
            var decision = matcher.Decide(scope, source, candidates.Select(track => new LocalTrackMatchCandidate(
                track.Id, track.OwnerUserId, track.BackendInstanceId, track.BackendLibraryId, track.BackendItemId,
                track.CanonicalRecordingId, track.Title, track.Artist, track.Album, track.AlbumArtist, track.DurationMilliseconds,
                track.Isrc, track.MusicBrainzRecordingId, null)).ToArray(), null);
            var selected = candidates.FirstOrDefault(track => track.Id == decision.SelectedLibraryTrackId);
            if (selected == null || !Accepted(decision) || !SameEdition(play.Title, selected.Title)) { remaining.Add(group); continue; }
            var canonical = selected.CanonicalRecordingId ?? await db.ListeningEvents.AsNoTracking().Where(item =>
                item.OwnerUserId == user.Id && item.Protocol == user.BackendType && item.BackendInstanceId == user.BackendInstanceId &&
                item.LibraryTrackId == selected.Id && item.CanonicalRecordingId != null).Select(item => item.CanonicalRecordingId).FirstOrDefaultAsync(token)
                ?? (await identities.CreateRecordingAsync(context.RequireActor(), context.CorrelationId,
                    selected.Isrc, selected.MusicBrainzRecordingId, token)).Recording.Id;
            selected.CanonicalRecordingId = canonical;
            await SaveAsync(group, canonical, selected, null, null);
        }

        var attempted = 0;
        foreach (var group in remaining.Take(ProviderLimit))
        {
            token.ThrowIfCancellationRequested();
            if (attempted++ > 0) await Task.Delay(TimeSpan.FromSeconds(2), timeProvider ?? TimeProvider.System, token);
            context = Execution();
            var play = group.First();
            var match = await matches.MatchAsync(context, Source(group.Key, play), scope, [], null, token);
            var selected = match.SelectedExternal;
            Song? song = null;
            if (Accepted(match.Decision) && selected != null && SameEdition(play.Title, selected.Title) &&
                !(FuzzyMatcher.HasCleanRecordingLabel(play.Title) && selected.ExplicitContentLyrics == 1))
            {
                var reference = $"ext-{selected.ExternalProvider}-song-{selected.ExternalId}";
                await providers.RememberTrackAsync(context, reference);
                song = await providers.GetSongAsync(context, selected.ExternalProvider!, selected.ExternalId!);
                if (song != null && (!SameEdition(play.Title, song.Title) ||
                    FuzzyMatcher.HasCleanRecordingLabel(play.Title) && song.ExplicitContentLyrics == 1)) song = null;
            }
            var identity = song?.CanonicalRecordingId is { } recording
                ? (await db.ProviderTrackIdentities.AsNoTracking().Where(item => item.CanonicalRecordingId == recording &&
                    item.ProviderId == selected!.ExternalProvider && item.ExternalId == selected.ExternalId &&
                    (item.Scope == ProviderIdentityScope.Catalog || db.ProviderAccounts.Any(account =>
                        account.Id == item.ProviderAccountId && (account.OwnerUserId == user.Id || account.OwnerUserId == null) && account.Enabled)))
                    .OrderBy(item => item.Id).ToArrayAsync(token)).FirstOrDefault(CatalogIdentityPolicy.IsPlaybackEligible) : null;
            var library = song?.CanonicalRecordingId is { } id
                ? tracks.FirstOrDefault(track => track.CanonicalRecordingId == id && SameEdition(play.Title, track.Title)) : null;
            await SaveAsync(group, identity == null ? null : song?.CanonicalRecordingId, library, selected, identity);
            await execution.ReportProgressAsync(new("history-resolution", "Matching imported listening history.", saved.Cursor,
                saved.Cursor + Math.Max(0, remaining.Count - attempted)), token);
        }
        if (remaining.Count > ProviderLimit)
            return DurableJobCompletion.Defer("history_resolution_continuing", "Saved progress; more imported songs remain.", TimeSpan.FromMinutes(1));
        await mixes.RefreshAfterHistoryResolutionAsync(user.Id, execution.Claim.JobId, jobs, token);
        return DurableJobCompletion.Success();

        async Task SaveAsync(IGrouping<string, ListeningEventRecord> group, Guid? canonical, LibraryTrackRecord? library,
            Song? song, ProviderTrackIdentityRecord? identity)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            var next = saved with { Cursor = saved.Cursor + 1 };
            var json = payloadPolicy.SerializeAndValidate(next);
            if (await db.Jobs.Where(item => item.Id == execution.Claim.JobId && item.State == DurableJobState.Running &&
                item.AttemptCount == execution.Claim.AttemptNumber && item.CancellationRequestedAt == null)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.PayloadJson, json)
                    .SetProperty(item => item.UpdatedAt, clock.UtcNow).SetProperty(item => item.Revision, item => item.Revision + 1), token) != 1)
                throw new DurableJobExecutionChangedException();
            if (canonical != null)
            {
                foreach (var play in group)
                {
                    play.CanonicalRecordingId = canonical;
                    play.LibraryTrackId = library?.Id;
                    play.TrackReference = library?.BackendItemId ?? $"ext-{song!.ExternalProvider}-song-{song.ExternalId}";
                    if (identity != null)
                    {
                        play.ProviderTrackIdentityId = identity.Id;
                        play.ProviderId = identity.ProviderId;
                        play.ProviderAccountId = identity.ProviderAccountId;
                        play.ProviderTrackReference = identity.ExternalId;
                    }
                    play.Isrc ??= library?.Isrc ?? song?.Isrc;
                    play.Revision++;
                }
                foreach (var importGroup in group.GroupBy(play => play.ImportProvenance?.Split(':').ElementAtOrDefault(1)))
                {
                    if (!Guid.TryParse(importGroup.Key, out var importId)) continue;
                    var import = await db.ListeningHistoryImports.SingleOrDefaultAsync(item => item.Id == importId &&
                        item.OwnerUserId == user.Id && item.Protocol == user.BackendType && item.BackendInstanceId == user.BackendInstanceId, token);
                    if (import == null) continue;
                    import.ResolvedRows += importGroup.Count();
                    import.UnresolvedRows = Math.Max(0, import.UnresolvedRows - importGroup.Count());
                    import.UpdatedAt = clock.UtcNow;
                    import.Revision++;
                }
            }
            else
            {
                var version = await Receipts(db, saved.Scope).Where(item => item.ExternalIdHash == group.Key)
                    .Select(item => (int?)item.SnapshotVersion).MaxAsync(token) ?? 0;
                db.ExternalMetadataSnapshots.Add(new()
                {
                    Id = Guid.CreateVersion7(),
                    OwnerUserId = user.Id,
                    Protocol = user.BackendType,
                    BackendInstanceId = user.BackendInstanceId,
                    BackendPrincipalId = user.BackendPrincipalId,
                    ProviderId = "allstarr",
                    ResourceKind = ResourceKind,
                    ExternalIdHash = group.Key,
                    SnapshotVersion = version + 1,
                    SourceJobId = execution.Claim.JobId,
                    RetrievedAt = clock.UtcNow,
                    PayloadJson = "{}",
                    PayloadSha256 = ListeningHistoryImportService.Hash("{}"),
                    CorrelationId = context.CorrelationId
                });
            }
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            saved = next;
        }
    }

    private static ExternalTrackMatchSnapshot Source(string key, ListeningEventRecord play) => new(key,
        "history", key, play.Title ?? "", play.Artist ?? "", play.Album, play.AlbumArtist,
        play.DurationMilliseconds, play.Isrc, play.RecordingMusicBrainzId, FuzzyMatcher.HasCleanRecordingLabel(play.Title ?? "") ? false : null);

    private static bool Accepted(TrackMatchDecision decision) =>
        decision.State == TrackMatchReviewState.Accepted && !decision.RequiresReview;

    private static bool SameEdition(string? source, string? target) =>
        FuzzyMatcher.SemanticVersionTags(source).SetEquals(FuzzyMatcher.SemanticVersionTags(target));
}

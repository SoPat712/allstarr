using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Enrichment;
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

internal sealed record ListeningHistoryResolutionPayload(IntelligenceScope Scope, int Cursor = 0, int RetryOffset = 0,
    int? Total = null, string? Summary = null, double? ThroughputPerSecond = null);

public sealed partial class ListeningHistoryResolution(
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
    TimeProvider? timeProvider = null, SharedTagQueue? tags = null, TasteService? taste = null, ILogger<ListeningHistoryResolution>? logger = null) : IDurableJobHandler, IDurableJobRecoveryHandler
{
    public const string JobTypeName = "listening-history.resolve";
    private const string ResourceKind = "history-resolution";
    private const string ReceiptPayload = "{\"version\":2}";
    private const string UnmatchedPayload = "{\"version\":2,\"unmatched\":true}";
    private const int LocalBatchSize = 100;
    public string JobType => JobTypeName;

    private sealed record Resolution(IGrouping<string, ListeningEventRecord> Group, Guid? Canonical,
        LibraryTrackRecord? Library, Song? Song = null, ProviderTrackIdentityRecord? Identity = null, bool Retry = false, string Reason = "matched-local");

    private static IQueryable<ListeningEventRecord> Unresolved(AllstarrDbContext db, IntelligenceScope scope) =>
        db.ListeningEvents.Where(item => item.OwnerUserId == scope.OwnerUserId && item.Protocol == scope.Protocol &&
            item.BackendInstanceId == scope.BackendInstanceId && (item.SourceKind == "import" || item.SourceKind == "protocol" || item.SourceKind == "listenbrainz-api") && item.CanonicalRecordingId == null);

    // The first play of each song stands for it, so the order is fixed rather than left to whichever index SQLite picks.
    private static IQueryable<ListeningEventRecord> MatchingEvents(AllstarrDbContext db, IntelligenceScope scope) =>
        Unresolved(db, scope).AsNoTracking().OrderBy(item => item.State).ThenBy(item => item.ListenedAt).ThenBy(item => item.Id)
            .Select(item => new ListeningEventRecord
            {
                Id = item.Id,
                State = item.State,
                SourceKind = item.SourceKind,
                ProviderId = item.ProviderId,
                ProviderTrackReference = item.ProviderTrackReference,
                Title = item.Title,
                Artist = item.Artist,
                Album = item.Album,
                AlbumArtist = item.AlbumArtist,
                DurationMilliseconds = item.DurationMilliseconds,
                Isrc = item.Isrc,
                RecordingMusicBrainzId = item.RecordingMusicBrainzId,
                ImportProvenance = item.ImportProvenance
            });

    private static IQueryable<ExternalMetadataSnapshotRecord> Receipts(AllstarrDbContext db, IntelligenceScope scope) =>
        db.ExternalMetadataSnapshots.Where(item => item.OwnerUserId == scope.OwnerUserId && item.Protocol == scope.Protocol &&
            item.BackendInstanceId == scope.BackendInstanceId && item.ProviderId == "allstarr" && item.ResourceKind == ResourceKind);

    private static string MetadataKey(string? title, string? artist) =>
        FuzzyMatcher.NormalizeTitleForMatching(title ?? "", artist ?? "", artist ?? "") + ":" +
        FuzzyMatcher.NormalizeForMatching(FuzzyMatcher.SplitCredits(artist).FirstOrDefault() ?? "");

    private static string SongKey(ListeningEventRecord play) => ListeningHistoryImportService.Hash(
        play.ProviderId == "spotify" && play.ProviderTrackReference?.StartsWith("spotify:track:", StringComparison.Ordinal) == true
            ? "spotify:" + ListeningHistoryImportService.Hash(play.ProviderTrackReference[14..])
            : FuzzyMatcher.NormalizeTitleForMatching(play.Title ?? "", play.Artist ?? "") + ":" + FuzzyMatcher.NormalizeForMatching(play.Artist ?? ""));

    internal static async Task EnqueueAsync(IDbContextFactory<AllstarrDbContext> factory, DurableJobQueue jobs,
        IntelligenceScope scope, DateTimeOffset now, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        if (!await IntelligencePolicyService.OwnsBackendAsync(db, scope, token)) return;
        if (await db.Jobs.AsNoTracking().AnyAsync(item => item.OwnerUserId == scope.OwnerUserId && item.Type == JobTypeName &&
            (item.State == DurableJobState.Pending || item.State == DurableJobState.Running || item.State == DurableJobState.RetryScheduled), token)) return;
        var plays = await MatchingEvents(db, scope).ToArrayAsync(token);
        var recent = await Receipts(db, scope).Where(item => item.PayloadJson == UnmatchedPayload ||
                item.PayloadJson == ReceiptPayload && item.RetrievedAt > now.AddDays(-7))
            .Select(item => item.ExternalIdHash).ToHashSetAsync(token);
        var backfill = !await db.Jobs.AnyAsync(item => item.OwnerUserId == scope.OwnerUserId && item.Type == JobTypeName &&
            item.IdempotencyKey.StartsWith("history-styles:" + scope.Protocol + ":" + scope.BackendInstanceId + ":"), token);
        if (!plays.Any(play => !recent.Contains(SongKey(play))) && (!backfill ||
            !await db.ListeningEvents.AnyAsync(item => item.OwnerUserId == scope.OwnerUserId && item.Protocol == scope.Protocol &&
                item.BackendInstanceId == scope.BackendInstanceId, token))) return;
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        if (await db.Jobs.AnyAsync(item => item.OwnerUserId == scope.OwnerUserId && item.Type == JobTypeName &&
            (item.State == DurableJobState.Pending || item.State == DurableJobState.Running || item.State == DurableJobState.RetryScheduled), token)) return;
        var previous = await db.Jobs.Where(item => item.OwnerUserId == scope.OwnerUserId && item.Type == JobTypeName)
            .OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id).Select(item => (Guid?)item.Id).FirstOrDefaultAsync(token);
        await jobs.EnqueueInExistingTransactionAsync(db, new DurableJobEnqueueRequest<ListeningHistoryResolutionPayload>(
            JobTypeName, $"history-styles:{scope.Protocol}:{scope.BackendInstanceId}:{previous}", new(scope), scope.OwnerUserId,
            Priority: -100), token);
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
                play.BackendInstanceId == user.BackendInstanceId && (play.SourceKind == "import" || play.SourceKind == "protocol" || play.SourceKind == "listenbrainz-api")))
            .Select(user => new IntelligenceScope(user.Id, user.BackendType, user.BackendInstanceId)).ToArrayAsync(token);
        foreach (var scope in listeners) await EnqueueAsync(factory, jobs, scope, now, token);
    }

    public async Task<DurableJobCompletion> ExecuteAsync(DurableJobExecutionContext execution, CancellationToken token)
    {
        var saved = execution.Claim.Payload.Deserialize<ListeningHistoryResolutionPayload>();
        if (saved == null || saved.Scope.OwnerUserId != execution.Claim.OwnerUserId)
            return DurableJobCompletion.Failure("history_resolution_scope_invalid", "The saved listener is unavailable.");
        var initialCursor = saved.Cursor;
        await using var db = await factory.CreateDbContextAsync(token);
        if (!await IntelligencePolicyService.OwnsBackendAsync(db, saved.Scope, token))
            return DurableJobCompletion.Failure("history_resolution_listener_unavailable", "The saved listener is unavailable.");
        var user = await db.Users.AsNoTracking().SingleAsync(item => item.Id == saved.Scope.OwnerUserId, token);
        ProtocolExecutionContext Execution() => new(Enum.Parse<ProtocolKind>(user.BackendType, true), user.BackendInstanceId,
            user.BackendPrincipalId, new(user.Id, user.BackendType, user.BackendInstanceId, user.BackendPrincipalId, user.DisplayName, user.IsAdmin),
            execution.Claim.JobId.ToString("N"), clock.UtcNow.AddMinutes(10), token);
        if (taste != null) await taste.RememberPlaybackAsync(user.Id, token);
        var context = Execution();
        var access = await libraryAccess.ResolveAsync(context, token);
        if (!access.Succeeded)
            return DurableJobCompletion.Defer("history_resolution_library_unavailable", "Waiting for the listener's library access.", useBackoff: true);
        using var traffic = new HistoryRequestBudget();
        var started = (timeProvider ?? TimeProvider.System).GetTimestamp();
        var reasons = new Dictionary<string, int>();
        // The newest outcomes and the song being looked up, so Activity can show matches as they happen.
        var recent = new List<object>();
        string? checking = null;
        var matchedRecordings = new HashSet<Guid>();
        var state = await LoadAsync(db, context, access, execution.Claim.JobId, saved.Scope, token);
        var tracks = state.Library;
        var local = state.Metadata;
        var stored = state.Targets;
        var spotify = state.Spotify;
        var tracksByRecording = tracks.Where(track => track.CanonicalRecordingId != null)
            .GroupBy(track => track.CanonicalRecordingId!.Value).ToDictionary(group => group.Key, group => group.ToArray());
        var groups = state.Groups.Where(group => !state.Completed.Contains(group.Key)).ToArray();
        var total = saved.Cursor + groups.Length;
        var scope = new TrackMatchScope(user.Id, user.BackendInstanceId, null, 2, 1, access.LibraryIds.ToHashSet());
        var remaining = new List<IGrouping<string, ListeningEventRecord>>();
        for (var offset = 0; offset < groups.Length; offset += LocalBatchSize)
        {
            var batch = new List<Resolution>();
            foreach (var group in groups.Skip(offset).Take(LocalBatchSize))
            {
                token.ThrowIfCancellationRequested();
                var play = group.First();
                var source = Source(group.Key, play);
                var exact = play.ProviderTrackReference is { } reference ? spotify.GetValueOrDefault(reference) : null;
                var candidates = exact ?? local.GetValueOrDefault(MetadataKey(play.Title, play.Artist)) ?? [];
                if (exact is { Length: > 0 }) source = source with { CanonicalRecordingId = exact[0].CanonicalRecordingId };
                if (candidates.Length == 0) { remaining.Add(group); continue; }
                var decision = matcher.Decide(scope, source, candidates, null);
                var selected = candidates.FirstOrDefault(track => track.LibraryTrackId == decision.SelectedLibraryTrackId);
                if (selected == null || !Accepted(decision) || !SameEdition(play.Title, selected.Title)) { remaining.Add(group); continue; }
                var target = stored[selected.LibraryTrackId];
                var canonical = selected.CanonicalRecordingId ?? target.Library?.CanonicalRecordingId ?? await db.ListeningEvents.AsNoTracking().Where(item =>
                    item.OwnerUserId == user.Id && item.Protocol == user.BackendType && item.BackendInstanceId == user.BackendInstanceId &&
                    item.LibraryTrackId == selected.LibraryTrackId && item.CanonicalRecordingId != null).Select(item => item.CanonicalRecordingId).FirstOrDefaultAsync(token)
                    ?? (await identities.CreateRecordingAsync(context.RequireActor(), context.CorrelationId,
                        selected.Isrc, selected.MusicBrainzRecordingId, token)).Recording.Id;
                if (target.Library != null) target.Library.CanonicalRecordingId = canonical;
                batch.Add(new(group, canonical, target.Library, target.Song, target.Identity,
                    Reason: exact != null ? "matched-stored-id" : "matched-stored-metadata"));
            }
            if (batch.Count > 0) await SaveAsync(batch);
            if (batch.Count > 0 && offset + LocalBatchSize < groups.Length)
                await Task.Delay(TimeSpan.FromMilliseconds(250), timeProvider ?? TimeProvider.System, token);
        }

        var attempted = 0;
        var needsRetry = false;
        TimeSpan? retryDelay = null;
        ProviderError? unavailable = null;
        var resolved = new Dictionary<string, Resolution>();
        var providerFailures = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < remaining.Count; index++)
        {
            var group = remaining[index];
            token.ThrowIfCancellationRequested();
            if ((timeProvider ?? TimeProvider.System).GetElapsedTime(started) > TimeSpan.FromHours(1)) break;
            if (attempted > 0 && attempted % 100 == 0 &&
                (await db.ListeningEvents.CountAsync(item => item.OwnerUserId == user.Id && item.Protocol == user.BackendType &&
                    item.BackendInstanceId == user.BackendInstanceId, token) != state.Events ||
                 await LibraryTrackAccess.Query(db, context, access).CountAsync(token) != state.Tracks)) break;
            attempted++;
            context = Execution();
            var play = group.First();
            var key = MetadataKey(play.Title, play.Artist);
            var previous = resolved.GetValueOrDefault(key);
            var resolution = previous?.Song is { } song && Accepted(matcher.Decide(scope, Source(group.Key, play),
                [TrackMatchPlayableSearch.ToCandidate(song, scope)], null)) && SameEdition(play.Title, song.Title)
                ? previous with { Group = group, Reason = "matched-stored-metadata" }
                : await ResolveProviderAsync(group);
            if (resolution.Canonical != null) resolved[key] = resolution;
            needsRetry |= resolution.Retry;
            checking = index + 1 < remaining.Count ? $"{Clip(remaining[index + 1].First().Title)} · {Clip(remaining[index + 1].First().Artist)}" : null;
            await SaveAsync([resolution]);
        }
        checking = null;
        if (tags != null && matchedRecordings.Count > 0) await tags.EnqueuePendingAsync(token, matchedRecordings);
        if (matchedRecordings.Count > 0) await mixes.RefreshAfterHistoryResolutionAsync(user.Id, execution.Claim.JobId, jobs, token);
        var summary = JsonSerializer.Serialize(reasons);
        logger?.LogInformation("History resolution {JobId}: reasons {Reasons}; matched by tier {MatchedByTier}; HTTP calls {HttpCalls}",
            execution.Claim.JobId, summary, JsonSerializer.Serialize(reasons.Where(item => item.Key.StartsWith("matched-", StringComparison.Ordinal)).ToDictionary()), traffic.Calls);
        await ReportProgressAsync();
        if (remaining.Count > attempted || needsRetry)
        {
            if (saved.Cursor > initialCursor && !needsRetry)
                return DurableJobCompletion.Continue(TimeSpan.FromMinutes(1));
            return DurableJobCompletion.Defer("history_resolution_blocked", "Waiting for music sources to resolve more past songs.",
                retryDelay, useBackoff: true);
        }
        retained = null;
        return DurableJobCompletion.Success();

        async Task<Resolution> ResolveProviderAsync(IGrouping<string, ListeningEventRecord> group)
        {
            var play = group.First();
            // A play saved without a title or artist can't be searched for, so it is settled instead of holding the job.
            if (string.IsNullOrWhiteSpace(play.Title) || string.IsNullOrWhiteSpace(play.Artist))
                return new(group, null, null, Reason: "missing-metadata");
            try
            {
                if (unavailable != null) return new(group, null, null, Retry: true, Reason: "provider-failure:" + unavailable.Kind);
                var outcome = await matches.MatchImportedHistoryAsync(context, Source(group.Key, play), scope, token, providerFailures);
                if (!outcome.IsSuccess)
                {
                    if (outcome.Error?.RetryAfter > retryDelay.GetValueOrDefault()) retryDelay = outcome.Error.RetryAfter;
                    if (outcome.Error?.Kind == ProviderErrorKind.CapabilityUnavailable) unavailable = outcome.Error;
                    return new(group, null, null, Retry: true, Reason: "provider-failure:" + outcome.Error?.Kind);
                }
                var match = outcome.RequireValue();
                if (match.SearchFailure?.RetryAfter > retryDelay.GetValueOrDefault()) retryDelay = match.SearchFailure.RetryAfter;
                var selected = match.SelectedExternal;
                if (!Accepted(match.Decision) || selected == null)
                    return new(group, null, null, Retry: match.SearchFailure != null,
                        Reason: match.SearchFailure != null ? "provider-failure:" + match.SearchFailure.Kind :
                            match.ExternalCandidates.Count == 0 ? "no-candidates" : "candidates-rejected:" + (match.Decision.Warnings.FirstOrDefault() ?? match.Decision.Reasons.FirstOrDefault() ?? match.Decision.State.ToString()));
                if (!SameEdition(play.Title, selected.Title) || FuzzyMatcher.HasCleanRecordingLabel(play.Title) && selected.ExplicitContentLyrics == 1)
                    return new(group, null, null, Reason: "edition-mismatch");
                var reference = $"ext-{selected.ExternalProvider}-song-{selected.ExternalId}";
                await providers.RememberTrackAsync(context, reference).WaitAsync(TimeSpan.FromSeconds(10), token);
                var song = await providers.GetSongAsync(context, selected.ExternalProvider!, selected.ExternalId!)
                    .WaitAsync(TimeSpan.FromSeconds(10), token);
                if (song == null) return new(group, null, null, Retry: true, Reason: "song-read-failed");
                if (!SameEdition(play.Title, song.Title) || FuzzyMatcher.HasCleanRecordingLabel(play.Title) && song.ExplicitContentLyrics == 1)
                    return new(group, null, null, Reason: "edition-mismatch");
                var verified = matcher.Decide(scope, Source(group.Key, play), [TrackMatchPlayableSearch.ToCandidate(song, scope)], null);
                if (!Accepted(verified)) return new(group, null, null, Reason: "song-read-rejected:" + (verified.Warnings.FirstOrDefault() ?? verified.Reasons.FirstOrDefault() ?? verified.State.ToString()));
                var providerId = allstarr.Core.Providers.AppleMusicKit.AppleMusicClient.NormalizeProviderId(selected.ExternalProvider!);
                var externalHash = CanonicalCatalogKeys.Hash(selected.ExternalId!);
                var links = await db.ProviderTrackIdentities.AsNoTracking().Where(item =>
                    item.ProviderId == providerId && item.ExternalIdHash == externalHash && item.ExternalId == selected.ExternalId &&
                    (item.Scope == ProviderIdentityScope.Catalog || db.ProviderAccounts.Any(account =>
                        account.Id == item.ProviderAccountId && (account.OwnerUserId == user.Id || account.OwnerUserId == null) && account.Enabled)))
                    .OrderBy(item => item.Id).ToArrayAsync(token);
                var eligible = links.Where(CatalogIdentityPolicy.IsPlaybackEligible).ToArray();
                // A link retired for a better route still proves which recording this is, and another route plays it.
                var retired = links.Where(item => item.VerificationMethod == CatalogIdentityPolicy.SupersededVerificationMethod)
                    .Select(item => item.CanonicalRecordingId).Distinct().ToArray();
                if (eligible.Length == 0 && retired.Length == 1)
                    eligible = CatalogIdentityPolicy.OrderForPlayback((await db.ProviderTrackIdentities.AsNoTracking().Where(item =>
                        item.CanonicalRecordingId == retired[0] && item.ResourceKind == ProviderResourceKind.Track &&
                        (item.Scope == ProviderIdentityScope.Catalog || db.ProviderAccounts.Any(account =>
                            account.Id == item.ProviderAccountId && (account.OwnerUserId == user.Id || account.OwnerUserId == null) && account.Enabled)))
                        .ToArrayAsync(token)).Where(CatalogIdentityPolicy.IsPlaybackEligible)).ToArray();
                if (eligible.Length == 0) return new(group, null, null, Retry: links.Length == 0,
                    Reason: links.Length == 0 ? "identity-missing" : "not-playback-eligible");
                if (song.CanonicalRecordingId is { } known) eligible = eligible.Where(item => item.CanonicalRecordingId == known).ToArray();
                if (eligible.Select(item => item.CanonicalRecordingId).Distinct().Count() != 1)
                    return new(group, null, null, Retry: true, Reason: "identity-conflict");
                var identity = eligible[0];
                var library = tracksByRecording.GetValueOrDefault(identity.CanonicalRecordingId)?.FirstOrDefault(track => SameEdition(play.Title, track.Title));
                return new(group, identity.CanonicalRecordingId, library, song, identity, Reason: "matched-search");
            }
            catch (Exception exception) when (!token.IsCancellationRequested)
            {
                logger?.LogWarning("History resolution failed at provider read ({ExceptionType})", exception.GetType().Name);
                // Bad saved values fail the same way on every attempt, so only other failures wait for a retry.
                return new(group, null, null, Retry: exception is not ArgumentException, Reason: "exception:" + exception.GetType().Name);
            }
        }

        async Task SaveAsync(IReadOnlyCollection<Resolution> batch)
        {
            foreach (var item in batch)
            {
                reasons[item.Reason] = reasons.GetValueOrDefault(item.Reason) + 1;
                if (reasons[item.Reason] <= 3) logger?.LogDebug("History outcome {Reason}: {Title}", item.Reason, item.Group.First().Title);
                var heard = item.Group.First();
                recent.Insert(0, new
                {
                    title = Clip(heard.Title),
                    artist = Clip(heard.Artist),
                    plays = item.Group.Count(),
                    found = Clip(item.Library?.Title ?? item.Song?.Title),
                    foundArtist = Clip(item.Library?.Artist ?? item.Song?.Artist),
                    reason = item.Reason
                });
                if (recent.Count > 6) recent.RemoveAt(6);
            }
            var next = ProgressPayload(saved.Cursor + batch.Count(item => !item.Retry));
            var json = payloadPolicy.SerializeAndValidate(next);
            var checkpoint = batch.Any(item => !item.Retry);
            for (var retry = 0; checkpoint; retry++)
            {
                try
                {
                    await using var write = await factory.CreateDbContextAsync(token);
                    await using var transaction = await write.Database.BeginTransactionAsync(token);
                    if (await write.Jobs.Where(item => item.Id == execution.Claim.JobId && item.State == DurableJobState.Running &&
                        item.AttemptCount == execution.Claim.AttemptNumber && item.CancellationRequestedAt == null)
                        .ExecuteUpdateAsync(update => update.SetProperty(item => item.PayloadJson, json)
                            .SetProperty(item => item.DeferralCount, 0)
                            .SetProperty(item => item.UpdatedAt, clock.UtcNow)
                            .SetProperty(item => item.Revision, item => item.Revision + batch.Count), token) != 1)
                        throw new DurableJobExecutionChangedException();
                    foreach (var resolution in batch)
                    {
                        var group = resolution.Group;
                        var canonical = resolution.Canonical;
                        var library = resolution.Library;
                        var song = resolution.Song;
                        var identity = resolution.Identity;
                        if (canonical != null)
                        {
                            var libraryId = library?.Id;
                            var reference = library?.BackendItemId ?? $"ext-{song!.ExternalProvider}-song-{song.ExternalId}";
                            var isrc = library?.Isrc ?? song?.Isrc;
                            foreach (var importGroup in group.GroupBy(play => play.SourceKind == "import" ? play.ImportProvenance?.Split(':').ElementAtOrDefault(1) : null))
                            {
                                var count = 0;
                                foreach (var ids in importGroup.Select(play => play.Id).Chunk(500))
                                {
                                    var events = Unresolved(write, saved.Scope).Where(item => ids.Contains(item.Id));
                                    count += await events.ExecuteUpdateAsync(update => update
                                        .SetProperty(item => item.CanonicalRecordingId, canonical)
                                        .SetProperty(item => item.LibraryTrackId, libraryId)
                                        .SetProperty(item => item.TrackReference, reference)
                                        .SetProperty(item => item.Isrc, item => item.Isrc ?? isrc)
                                        .SetProperty(item => item.Revision, item => item.Revision + 1)
                                        .SetProperty(item => item.ProviderTrackIdentityId, item => identity != null ? identity.Id : item.ProviderTrackIdentityId)
                                        .SetProperty(item => item.ProviderId, item => identity != null && item.ProviderTrackReference == null ? identity.ProviderId : item.ProviderId)
                                        .SetProperty(item => item.ProviderAccountId, item => identity != null && item.ProviderTrackReference == null ? identity.ProviderAccountId : item.ProviderAccountId)
                                        .SetProperty(item => item.ProviderTrackReference, item => identity != null && item.ProviderTrackReference == null ? identity.ExternalId : item.ProviderTrackReference), token);
                                }
                                if (!Guid.TryParse(importGroup.Key, out var importId)) continue;
                                await write.ListeningHistoryImports.Where(item => item.Id == importId &&
                                    item.OwnerUserId == user.Id && item.Protocol == user.BackendType && item.BackendInstanceId == user.BackendInstanceId)
                                    .ExecuteUpdateAsync(update => update
                                        .SetProperty(item => item.ResolvedRows, item => item.ResolvedRows + count)
                                        .SetProperty(item => item.UnresolvedRows, item => Math.Max(0, item.UnresolvedRows - count))
                                        .SetProperty(item => item.UpdatedAt, clock.UtcNow)
                                        .SetProperty(item => item.Revision, item => item.Revision + 1), token);
                            }
                        }
                        else if (!resolution.Retry)
                        {
                            var kind = ResourceKind;
                            var version = await write.ExternalMetadataSnapshots.Where(item => item.OwnerUserId == user.Id &&
                                item.Protocol == user.BackendType && item.BackendInstanceId == user.BackendInstanceId &&
                                item.ProviderId == "allstarr" && item.ResourceKind == kind && item.ExternalIdHash == group.Key)
                                .Select(item => (int?)item.SnapshotVersion).MaxAsync(token) ?? 0;
                            write.ExternalMetadataSnapshots.Add(new()
                            {
                                Id = Guid.CreateVersion7(),
                                OwnerUserId = user.Id,
                                Protocol = user.BackendType,
                                BackendInstanceId = user.BackendInstanceId,
                                BackendPrincipalId = user.BackendPrincipalId,
                                ProviderId = "allstarr",
                                ResourceKind = kind,
                                ExternalIdHash = group.Key,
                                SnapshotVersion = version + 1,
                                SourceJobId = execution.Claim.JobId,
                                RetrievedAt = clock.UtcNow,
                                PayloadJson = ReceiptPayload,
                                PayloadSha256 = ListeningHistoryImportService.Hash(ReceiptPayload),
                                CorrelationId = context.CorrelationId
                            });
                        }
                    }
                    await write.SaveChangesAsync(token);
                    await transaction.CommitAsync(token);
                    saved = next;
                    break;
                }
                catch (Exception exception) when (retry < 2 && DbErrors.IsTransientConflict(exception))
                {
                }
            }
            foreach (var item in batch)
            {
                if (item.Canonical is { } id) matchedRecordings.Add(id);
                if (!item.Retry) state.Completed.Add(item.Group.Key);
            }
            if (checkpoint || reasons.Values.Sum() % 100 == 0)
                await ReportProgressAsync();
        }

        ListeningHistoryResolutionPayload ProgressPayload(int cursor)
        {
            var elapsed = (timeProvider ?? TimeProvider.System).GetElapsedTime(started).TotalSeconds;
            var rate = elapsed > 0 ? (cursor - initialCursor) / elapsed : 0;
            return saved with
            {
                Cursor = cursor,
                Total = total,
                Summary = JsonSerializer.Serialize(reasons),
                ThroughputPerSecond = rate is > 0 and < 10000 ? rate : null
            };
        }

        async Task ReportProgressAsync()
        {
            var next = ProgressPayload(saved.Cursor);
            if (next != saved)
            {
                var json = payloadPolicy.SerializeAndValidate(next);
                await using var write = await factory.CreateDbContextAsync(token);
                if (await write.Jobs.Where(item => item.Id == execution.Claim.JobId && item.State == DurableJobState.Running &&
                    item.AttemptCount == execution.Claim.AttemptNumber && item.CancellationRequestedAt == null)
                    .ExecuteUpdateAsync(update => update.SetProperty(item => item.PayloadJson, json)
                        .SetProperty(item => item.UpdatedAt, clock.UtcNow)
                        .SetProperty(item => item.Revision, item => item.Revision + 1), token) != 1)
                    throw new DurableJobExecutionChangedException();
                saved = next;
            }
            await execution.ReportProgressAsync(new("history-resolution", JsonSerializer.Serialize(new { reasons, recent }), saved.Cursor, total,
                Track: checking, ThroughputPerSecond: saved.ThroughputPerSecond), token);
        }
    }

    private static string? Clip(string? value) => value is { Length: > 120 } ? value[..120] : value;

    private static ExternalTrackMatchSnapshot Source(string key, ListeningEventRecord play) => new(key,
        "history", key, play.Title ?? "", play.Artist ?? "", play.Album, play.AlbumArtist,
        play.DurationMilliseconds, play.Isrc, play.RecordingMusicBrainzId, FuzzyMatcher.HasCleanRecordingLabel(play.Title ?? "") ? false : null);

    private static bool Accepted(TrackMatchDecision decision) =>
        decision.State == TrackMatchReviewState.Accepted && !decision.RequiresReview;

    private static bool SameEdition(string? source, string? target) =>
        FuzzyMatcher.SemanticVersionTags(source).SetEquals(FuzzyMatcher.SemanticVersionTags(target));
}

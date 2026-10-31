using System.Text.Json;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Capabilities;
using allstarr.Services.MusicBrainz;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Enrichment;

public sealed record SharedTagPayload(Guid RecordingId, string? ArtistKey = null, string? ArtistName = null,
    string? Source = null, IReadOnlyList<string>? Labels = null);

public sealed class SharedTagQueue(IDbContextFactory<AllstarrDbContext> factory, DurableJobQueue jobs, IPlatformClock clock)
{
    public const string JobType = "recording.tags.refresh";
    public static readonly TimeSpan RefreshAge = TimeSpan.FromDays(30);

    public Task<DurableJobEnqueueResult> CaptureArtistAsync(Guid owner, Artist artist, string provider, CancellationToken token)
    {
        var artistKey = SharedTagMetadata.ArtistIdentity(provider, artist.Id);
        if (artistKey.Length == 0) return Task.FromResult(new DurableJobEnqueueResult(Guid.Empty, false));
        var payload = new SharedTagPayload(Guid.Empty, artistKey, artist.Name, $"{provider}-artist", artist.Genres.Take(100).ToArray());
        var key = $"artist:{Matching.CanonicalCatalogKeys.Hash(JsonSerializer.Serialize(payload))}:{clock.UtcNow.ToUnixTimeSeconds() / 2592000}";
        return jobs.EnqueueAsync(new DurableJobEnqueueRequest<SharedTagPayload>(JobType, key, payload, owner, Priority: -100), token);
    }

    internal async Task RefreshListenersAsync(Guid recording, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var owners = await db.Users.AsNoTracking().Where(user => user.Enabled && !user.IsSynthetic &&
            db.ListeningEvents.Any(play => play.OwnerUserId == user.Id && play.Protocol == user.BackendType &&
                play.BackendInstanceId == user.BackendInstanceId && play.CanonicalRecordingId == recording) &&
            db.ExternalMetadataSnapshots.Any(item => item.OwnerUserId == user.Id && item.Protocol == user.BackendType &&
                item.BackendInstanceId == user.BackendInstanceId && item.ResourceKind == "personal-mixes"))
            .Select(user => user.Id).Take(100).ToArrayAsync(token);
        var batch = clock.UtcNow.ToUnixTimeSeconds() / 300;
        foreach (var owner in owners)
            await jobs.EnqueueAsync(new DurableJobEnqueueRequest<Guid>(Music.PersonalMixService.JobType,
                $"history-tags:{owner:N}:{batch}", Guid.Empty, owner,
                Priority: -100, AvailableAt: DateTimeOffset.FromUnixTimeSeconds((batch + 1) * 300)), token);
    }

    public async Task<int> EnqueuePendingAsync(CancellationToken token, IReadOnlyCollection<Guid>? onlyRecordings = null)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var owner = await db.Users.Where(item => item.Enabled).OrderBy(item => item.Id).Select(item => (Guid?)item.Id).FirstOrDefaultAsync(token);
        if (owner == null) return 0;
        var active = (await db.Jobs.AsNoTracking().Where(item => item.Type == JobType &&
            (item.State == DurableJobState.Pending || item.State == DurableJobState.Running || item.State == DurableJobState.RetryScheduled))
            .Select(item => item.IdempotencyKey).ToArrayAsync(token)).Select(key => key.Split(':')[0]).ToHashSet();
        var queued = 0;
        var pending = db.CanonicalRecordings.AsNoTracking().Where(item => item.MergedIntoId == null);
        if (onlyRecordings != null) pending = pending.Where(item => onlyRecordings.Contains(item.Id));
        for (var offset = 0; ; offset += 100)
        {
            var recordings = await pending
                .OrderBy(item => item.Id).Skip(offset).Take(100).Select(item => new { item.Id, item.UpdatedAt }).ToArrayAsync(token);
            var ids = recordings.Select(item => item.Id).ToArray();
            if (ids.Length == 0) return queued;
            var historical = await db.ListeningEvents.AsNoTracking().Where(item => item.CanonicalRecordingId != null &&
                ids.Contains(item.CanonicalRecordingId.Value) && (item.SourceKind == "import" || item.SourceKind == "protocol" || item.SourceKind == "listenbrainz-api"))
                .Select(item => item.CanonicalRecordingId!.Value).Distinct().ToHashSetAsync(token);
            var keys = ids.Select(SharedTagStore.RecordingKey).ToArray();
            var profiles = (await db.SharedTagProfiles.AsNoTracking().Where(item => keys.Contains(item.Key)).ToArrayAsync(token))
                .ToDictionary(item => item.Key);
            var snapshots = await (from snapshot in db.ExternalMetadataSnapshots.AsNoTracking()
                                   join identity in db.ProviderTrackIdentities on snapshot.ProviderTrackIdentityId equals identity.Id
                                   where ids.Contains(identity.CanonicalRecordingId) && identity.Verification == ProviderIdentityVerification.Verified &&
                                       identity.VerificationMethod != "automatic-suggestion" && identity.VerificationMethod != "manual-released" &&
                                       identity.VerificationMethod != "manual-replaced" && identity.VerificationMethod != "superseded" &&
                                       (snapshot.ResourceKind == "track" || snapshot.ResourceKind == "playback-metadata")
                                   group snapshot by identity.CanonicalRecordingId into grouped
                                   select new { Id = grouped.Key, At = grouped.Max(item => item.RetrievedAt) }).ToArrayAsync(token);
            var changes = snapshots.Concat(await db.LibraryTracks.AsNoTracking().Where(item => item.CanonicalRecordingId != null &&
                ids.Contains(item.CanonicalRecordingId.Value)).Select(item => new { Id = item.CanonicalRecordingId!.Value, At = item.UpdatedAt }).ToArrayAsync(token))
                .Concat(await db.Set<RecordingKeptRouteEntity>().AsNoTracking().Where(item => ids.Contains(item.CanonicalRecordingId))
                    .Select(item => new { Id = item.CanonicalRecordingId, At = item.UpdatedAt }).ToArrayAsync(token)).ToArray();
            foreach (var recording in recordings)
            {
                var id = recording.Id;
                if (active.Contains(id.ToString("D"))) continue;
                var profile = profiles.GetValueOrDefault(SharedTagStore.RecordingKey(id));
                var document = SharedTagStore.Read(profile);
                var needsHistory = historical.Contains(id) && !document.HistoryCaptured && !document.Evidence.Any(item => item.CanonicalTag != null);
                var latest = changes.Where(item => item.Id == id).Select(item => item.At).Append(recording.UpdatedAt).Max();
                if (profile != null && !needsHistory && profile.CapturedAt >= latest &&
                    (profile.RefreshedAt > clock.UtcNow - RefreshAge || document.Evidence.Any(item => item.CanonicalTag != null))) continue;
                var key = $"{id:D}:{profile?.RefreshedAt.UtcTicks ?? 0}:{latest.UtcTicks}:{clock.UtcNow.ToUnixTimeSeconds() / 2592000}{(needsHistory ? ":history" : "")}";
                if ((await jobs.EnqueueAsync(new DurableJobEnqueueRequest<SharedTagPayload>(JobType, key, new(id),
                        owner, Priority: -100, MaxAttempts: 3), token)).Created && ++queued >= 100) return queued;
            }
            if (ids.Length < 100) return queued;
        }
    }
}

public sealed class SharedTagJobHandler(IDbContextFactory<AllstarrDbContext> factory, SharedTagStore store,
    SharedTagSources sources, IPlatformClock clock, SharedTagQueue? queue = null) : IDurableJobHandler
{
    private readonly SemaphoreSlim collection = new(1, 1);
    public string JobType => SharedTagQueue.JobType;

    public async Task<DurableJobCompletion> ExecuteAsync(DurableJobExecutionContext execution, CancellationToken token)
    {
        SharedTagPayload? payload;
        try { payload = execution.Claim.Payload.Deserialize<SharedTagPayload>(); }
        catch (JsonException) { payload = null; }
        if (payload == null || payload.RecordingId == Guid.Empty && payload.ArtistKey == null)
            return DurableJobCompletion.Failure("tags_request_invalid", "The saved tag request is invalid.");
        await collection.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            if (execution.Claim.OwnerUserId is not { } owner || !await db.Users.AnyAsync(item => item.Id == owner && item.Enabled, token))
                return DurableJobCompletion.Cancelled();
            if (payload.ArtistKey is { } capturedArtistKey && payload.Source is { } source && payload.Labels is { } labels)
            {
                var fetchedAt = await db.Jobs.Where(item => item.Id == execution.Claim.JobId).Select(item => item.CreatedAt).SingleAsync(token);
                await store.SaveAsync(SharedTagStore.ArtistKey(capturedArtistKey),
                    [new(source, labels.Select(label => new SharedTagInput(label, IsGenre: true)).ToArray(), fetchedAt)], [], false, token, [payload.ArtistName ?? ""]);
                return DurableJobCompletion.Success();
            }
            var recording = await db.CanonicalRecordings.AsNoTracking().Include(item => item.Identifiers)
                .SingleOrDefaultAsync(item => item.Id == payload.RecordingId, token);
            if (recording == null || recording.MergedIntoId != null) return DurableJobCompletion.Success();
            var key = SharedTagStore.RecordingKey(recording.Id);
            var profile = await db.SharedTagProfiles.AsNoTracking().SingleOrDefaultAsync(item => item.Key == key, token);
            var snapshots = await (from snapshot in db.ExternalMetadataSnapshots.AsNoTracking()
                                   join identity in db.ProviderTrackIdentities on snapshot.ProviderTrackIdentityId equals identity.Id
                                   where identity.CanonicalRecordingId == recording.Id && identity.Verification == ProviderIdentityVerification.Verified &&
                                       identity.VerificationMethod != "automatic-suggestion" && identity.VerificationMethod != "manual-released" &&
                                       identity.VerificationMethod != "manual-replaced" && identity.VerificationMethod != "superseded" &&
                                       (snapshot.ResourceKind == "track" || snapshot.ResourceKind == "playback-metadata")
                                   orderby snapshot.RetrievedAt descending
                                   select snapshot).Take(100).ToArrayAsync(token);
            var captured = new List<SharedTagMetadata>();
            foreach (var snapshot in snapshots)
            {
                try { captured.Add(SharedTagMetadata.Read(snapshot.ProviderId, snapshot.PayloadJson, snapshot.RetrievedAt)); }
                catch (JsonException) { }
            }
            var library = await db.LibraryTracks.AsNoTracking().FirstOrDefaultAsync(item => item.CanonicalRecordingId == recording.Id, token);
            var kept = await db.Set<RecordingKeptRouteEntity>().AsNoTracking().FirstOrDefaultAsync(item => item.CanonicalRecordingId == recording.Id, token);
            if (kept?.DisplayMetadataJson is { } metadata)
                captured.Add(SharedTagMetadata.Read("library", metadata, kept.CreatedAt));
            var history = await db.ListeningEvents.AsNoTracking().Where(item => item.CanonicalRecordingId == recording.Id &&
                item.Artist != null && item.Artist != "").Select(item => new { item.Artist, item.Title }).FirstOrDefaultAsync(token);
            var artist = captured.Select(item => item.Artist).FirstOrDefault(item => item.Length > 0) ?? library?.Artist ?? history?.Artist ?? "";
            var artistNames = captured.SelectMany(item => item.ArtistNames).Append(artist).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var title = captured.Select(item => item.Title).FirstOrDefault(item => item.Length > 0) ?? library?.Title ?? history?.Title ?? recording.Title;
            var artistKeys = captured.SelectMany(item => item.ArtistKeys).Distinct().ToList();
            var batches = captured.SelectMany(item => item.Batches).GroupBy(item => item.Source)
                .Select(group => group.FirstOrDefault(batch => batch.Tags.Count > 0) ?? group.First()).ToList();
            await store.SaveAsync(key, batches, artistKeys, false, token, artistNames);
            var mbid = recording.Identifiers.FirstOrDefault(item => item.Kind == RecordingIdentifierKinds.MusicBrainz)?.Value ?? library?.MusicBrainzRecordingId;
            var isrc = recording.Identifiers.FirstOrDefault(item => item.Kind == RecordingIdentifierKinds.Isrc)?.Value ??
                captured.Select(item => item.Isrc).FirstOrDefault(item => !string.IsNullOrWhiteSpace(item)) ?? library?.Isrc;
            var knownTags = (await store.GetTags(recording.Id, token)).Tags.Count > 0;
            var refreshed = !knownTags && (profile == null || profile.RefreshedAt <= clock.UtcNow - SharedTagQueue.RefreshAge ||
                history != null && !SharedTagStore.Read(profile).HistoryCaptured);
            if (refreshed && (mbid != null || isrc != null || artist.Length > 0 && title.Length > 0))
            {
                var collected = await sources.CollectRecordingAsync(mbid, isrc, artist, title, token);
                batches.AddRange(collected.Batches);
                var mbArtist = MusicBrainzService.NormalizeMbid(collected.MusicBrainzArtistId ?? library?.MusicBrainzArtistId);
                if (mbArtist != null) artistKeys.Add($"musicbrainz:{mbArtist}");
                var artistKey = mbArtist != null ? $"musicbrainz:{mbArtist}" : artistKeys.FirstOrDefault();
                if (artistKey != null)
                {
                    var artistProfile = await db.SharedTagProfiles.AsNoTracking().SingleOrDefaultAsync(item => item.Key == SharedTagStore.ArtistKey(artistKey), token);
                    if (artistProfile == null || artistProfile.RefreshedAt <= clock.UtcNow - SharedTagQueue.RefreshAge &&
                        !SharedTagStore.Read(artistProfile).Evidence.Any(item => item.CanonicalTag != null))
                        await store.SaveAsync(SharedTagStore.ArtistKey(artistKey),
                            await sources.CollectArtistAsync(mbArtist, artist, token), [], true, token, artistNames);
                }
            }
            await store.SaveAsync(key, batches, artistKeys, refreshed, token, artistNames,
                snapshots.Select(item => item.RetrievedAt).Append(recording.UpdatedAt)
                    .Append(library?.UpdatedAt ?? DateTimeOffset.MinValue).Append(kept?.UpdatedAt ?? DateTimeOffset.MinValue).Max(),
                historyCaptured: history != null && (refreshed || knownTags));
            if (queue != null) await queue.RefreshListenersAsync(recording.Id, token);
            await execution.ReportProgressAsync(new("recording-tags", "Styles collected for this song.", 1, 1), token);
            return DurableJobCompletion.Success();
        }
        catch (MusicBrainzLookupException error)
        {
            return error.Retryable ? DurableJobCompletion.Retry(error.Code, error.Message, error.RetryAfter)
                : DurableJobCompletion.Failure(error.Code, error.Message);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return DurableJobCompletion.Cancelled(); }
        catch (Exception error) when (error is HttpRequestException or JsonException or TaskCanceledException)
        {
            return DurableJobCompletion.Retry("tags_unavailable", "Shared tag collection will retry.");
        }
        finally { collection.Release(); }
    }
}

public sealed class SharedTagDiscoveryService(SharedTagQueue queue, DurableStorageState storage,
    ILogger<SharedTagDiscoveryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (storage.GetSnapshot().Readiness == DurableStorageReadiness.Ready)
                    await queue.EnqueuePendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error) { logger.LogWarning("Shared tag discovery failed ({ErrorType})", error.GetType().Name); }
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }
}

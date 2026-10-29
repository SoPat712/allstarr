using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Favorites;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Routing;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Releases;

public sealed class NewReleasesService(IDbContextFactory<AllstarrDbContext> factory, IPlatformClock clock,
    IProviderRouter router, IDurableRuntimeSettings settings)
{
    public const string JobType = "releases.poll";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private const string FollowsKind = "release-follows";
    private const string CatalogKind = "release-catalog";
    private const string RateKind = "release-rate";
    private const string DismissKind = "release-dismissal";

    public async Task<IReadOnlyList<FollowedReleaseArtist>> ListAsync(Guid owner, CancellationToken token = default)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var user = await UserAsync(db, owner, token);
        return await FollowsAsync(db, user, token);
    }

    public async Task FollowAsync(Guid owner, FollowedReleaseArtist artist, CancellationToken token = default)
    {
        var id = new ProviderExternalResourceId(artist.ProviderId, ProviderResourceKind.Artist, artist.ArtistId);
        if (string.IsNullOrWhiteSpace(artist.DisplayName) || artist.DisplayName.Length > 300 || artist.DisplayName.Any(char.IsControl))
            throw new ArgumentException("An artist display name is required.");
        await Gate.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var user = await UserAsync(db, owner, token);
            var artists = (await FollowsAsync(db, user, token)).ToList();
            var followed = new FollowedReleaseArtist(id.ProviderId, id.Value, artist.DisplayName.Trim(),
                await KnownArtistMbidAsync(db, user, id, token));
            artists.RemoveAll(item => item.ProviderId == id.ProviderId && item.ArtistId == id.Value);
            artists.Add(followed);
            await SaveAsync(db, user, FollowsKind, Scope(user), artists.ToArray(), token);
            await EnsureScheduleAsync(db, user, token);
            await db.SaveChangesAsync(token);
        }
        finally { Gate.Release(); }
    }

    public async Task UnfollowAsync(Guid owner, string provider, string artistId, CancellationToken token = default)
    {
        await Gate.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var user = await UserAsync(db, owner, token);
            var artists = (await FollowsAsync(db, user, token)).Where(item => item.ProviderId != provider || item.ArtistId != artistId).ToArray();
            await SaveAsync(db, user, FollowsKind, Scope(user), artists, token);
            if (artists.Length == 0)
            {
                var schedule = await db.JobSchedules.SingleOrDefaultAsync(item => item.Id == ScheduleId(user), token);
                if (schedule != null) { schedule.Enabled = false; schedule.Revision++; schedule.UpdatedAt = clock.UtcNow; }
            }
            await db.SaveChangesAsync(token);
        }
        finally { Gate.Release(); }
    }

    public async Task<int> FollowHeartedArtistsAsync(Guid owner, CancellationToken token = default)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var user = await UserAsync(db, owner, token);
        var rows = await (from state in db.FavoriteStates.AsNoTracking()
                          join accepted in db.FavoriteEvents on state.LastEventId equals accepted.Id
                          where state.OwnerUserId == owner && state.IsFavorite && state.Protocol == user.BackendType &&
                              state.BackendInstanceId == user.BackendInstanceId && accepted.OwnerUserId == owner &&
                              accepted.Protocol == state.Protocol && accepted.BackendInstanceId == state.BackendInstanceId &&
                              accepted.BackendPrincipalId == user.BackendPrincipalId && accepted.ItemId == state.ItemId &&
                              accepted.Operation == FavoriteOperation.Favorite
                          orderby accepted.CreatedAt descending, state.Id
                          select new { state.ItemId, state.DisplayMetadataJson }).ToArrayAsync(token);
        var artists = rows.Select(row => FavoriteDisplaySnapshot.Read(row.ItemId, row.DisplayMetadataJson))
            .OfType<FavoriteDisplaySnapshot>().Where(item => item.Kind == "song")
            .SelectMany(song => song.ArtistIds.Select((id, index) => (Id: FavoriteDisplaySnapshot.Resource(id),
                Name: song.Artists.ElementAtOrDefault(index))))
            .Where(item => item.Id?.ResourceKind == ProviderResourceKind.Artist && !string.IsNullOrWhiteSpace(item.Name))
            .DistinctBy(item => (item.Id!.ProviderId, item.Id.Value)).Take(200).ToArray();
        await Gate.WaitAsync(token);
        try
        {
            var follows = (await FollowsAsync(db, user, token)).ToList();
            var added = 0;
            foreach (var artist in artists)
            {
                var id = artist.Id!;
                if (follows.Any(item => item.ProviderId == id.ProviderId && item.ArtistId == id.Value)) continue;
                follows.Add(new(id.ProviderId, id.Value, artist.Name!, await KnownArtistMbidAsync(db, user, id, token)));
                added++;
            }
            if (added > 0)
            {
                await SaveAsync(db, user, FollowsKind, Scope(user), follows.ToArray(), token);
                await EnsureScheduleAsync(db, user, token);
                await db.SaveChangesAsync(token);
            }
            return added;
        }
        finally { Gate.Release(); }
    }

    public async Task<ReleaseInbox> ReadAsync(Guid owner, CancellationToken token = default)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var user = await UserAsync(db, owner, token);
        var sources = new List<ReleaseSource>();
        var coverage = new List<ReleaseCoverage>();
        foreach (var artist in await FollowsAsync(db, user, token))
        {
            var route = await RouteAsync(user, artist, token);
            var cache = route == null ? null : await LatestAsync(db, CatalogKind, CatalogKey(artist, route.Context), token);
            var catalog = Read<ReleaseCatalog>(cache);
            if (catalog != null) sources.AddRange(catalog.Releases);
            coverage.Add(new(artist, catalog == null || catalog.Status == "retry" || catalog.NextCheckAt <= clock.UtcNow && catalog.Cursor == null
                ? "unknown" : catalog.Status, catalog?.CheckedAt));
        }
        var dismissed = await db.ExternalMetadataSnapshots.AsNoTracking().Where(item => item.OwnerUserId == owner &&
            item.ResourceKind == DismissKind && item.Protocol == user.BackendType && item.BackendInstanceId == user.BackendInstanceId &&
            item.BackendPrincipalId == user.BackendPrincipalId).Select(item => item.PayloadJson).ToArrayAsync(token);
        var dismissedSources = dismissed.SelectMany(json => JsonSerializer.Deserialize<ReleaseSource[]>(json) ?? []).ToArray();
        var cleanOnly = (await settings.GetPreferencesAsync(owner, token)).Values.ExplicitFilter == "CleanOnly";
        var entries = ReleaseInboxProjection.Group(sources, clock.UtcNow)
            .Where(item => (!cleanOnly || item.IsExplicit == false) &&
                !item.Sources.Any(source => dismissedSources.Any(dismissed => ReleaseInboxProjection.Dismissed(source, dismissed)))).ToArray();
        return new(entries.Where(item => item.ExclusionReason == null).ToArray(),
            entries.Where(item => item.ExclusionReason != null).ToArray(), coverage);
    }

    public async Task<bool> DismissAsync(Guid owner, string entryId, CancellationToken token = default)
    {
        var inbox = await ReadAsync(owner, token);
        var entry = inbox.Entries.Concat(inbox.Excluded).SingleOrDefault(item => item.Id == entryId);
        if (entry == null) return false;
        await Gate.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var user = await UserAsync(db, owner, token);
            await SaveAsync(db, user, DismissKind, Hash(Scope(user) + entryId), entry.Sources.ToArray(), token);
            await db.SaveChangesAsync(token);
            return true;
        }
        finally { Gate.Release(); }
    }

    public async Task<DurableJobCompletion> PollAsync(Guid owner, Guid jobId, CancellationToken token)
    {
        await Gate.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var user = await UserAsync(db, owner, token);
            TimeSpan? pending = null;
            void WaitFor(TimeSpan delay) => pending = pending == null || delay < pending.Value ? delay : pending;
            foreach (var artist in await FollowsAsync(db, user, token))
            {
                var route = await RouteAsync(user, artist, token, jobId);
                if (route == null) continue;
                var context = route.Context;
                var key = CatalogKey(artist, context);
                var catalog = Read<ReleaseCatalog>(await LatestAsync(db, CatalogKind, key, token));
                if (catalog != null && catalog.NextCheckAt > clock.UtcNow)
                {
                    if (catalog.Status == "retry") WaitFor(catalog.NextCheckAt - clock.UtcNow);
                    continue;
                }
                var rateKey = Hash(context.ProviderId + ":" + context.Account?.AccountId);
                var rate = Read<ReleaseRate>(await LatestAsync(db, RateKind, rateKey, token));
                if (rate?.NextRequestAt > clock.UtcNow)
                {
                    WaitFor(rate.NextRequestAt - clock.UtcNow);
                    continue;
                }
                await SaveAsync(db, user, RateKind, rateKey, new ReleaseRate(clock.UtcNow.AddSeconds(2)), token);
                await db.SaveChangesAsync(token);
                var cursor = catalog?.Cursor;
                var outcome = await route.Implementation.GetArtistAlbumsAsync(context,
                    new(new(context.ProviderId, ProviderResourceKind.Artist, artist.ArtistId), new(200, cursor)));
                var now = clock.UtcNow;
                if (!outcome.IsSuccess)
                {
                    var error = outcome.Error!;
                    if (error.Kind == ProviderErrorKind.Canceled) token.ThrowIfCancellationRequested();
                    var retry = error.Kind is ProviderErrorKind.RateLimited or ProviderErrorKind.TransientFailure or ProviderErrorKind.Canceled;
                    var delay = error.RetryAfter ?? TimeSpan.FromMinutes(15);
                    if (delay < TimeSpan.FromSeconds(2)) delay = TimeSpan.FromSeconds(2);
                    await SaveAsync(db, user, CatalogKind, key, new ReleaseCatalog(retry ? "retry" : "unknown", now,
                        retry ? now + delay : NextDay(now), catalog?.Releases ?? [], cursor, catalog?.SeenCursors), token);
                    if (retry) await SaveAsync(db, user, RateKind, rateKey, new ReleaseRate(now + delay), token);
                    await db.SaveChangesAsync(token);
                    if (retry) WaitFor(delay);
                    continue;
                }
                var page = outcome.RequireValue();
                var gathered = (cursor == null ? [] : catalog?.Releases ?? []).Concat(page.Items
                    .Where(album => album.Id.ProviderId == context.ProviderId)
                    .Select(album => ReleaseInboxProjection.From(album, artist))).DistinctBy(item => item.Key).ToArray();
                var releases = ReleaseInboxProjection.Group(gathered, now).SelectMany(item => item.Sources).ToArray();
                var seen = cursor == null ? [] : catalog?.SeenCursors?.ToList() ?? [];
                var next = page.NextCursor;
                var stopped = next != null && (seen.Contains(next, StringComparer.Ordinal) || seen.Count >= 19);
                if (next != null) seen.Add(next);
                await SaveAsync(db, user, CatalogKind, key, new ReleaseCatalog(next != null || stopped || page.IsPartial
                    ? "partial" : gathered.Length == 0 ? "unknown" : "known", now,
                    next == null || stopped ? NextDay(now) : now, releases, stopped ? null : next, seen.ToArray()), token);
                await db.SaveChangesAsync(token);
                if (next != null && !stopped) WaitFor(TimeSpan.FromSeconds(2));
            }
            return pending.HasValue ? Deferred(pending.Value) : DurableJobCompletion.Success();
        }
        finally { Gate.Release(); }
    }

    private async Task<ProviderRouteCandidate<IProviderMetadataCapability>?> RouteAsync(UserRecord user,
        FollowedReleaseArtist artist, CancellationToken token, Guid? jobId = null)
    {
        var principal = new ProviderBackendPrincipal(user.BackendType, user.BackendInstanceId, user.BackendPrincipalId);
        var actor = jobId.HasValue ? new ProviderActorContext(ProviderActorKind.SystemJob, user.Id, principal, jobId) :
            new ProviderActorContext(ProviderActorKind.User, user.Id, principal);
        var policy = new ProviderExecutionPolicy(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, false),
            ProviderExplicitContentPolicy.Allow, false, true, false, [artist.ProviderId]);
        var plan = await router.PlanAsync<IProviderMetadataCapability>(new(ProviderCapabilityKind.Metadata, actor, policy,
            "releases-artist-albums", "releases", DateTimeOffset.UtcNow.AddSeconds(30), [artist.ProviderId], cancellationToken: token));
        return plan.Candidates.FirstOrDefault(item => item.Descriptor.Hooks.Contains("getArtistAlbums", StringComparer.Ordinal));
    }

    private async Task EnsureScheduleAsync(AllstarrDbContext db, UserRecord user, CancellationToken token)
    {
        var id = ScheduleId(user);
        var schedule = await db.JobSchedules.SingleOrDefaultAsync(item => item.Id == id, token);
        if (schedule == null)
        {
            schedule = new()
            {
                Id = id,
                OwnerUserId = user.Id,
                JobType = JobType,
                CronExpression = "0 4 * * *",
                TimeZoneId = "UTC",
                OverlapPolicy = ScheduleOverlapPolicy.Skip,
                MisfirePolicy = ScheduleMisfirePolicy.RunOnce,
                CreatedAt = clock.UtcNow,
                Revision = 1
            };
            db.JobSchedules.Add(schedule);
        }
        if (!schedule.Enabled) { schedule.Enabled = true; schedule.NextRunAt = clock.UtcNow; }
        schedule.UpdatedAt = clock.UtcNow;
    }

    private static Guid ScheduleId(UserRecord user) => new(Convert.FromHexString(Hash("release-schedule:" + Scope(user)))[..16]);
    private static string Scope(UserRecord user) => Hash(user.Id + ":" + user.BackendType + ":" + user.BackendInstanceId + ":" + user.BackendPrincipalId);
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string CatalogKey(FollowedReleaseArtist artist, ProviderExecutionContext context) =>
        Hash(context.ProviderId + ":" + artist.ArtistId + ":" + context.Account?.AccountId + ":" + context.Account?.Revision);
    private static DateTimeOffset NextDay(DateTimeOffset now) => new(now.UtcDateTime.Date.AddDays(1), TimeSpan.Zero);
    private static DurableJobCompletion Deferred(TimeSpan delay) => DurableJobCompletion.Defer("releases-backoff", "Artist catalog polling will continue later.", delay);
    private static T? Read<T>(ExternalMetadataSnapshotRecord? row) => row == null ? default : JsonSerializer.Deserialize<T>(row.PayloadJson);
    private static async Task<UserRecord> UserAsync(AllstarrDbContext db, Guid owner, CancellationToken token) =>
        await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == owner && item.Enabled, token)
            ?? throw new UnauthorizedAccessException("A linked listener is required.");
    private static Task<ExternalMetadataSnapshotRecord?> LatestAsync(AllstarrDbContext db, string kind, string key, CancellationToken token) =>
        db.ExternalMetadataSnapshots.AsNoTracking().Where(item => item.ResourceKind == kind && item.ExternalIdHash == key)
            .OrderByDescending(item => item.RetrievedAt).ThenByDescending(item => item.SnapshotVersion).ThenByDescending(item => item.Id).FirstOrDefaultAsync(token);
    private static async Task<FollowedReleaseArtist[]> FollowsAsync(AllstarrDbContext db, UserRecord user, CancellationToken token) =>
        Read<FollowedReleaseArtist[]>(await LatestAsync(db, FollowsKind, Scope(user), token)) ?? [];

    private async Task SaveAsync<T>(AllstarrDbContext db, UserRecord user, string kind, string key, T value, CancellationToken token)
    {
        var previous = await LatestAsync(db, kind, key, token);
        var json = JsonSerializer.Serialize(value);
        db.ExternalMetadataSnapshots.Add(new()
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = user.Id,
            Protocol = user.BackendType,
            BackendInstanceId = user.BackendInstanceId,
            BackendPrincipalId = user.BackendPrincipalId,
            ProviderId = "allstarr",
            ResourceKind = kind,
            ExternalIdHash = key,
            SnapshotVersion = (previous?.SnapshotVersion ?? 0) + 1,
            ProviderRevision = "1",
            PayloadJson = json,
            PayloadSha256 = Hash(json),
            CorrelationId = "releases",
            RetrievedAt = clock.UtcNow
        });
    }

    private static async Task<string?> KnownArtistMbidAsync(AllstarrDbContext db, UserRecord user,
        ProviderExternalResourceId artist, CancellationToken token)
    {
        var row = await db.ExternalMetadataSnapshots.AsNoTracking().Where(item => item.OwnerUserId == user.Id &&
            item.ProviderId == artist.ProviderId && item.ResourceKind == "artist" && item.ExternalIdHash == Hash(artist.Value) &&
            item.Protocol == user.BackendType && item.BackendInstanceId == user.BackendInstanceId && item.BackendPrincipalId == user.BackendPrincipalId)
            .OrderByDescending(item => item.RetrievedAt).FirstOrDefaultAsync(token);
        if (row == null) return null;
        using var payload = JsonDocument.Parse(row.PayloadJson);
        if (!payload.RootElement.TryGetProperty("ProviderIds", out var ids)) return null;
        foreach (var key in new[] { "musicbrainzartist", "musicbrainz" })
            if (ids.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var mbid)) return mbid.ToString();
        return null;
    }
}

public sealed class NewReleasesJobHandler(NewReleasesService releases) : IDurableJobHandler
{
    public string JobType => NewReleasesService.JobType;
    public Task<DurableJobCompletion> ExecuteAsync(DurableJobExecutionContext context, CancellationToken cancellationToken) =>
        context.Claim.OwnerUserId is { } owner ? releases.PollAsync(owner, context.Claim.JobId, cancellationToken) :
            Task.FromResult(DurableJobCompletion.Failure("releases-owner-missing", "A linked listener is required."));
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Core.Enrichment;
using allstarr.Core.AudioMuse;
using allstarr.Core.Intelligence;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Releases;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Music;

public sealed record PersonalMixItem(Guid MixId, int Generation, Song Song, string Source, string Reason, double Score);
public sealed record PersonalMix(Guid Id, string Kind, string Name, string Description, int Generation,
    DateTimeOffset? UpdatedAt, MixReadiness Readiness, bool Added, IReadOnlyList<PersonalMixItem> Songs)
{
    public string Section => Kind == "custom" ? "custom" : Kind == "external" ? "yourServices" : PersonalMixMoods.Kinds.Contains(Kind) ? "moods" : "madeForYou";
    public int SortOrder => Kind == "external" || Kind == "custom" ? 0 : Array.IndexOf(PersonalMixRules.Kinds, Kind);
    public string? SourceLabel { get; init; }
    public string? RecipeVersion { get; init; }
    public string? SeedName { get; init; }
    public string? CoverTag { get; init; }
}
public sealed record MixFeedback(string SongId, string Kind, bool TodayOnly = false);
internal sealed class PersonalMixDocument
{
    public int AppsOptInVersion { get; set; }
    public string TimeZoneId { get; set; } = TimeZoneInfo.Local.Id;
    public Dictionary<string, PersonalMix> Mixes { get; set; } = [];
    public Dictionary<string, DateTimeOffset> DiscoverPicks { get; set; } = [];
    public Dictionary<Guid, PersonalMix> Previews { get; set; } = [];
}

public sealed partial class PersonalMixService(IDbContextFactory<AllstarrDbContext> factory, IPlatformClock clock,
    SharedTagStore tags, TasteService taste, MusicMixService radio, IProtocolProviderGateway providers,
    NewReleasesService releases, IDurableRuntimeSettings settings, IEffectiveProviderPolicyResolver policies,
    IBackendLibraryAccessResolver libraryAccess, AudioMuseDiscovery? audioMuse = null)
{
    public const string PublishKey = "Mixes:PublishToApps", JobType = "mixes.refresh";
    private const string ResourceKind = "personal-mixes";
    private readonly SemaphoreSlim writes = new(1, 1);
    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static string Scope(UserRecord user) => Hash($"{user.Id}:{user.BackendType}:{user.BackendInstanceId}:{user.BackendPrincipalId}");
    private static Guid Id(UserRecord user, string kind) => new(Convert.FromHexString(Hash(Scope(user) + ":" + kind))[..16]);
    private static async Task<UserRecord> UserAsync(AllstarrDbContext db, Guid owner, CancellationToken token) =>
        await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == owner && item.Enabled && !item.IsSynthetic, token)
            ?? throw new UnauthorizedAccessException();
    private static IQueryable<ListeningEventRecord> Events(AllstarrDbContext db, UserRecord user) =>
        db.ListeningEvents.AsNoTracking().Where(item => item.OwnerUserId == user.Id && item.Protocol == user.BackendType && item.BackendInstanceId == user.BackendInstanceId);
    private static Task<ExternalMetadataSnapshotRecord?> RowAsync(AllstarrDbContext db, UserRecord user, CancellationToken token, string kind = ResourceKind) =>
        db.ExternalMetadataSnapshots.AsNoTracking().Where(item => item.OwnerUserId == user.Id && item.Protocol == user.BackendType &&
            item.BackendInstanceId == user.BackendInstanceId && item.BackendPrincipalId == user.BackendPrincipalId &&
            item.ProviderId == "allstarr" && item.ResourceKind == kind && item.ExternalIdHash == Scope(user))
            .OrderByDescending(item => item.SnapshotVersion).FirstOrDefaultAsync(token);

    private async Task<PersonalMixDocument> LoadAsync(AllstarrDbContext db, UserRecord user, CancellationToken token)
    {
        var row = await RowAsync(db, user, token);
        var document = row == null ? new() { AppsOptInVersion = 3 } : JsonSerializer.Deserialize<PersonalMixDocument>(row.PayloadJson)!;
        if (document.AppsOptInVersion < 3)
        {
            var payload = JsonNode.Parse(row!.PayloadJson)!;
            foreach (var item in payload["Mixes"]!.AsObject())
            {
                var mix = item.Value!.AsObject();
                mix["Added"] = (mix["Added"]?.GetValue<bool>() ?? false) || (mix["ShowInApps"]?.GetValue<bool>() ?? false);
                mix.Remove("ShowInApps");
            }
            if (payload["ExternalShowInApps"] is JsonObject choices)
                foreach (var choice in choices.Where(item => item.Value?.GetValue<bool>() == true))
                    if (Guid.TryParse(choice.Key, out var id)) await SetExternalInAppsAsync(user, id, true, token, payload["TimeZoneId"]?.GetValue<string>() ?? TimeZoneInfo.Local.Id);
            payload.AsObject().Remove("ExternalShowInApps");
            payload["AppsOptInVersion"] = 3;
            document = payload.Deserialize<PersonalMixDocument>()!;
            await SaveAsync(db, user, document, token);
        }
        foreach (var kind in new[] { "on-repeat", "rewind", "release-radar", "discover-weekly" })
            if (document.Mixes.TryGetValue(kind, out var named))
                document.Mixes[kind] = named with { Name = PersonalMixRules.Name(kind), Description = "Made for you · " + PersonalMixRules.Name(kind) };
        foreach (var kind in PersonalMixRules.Kinds.Where(kind => kind.StartsWith("daily-", StringComparison.Ordinal)))
            if (document.Mixes.TryGetValue(kind, out var blend))
            {
                var parts = blend.Name.Split(" · ", 3);
                var style = blend.CoverTag ?? (parts.Length > 1 ? parts[1] : null);
                document.Mixes[kind] = blend with
                {
                    Name = PersonalMixRules.Name(kind, style),
                    CoverTag = style,
                    Description = parts.Length > 1 ? "Made for you · " + string.Join(" · ", parts.Skip(1))
                        : blend.Description.Contains("Daily Mix", StringComparison.Ordinal) ? "Made for you" : blend.Description
                };
            }
        var missing = PersonalMixRules.Kinds.Where(kind => !document.Mixes.ContainsKey(kind)).ToArray();
        var stale = document.Mixes.Values.Where(mix => PersonalMixRules.Kinds.Contains(mix.Kind) && !PersonalMixMoods.Kinds.Contains(mix.Kind) && !mix.Readiness.Ready)
            .Select(mix => mix.Kind).ToArray();
        if (missing.Length == 0 && stale.Length == 0) return document;
        var events = await Events(db, user).Where(item => item.State == ListeningEventState.Completed && item.ListenedAt <= clock.UtcNow)
            .Select(item => new { item.Artist, item.TrackReference, item.ListenedAt }).ToArrayAsync(token);
        var follows = await releases.ListAsync(user.Id, token);
        var loved = (await db.FavoriteStates.AsNoTracking().Where(item => item.OwnerUserId == user.Id &&
            item.Protocol == user.BackendType && item.BackendInstanceId == user.BackendInstanceId && item.IsFavorite)
            .Select(item => item.ItemId).ToArrayAsync(token)).ToHashSet(StringComparer.Ordinal);
        var recent = events.Count(item => item.ListenedAt >= clock.UtcNow.AddDays(-30));
        var artists = follows.Count > 0 || events.GroupBy(item => item.Artist).Any(group => group.Count() >= 5);
        var old = events.GroupBy(item => item.TrackReference).Any(group => (group.Count() >= 5 || loved.Contains(group.Key)) &&
            group.Max(item => item.ListenedAt) <= clock.UtcNow.AddMonths(-6));
        foreach (var kind in missing.Concat(stale))
        {
            var ready = PersonalMixRules.Readiness(kind, events.Length, recent, false, artists, old);
            if (PersonalMixMoods.Kinds.Contains(kind)) ready = PersonalMixMoods.Readiness(0);
            document.Mixes[kind] = document.Mixes.TryGetValue(kind, out var mix) ? mix with { Readiness = ready }
                : new(Id(user, kind), kind, PersonalMixRules.Name(kind),
                    PersonalMixMoods.Kinds.Contains(kind) ? "Updated Monday · Music for " + kind : "Made for you", 0, null, ready, false, []);
        }
        return document;
    }

    internal async Task RefreshAfterHistoryResolutionAsync(Guid owner, Guid resolutionId, DurableJobQueue queue, CancellationToken token = default)
    {
        await writes.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var user = await UserAsync(db, owner, token);
            var saved = await RowAsync(db, user, token);
            if (saved == null) return;
            var before = JsonSerializer.Deserialize<PersonalMixDocument>(saved.PayloadJson)!;
            var fresh = await LoadAsync(db, user, token);
            var transitioned = before.Mixes.Any(item => PersonalMixRules.Kinds.Contains(item.Key) && !PersonalMixMoods.Kinds.Contains(item.Key) &&
                (!item.Value.Readiness.Ready || item.Value.Songs.Count == 0) &&
                fresh.Mixes.TryGetValue(item.Key, out var mix) && mix.Readiness.Ready);
            if (transitioned)
                await queue.EnqueueAsync(new DurableJobEnqueueRequest<Guid>(JobType, $"history-resolution:{resolutionId:N}", Guid.Empty, owner), token);
        }
        finally { writes.Release(); }
    }

    private async Task SaveAsync(AllstarrDbContext db, UserRecord user, PersonalMixDocument document, CancellationToken token, bool preview = false)
    {
        var kind = preview ? "personal-mixes-preview" : ResourceKind;
        var previous = await RowAsync(db, user, token, kind);
        var json = JsonSerializer.Serialize(document);
        db.ExternalMetadataSnapshots.Add(new()
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = user.Id,
            Protocol = user.BackendType,
            BackendInstanceId = user.BackendInstanceId,
            BackendPrincipalId = user.BackendPrincipalId,
            ProviderId = "allstarr",
            ResourceKind = kind,
            ExternalIdHash = Scope(user),
            SnapshotVersion = (previous?.SnapshotVersion ?? 0) + 1,
            ProviderRevision = "1",
            PayloadJson = json,
            PayloadSha256 = Hash(json),
            CorrelationId = "mixes",
            RetrievedAt = clock.UtcNow
        });
        await db.SaveChangesAsync(token);
    }

    public async Task<IReadOnlyList<PersonalMix>> ListAsync(Guid owner, string? timeZoneId = null, CancellationToken token = default)
    {
        await writes.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var user = await UserAsync(db, owner, token);
            var document = await LoadAsync(db, user, token);
            if (timeZoneId != null)
            {
                DurableScheduleEngine.Validate("0 4 * * *", timeZoneId);
                document.TimeZoneId = timeZoneId.Trim();
                await SaveAsync(db, user, document, token);
            }
            return (await VisibleAsync(owner, document, token)).Concat(await ExternalMixesAsync(user, token)).ToArray();
        }
        finally { writes.Release(); }
    }

    public async Task<PersonalMix> GetAsync(Guid owner, Guid id, CancellationToken token = default)
    {
        await writes.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var user = await UserAsync(db, owner, token);
            var document = await LoadAsync(db, user, token);
            var selected = document.Mixes.SingleOrDefault(item => item.Value.Id == id);
            if (selected.Key != null)
                return (await VisibleAsync(owner, new() { Mixes = new() { [selected.Key] = selected.Value } }, token)).Single();
            return (await ExternalMixesAsync(user, token, id)).SingleOrDefault() ?? throw new KeyNotFoundException();
        }
        finally { writes.Release(); }
    }

    public async Task<IReadOnlyList<PersonalMix>> GenerateAsync(Guid owner, Guid? mixId = null, bool dryRun = false,
        string? timeZoneId = null, CancellationToken token = default, bool scheduled = false)
    {
        await writes.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var user = await UserAsync(db, owner, token);
            var document = await LoadAsync(db, user, token);
            if (timeZoneId != null) { DurableScheduleEngine.Validate("0 4 * * *", timeZoneId); document.TimeZoneId = timeZoneId.Trim(); }
            var requested = mixId == null ? null : document.Mixes.Values.SingleOrDefault(item => item.Id == mixId) ?? throw new KeyNotFoundException();
            if (requested?.Kind == "custom") throw new ArgumentException("Saved mixes do not refresh.");
            if (!scheduled && requested?.UpdatedAt > clock.UtcNow.AddMinutes(-PersonalMixRules.RefreshMinutes))
                throw new InvalidOperationException("Wait 15 minutes before refreshing this mix again.");
            var events = await Events(db, user).Where(item => item.UpdatedAt <= clock.UtcNow &&
                (item.ListenedAt == null || item.ListenedAt <= clock.UtcNow)).ToArrayAsync(token);
            var completed = events.Where(item => item.State == ListeningEventState.Completed && item.ListenedAt <= clock.UtcNow).ToArray();
            var (candidates, _) = await CandidatesAsync(db, user, events, token);
            var clusters = PersonalMixRules.Cluster(candidates);
            var features = audioMuse == null || requested != null && !PersonalMixMoods.Kinds.Contains(requested.Kind)
                ? new Dictionary<string, AudioMuseScores>() : await audioMuse.MixFeaturesAsync(await ContextAsync(user, token),
                    candidates.Where(item => item.Song.IsLocal).Select(item => item.Song.Id), token);
            var follows = await releases.ListAsync(owner, token);
            var kinds = requested == null ? PersonalMixRules.Kinds : requested.Kind.StartsWith("daily-", StringComparison.Ordinal)
                ? PersonalMixRules.Kinds.Where(kind => kind.StartsWith("daily-", StringComparison.Ordinal)).ToArray() : [requested.Kind];
            var previous = document.DiscoverPicks.Where(item => item.Value >= clock.UtcNow.AddDays(-14)).Select(item => item.Key).ToHashSet();
            foreach (var key in document.DiscoverPicks.Where(item => item.Value < clock.UtcNow.AddDays(-14)).Select(item => item.Key).ToArray()) document.DiscoverPicks.Remove(key);
            foreach (var kind in kinds)
            {
                var mix = document.Mixes[kind];
                var ready = PersonalMixRules.Readiness(kind, completed.Length, completed.Count(item => item.ListenedAt >= clock.UtcNow.AddDays(-30)),
                    candidates.Any(item => item.Source == "radio" || item.Plays == 0 && !item.Loved && item.Tags.Count > 0),
                    follows.Count > 0 || completed.GroupBy(item => item.Artist).Any(group => group.Count() >= 5),
                    candidates.Any(item => (item.Plays >= 5 || item.Loved) && item.LastPlayed <= clock.UtcNow.AddMonths(-6)));
                IReadOnlyList<MixCandidate> pool = candidates;
                var name = PersonalMixRules.Name(kind, mix.CoverTag);
                var coverTag = mix.CoverTag;
                var description = kind.StartsWith("daily-", StringComparison.Ordinal) ? mix.Description : "Made for you · " + PersonalMixRules.Name(kind);
                if (kind.StartsWith("daily-", StringComparison.Ordinal))
                {
                    var index = kind[^1] - '1';
                    if (index >= clusters.Count || clusters.Count < 2) { pool = []; if (ready.Ready) ready = new(false, "Play more songs with shared tags to form another taste cluster."); }
                    else
                    {
                        var group = clusters[index];
                        var cluster = PersonalMixRules.ClusterName(group);
                        var clusterParts = cluster.Split(" · ", 2);
                        coverTag ??= clusterParts[0];
                        name = PersonalMixRules.Name(kind, coverTag);
                        description = "Made for you · " + coverTag + (clusterParts.Length > 1 ? " · " + clusterParts[1] : "");
                        pool = candidates.Where(item => item.Tags.Count > 0 && clusters.Select((cluster, slot) =>
                            (Slot: slot, Distance: cluster.Min(other => PersonalMixRules.Distance(item, other))))
                            .OrderBy(other => other.Distance).ThenBy(other => other.Slot).First().Slot == index).ToArray();
                    }
                }
                var mood = PersonalMixMoods.Kinds.Contains(kind);
                if (mood) pool = PersonalMixMoods.Qualify(kind, candidates, features);
                var selected = ready.Ready || mood ? PersonalMixRules.Select(kind, pool, clock.UtcNow, previous, mood ? 40 : null) : [];
                if (mood) { ready = PersonalMixMoods.Readiness(selected.Count); if (!ready.Ready) selected = []; }
                if (ready.Ready && selected.Count == 0) ready = new(false, kind.StartsWith("daily-", StringComparison.Ordinal)
                    ? "More familiar songs and close new picks are needed for a 70/30 mix." : "No eligible playable songs yet. Try again after more listening or new releases.");
                var generation = mix.Generation + 1;
                var items = selected.Select(item => new PersonalMixItem(mix.Id, generation, SafeSong(item.Song), item.Source,
                    mood ? PersonalMixMoods.Reason(kind, item) : kind switch
                    {
                        "on-repeat" => $"Played {item.RecentPlays} times in 30 days",
                        "rewind" => "An old favourite you have not played in six months",
                        "release-radar" => "New release from an artist you follow or play often",
                        _ => item.Song.TasteReason ?? (item.Plays > 0 || item.Loved ? "A familiar favourite" : "A close new pick")
                    }, item.Score)).ToArray();
                document.Mixes[kind] = mix with
                {
                    Name = name,
                    Generation = generation,
                    UpdatedAt = clock.UtcNow,
                    Description = mood ? $"Updated Monday · Music for {kind}" : description,
                    CoverTag = coverTag,
                    RecipeVersion = mood ? PersonalMixMoods.RecipeVersion : null,
                    Readiness = ready,
                    Songs = items
                };
                if (kind == "discover-weekly") foreach (var key in selected.SelectMany(item => PersonalMixRules.IdentityKeys(item.Song))) document.DiscoverPicks[key] = clock.UtcNow;
            }
            await SaveAsync(db, user, document, token, dryRun);
            return document.Mixes.Values.ToArray();
        }
        finally { writes.Release(); }
    }

    private static Song SafeSong(Song song) => new()
    {
        Id = song.Id,
        CanonicalRecordingId = song.CanonicalRecordingId,
        Title = song.Title,
        Artist = song.Artist,
        Artists = song.Artists,
        Album = song.Album,
        Duration = song.Duration,
        IsLocal = song.IsLocal,
        ExternalProvider = song.ExternalProvider,
        ExternalId = song.ExternalId,
        Isrc = song.Isrc,
        ExplicitContentLyrics = song.ExplicitContentLyrics
    };

    private async Task<IReadOnlyList<PersonalMix>> VisibleAsync(Guid owner, PersonalMixDocument document, CancellationToken token)
    {
        var songs = document.Mixes.Values.SelectMany(mix => mix.Songs).Select(item => item.Song).ToArray();
        var scores = await taste.ScoreManyAsync(owner, songs, token);
        var policy = await policies.ResolveForUserAsync(owner, token);
        return document.Mixes.Values.Select(mix =>
        {
            var visible = mix.Songs.Where(item => !scores[item.Song].Blocked && policy.Includes(item.Song)).ToArray();
            var readiness = PersonalMixMoods.Kinds.Contains(mix.Kind) && mix.Generation > 0
                ? PersonalMixMoods.Readiness(visible.Length) : mix.Readiness;
            if (readiness.Ready && visible.Length == 0)
                readiness = new(false, "No playable songs are available for this mix yet. Refresh after more listening or new releases.");
            return mix with { Songs = visible, Readiness = readiness };
        }).ToArray();
    }

    public async Task SetInAppsAsync(Guid owner, Guid id, bool enabled, CancellationToken token)
    {
        await writes.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var user = await UserAsync(db, owner, token);
            var document = await LoadAsync(db, user, token);
            if (await SetExternalInAppsAsync(user, id, enabled, token, document.TimeZoneId))
            {
                return;
            }
            var mix = document.Mixes.Values.SingleOrDefault(item => item.Id == id) ?? throw new KeyNotFoundException();
            if (mix.Added == enabled) return;
            document.Mixes[document.Mixes.First(item => item.Value.Id == id).Key] = mix with { Added = enabled };
            await SaveAsync(db, user, document, token);
        }
        finally { writes.Release(); }
    }

    public async Task FeedbackAsync(Guid owner, Guid id, MixFeedback feedback, CancellationToken token)
    {
        await writes.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var user = await UserAsync(db, owner, token);
            var document = await LoadAsync(db, user, token);
            var mix = document.Mixes.Values.SingleOrDefault(item => item.Id == id) ?? throw new KeyNotFoundException();
            var song = mix.Songs.SingleOrDefault(item => item.Song.Id == feedback.SongId)?.Song ?? throw new KeyNotFoundException();
            var topTag = song.CanonicalRecordingId is { } recording ? (await tags.GetTags(recording, token)).Tags.FirstOrDefault()?.Tag : null;
            var nudge = feedback.Kind switch
            {
                "notForMe" => new TasteNudgeRequest("song", PersonalMixRules.Key(song), "less", feedback.TodayOnly, document.TimeZoneId),
                "neverArtist" => new("artist", song.Artist, "never", feedback.TodayOnly, document.TimeZoneId),
                "moreLikeThis" => new(topTag == null ? "artist" : "tag", topTag ?? song.Artist, "more", feedback.TodayOnly, document.TimeZoneId),
                _ => throw new ArgumentException("Unknown feedback kind.")
            };
            await taste.PutNudgeAsync(owner, nudge, token);
            document.Mixes[document.Mixes.First(item => item.Value.Id == id).Key] = mix with { Songs = mix.Songs.Where(item => item.Song.Id != song.Id).ToArray() };
            await SaveAsync(db, user, document, token);
        }
        finally { writes.Release(); }
    }

    public async Task<IReadOnlyList<VirtualPlaylistReadModel>> PublishedAsync(ProtocolExecutionContext context, CancellationToken token)
    {
        if (context.Actor?.EffectiveUserId is not { } owner || !(bool)(await settings.GetAsync(PublishKey, token)).Value) return [];
        await writes.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var user = await UserAsync(db, owner, token);
            if (user.BackendType != context.Protocol.ToString().ToLowerInvariant() || user.BackendInstanceId != context.BackendInstanceId ||
                user.BackendPrincipalId != context.VerifiedBackendPrincipalId) return [];
            var document = await LoadAsync(db, user, token);
            var localIds = document.Mixes.Values.SelectMany(mix => mix.Songs).Where(item => item.Song.IsLocal).Select(item => item.Song.Id).ToArray();
            var allowed = localIds.Length == 0 ? [] : await LibraryTrackAccess.Query(db, context, await libraryAccess.ResolveAsync(context, token))
                .Where(item => localIds.Contains(item.BackendItemId)).Select(item => item.BackendItemId).ToArrayAsync(token);
            return (await VisibleAsync(owner, document, token)).Where(mix => mix.Generation > 0 && mix.Songs.Count > 0 && mix.Added && mix.Readiness.Ready &&
                (!PersonalMixMoods.Kinds.Contains(mix.Kind) || mix.Songs.Count(item => !item.Song.IsLocal || allowed.Contains(item.Song.Id)) >= 25)).Select(mix =>
                new VirtualPlaylistReadModel(PlaylistVirtualizationService.CreateProtocolId(mix.Id), mix.Id, mix.Id, mix.Name, mix.Description,
                    null, "allstarr", mix.Kind, mix.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture), PlaylistLinkMode.Virtual,
                    mix.Songs.Where(item => !item.Song.IsLocal || allowed.Contains(item.Song.Id)).Select((item, index) => new VirtualPlaylistTrack(index, item.Song.Id, item.Song.Title, item.Song.Artist,
                        item.Song.Album, null, item.Song.Duration * 1000L, null, TrackMatchState.Accepted,
                        item.Song.ExternalProvider, item.Song.ExternalId, item.Song.IsLocal ? TrackRouteKind.Local : TrackRouteKind.External,
                        item.Song.ExternalProvider, item.Song.ExternalId)
                    { PlaybackExplicitContentLyrics = item.Song.ExplicitContentLyrics }).ToArray())
                { BackendPublic = false, MixCover = MixCoverInput.From(mix) }).ToArray();
        }
        finally { writes.Release(); }
    }

    public async Task EnsureSchedulesAsync(CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        foreach (var user in await db.Users.AsNoTracking().Where(item => item.Enabled && !item.IsSynthetic).ToArrayAsync(token))
        {
            var state = await RowAsync(db, user, token);
            var document = state == null ? new PersonalMixDocument() : JsonSerializer.Deserialize<PersonalMixDocument>(state.PayloadJson)!;
            foreach (var kind in PersonalMixRules.Kinds.Where(kind => !kind.StartsWith("daily-", StringComparison.Ordinal) || kind == "daily-1"))
            {
                var id = Id(user, "schedule:" + kind);
                var row = await db.JobSchedules.SingleOrDefaultAsync(item => item.Id == id, token);
                if (row != null && row.TimeZoneId == document.TimeZoneId) continue;
                if (row == null) db.JobSchedules.Add(row = new() { Id = id, OwnerUserId = user.Id, CreatedAt = clock.UtcNow });
                row.JobType = JobType;
                row.Enabled = true;
                row.CronExpression = PersonalMixRules.Cron(kind);
                row.TimeZoneId = document.TimeZoneId;
                row.PayloadTemplateJson = JsonSerializer.Serialize(Id(user, kind));
                row.OverlapPolicy = ScheduleOverlapPolicy.Skip;
                row.MisfirePolicy = ScheduleMisfirePolicy.RunOnce;
                row.NextRunAt = DurableScheduleEngine.GetNextOccurrence(row.CronExpression, row.TimeZoneId, clock.UtcNow);
                row.UpdatedAt = clock.UtcNow;
                row.Revision++;
            }
        }
        await db.SaveChangesAsync(token);
    }
}

public sealed class PersonalMixJobHandler(PersonalMixService mixes) : IDurableJobHandler
{
    public string JobType => PersonalMixService.JobType;
    public async Task<DurableJobCompletion> ExecuteAsync(DurableJobExecutionContext context, CancellationToken cancellationToken)
    {
        if (context.Claim.OwnerUserId is not { } owner) return DurableJobCompletion.Failure("mix-owner-missing", "A linked listener is required.");
        var mixId = context.Claim.Payload.GetGuid();
        await mixes.GenerateAsync(owner, mixId == Guid.Empty ? null : mixId, token: cancellationToken, scheduled: true);
        return DurableJobCompletion.Success();
    }
}

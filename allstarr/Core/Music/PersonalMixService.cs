using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Core.Enrichment;
using allstarr.Core.ClientEvents;
using allstarr.Core.Capabilities;
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
    public MixRecipeEvidence? Evidence { get; init; }
}
public sealed record MixFeedback(string SongId, string Kind, bool TodayOnly = false);
internal sealed class PersonalMixDocument
{
    public int AppsOptInVersion { get; set; }
    public string TimeZoneId { get; set; } = TimeZoneInfo.Local.Id;
    public Dictionary<string, PersonalMix> Mixes { get; set; } = [];
    public Dictionary<string, DateTimeOffset> DiscoverPicks { get; set; } = [];
    public Dictionary<Guid, PersonalMix> Previews { get; set; } = [];
    public List<MixFillSong> FillSongs { get; set; } = [];
    public Dictionary<string, DateTimeOffset> FillReadAt { get; set; } = [];
}

internal sealed record MixFillSong(Song Song, DateTimeOffset ReadAt, string Source, bool Filled,
    string[] Tags, string[] ChartTags, string? Seed, string? EvidenceSource, DateTimeOffset? Release, string? ReleaseKey);

public sealed partial class PersonalMixService(IDbContextFactory<AllstarrDbContext> factory, IPlatformClock clock,
    SharedTagStore tags, TasteService taste, MusicMixService radio, IProtocolProviderGateway providers,
    NewReleasesService releases, IDurableRuntimeSettings settings, IEffectiveProviderPolicyResolver policies,
    IBackendLibraryAccessResolver libraryAccess, AudioMuseDiscovery? audioMuse = null, SharedTagSources? tagSources = null, IProviderRegistry? registry = null, LastFmSimilarTracks? similarTracks = null,
    ILogger<PersonalMixService>? logger = null, IClientEventPublisher? events = null,
    ILibraryIndexService? libraryIndex = null, ITrackIdentityService? identities = null)
{
    public const string PublishKey = "Mixes:PublishToApps", JobType = "mixes.refresh";
    // Saved songs carry dozens of empty fields; leaving them out keeps a listener's mixes well under the 1 MB a stored
    // document may hold. A long history had pushed it over, and refreshes after new tags failed.
    private static readonly JsonSerializerOptions DocumentJson = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
    private const string ResourceKind = "personal-mixes";
    private const int KeptDocuments = 3;
    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly SemaphoreSlim generations = new(1, 1);
    private readonly IClientEventPublisher? clientEvents = events;
    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static string Scope(UserRecord user) => Hash($"{user.Id}:{user.BackendType}:{user.BackendInstanceId}:{user.BackendPrincipalId}");
    private static Guid Id(UserRecord user, string kind) => new(Convert.FromHexString(Hash(Scope(user) + ":" + kind))[..16]);
    private static async Task<UserRecord> UserAsync(AllstarrDbContext db, Guid owner, CancellationToken token) =>
        await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == owner && item.Enabled && !item.IsSynthetic, token)
            ?? throw new UnauthorizedAccessException();
    private static async Task<UserRecord?> BrowseUserAsync(AllstarrDbContext db, Guid owner, CancellationToken token,
        ProtocolExecutionContext? context = null)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == owner && item.Enabled && !item.IsSynthetic, token);
        if (user == null || context != null && (user.BackendType != context.Protocol.ToString().ToLowerInvariant() ||
            user.BackendInstanceId != context.BackendInstanceId || user.BackendPrincipalId != context.VerifiedBackendPrincipalId)) return null;
        return user;
    }

    private static IQueryable<ListeningEventRecord> Events(AllstarrDbContext db, UserRecord user, bool imported) =>
        db.ListeningEvents.AsNoTracking().Where(item => item.OwnerUserId == user.Id && item.Protocol == user.BackendType && item.BackendInstanceId == user.BackendInstanceId &&
            (imported || item.SourceKind == "protocol"));
    private HashSet<Guid> RecentHistory(IReadOnlyList<ListeningEventRecord> events)
    {
        var live = events.Where(item => item.SourceKind == "protocol" && item.State == ListeningEventState.Completed &&
            item.ListenedAt >= clock.UtcNow.AddDays(-MixRecipes.RecentDays)).ToArray();
        var result = live.Select(item => item.Id).ToHashSet();
        if (live.Length >= PersonalMixRules.RepeatPlays) return result;
        var imports = events.Where(item => item.SourceKind != "protocol" && item.State == ListeningEventState.Completed &&
            item.ListenedAt <= clock.UtcNow).ToArray();
        var latest = imports.Select(item => item.ListenedAt).Max();
        result.UnionWith(imports.Where(item => item.ListenedAt >= latest?.AddDays(-30)).Select(item => item.Id));
        return result;
    }

    private static Task<ExternalMetadataSnapshotRecord?> RowAsync(AllstarrDbContext db, UserRecord user, CancellationToken token, string kind = ResourceKind) =>
        db.ExternalMetadataSnapshots.AsNoTracking().Where(item => item.OwnerUserId == user.Id && item.Protocol == user.BackendType &&
            item.BackendInstanceId == user.BackendInstanceId && item.BackendPrincipalId == user.BackendPrincipalId &&
            item.ProviderId == "allstarr" && item.ProviderAccountId == null && item.ResourceKind == kind && item.ExternalIdHash == Scope(user))
            .OrderByDescending(item => item.SnapshotVersion).FirstOrDefaultAsync(token);

    private async Task<PersonalMixDocument> LoadAsync(AllstarrDbContext db, UserRecord user, CancellationToken token, bool publishedOnly = false,
        ExternalMetadataSnapshotRecord? saved = null, PersonalMixDocument? document = null)
    {
        var row = saved ?? await RowAsync(db, user, token);
        document ??= row == null ? new() { AppsOptInVersion = 3 } : JsonSerializer.Deserialize<PersonalMixDocument>(row.PayloadJson)!;
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
        foreach (var kind in PersonalMixRules.Kinds.Where(kind => !kind.StartsWith("daily-", StringComparison.Ordinal)))
            if (document.Mixes.TryGetValue(kind, out var named))
                document.Mixes[kind] = named with { Name = PersonalMixRules.Name(kind), Description = PersonalMixDescriptions.OnRead(named, null) };
        foreach (var kind in PersonalMixRules.Kinds.Where(kind => kind.StartsWith("daily-", StringComparison.Ordinal)))
            if (document.Mixes.TryGetValue(kind, out var blend))
            {
                var parts = blend.Name.Split(" · ", 3);
                var style = blend.CoverTag ?? (parts.Length > 1 ? parts[1] : null);
                document.Mixes[kind] = blend with
                {
                    Name = PersonalMixRules.Name(kind, style),
                    CoverTag = style,
                    Description = PersonalMixDescriptions.OnRead(blend, style)
                };
            }
        if (publishedOnly) return document;
        foreach (var kind in PersonalMixRules.Kinds.Where(kind => !document.Mixes.ContainsKey(kind)))
            document.Mixes[kind] = new(Id(user, kind), kind, PersonalMixRules.Name(kind),
                PersonalMixDescriptions.Tagline(kind), 0, null,
                new(false, "Refresh this mix to look for songs from your music and services."), false, []);
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
            if (await db.Jobs.AsNoTracking().AnyAsync(item => item.Type == JobType && item.OwnerUserId == owner &&
                (item.IdempotencyKey.StartsWith("history-tags:") || item.IdempotencyKey.StartsWith("history-resolution:")) &&
                (item.CreatedAt > clock.UtcNow.AddMinutes(-15) || item.State == DurableJobState.Pending ||
                    item.State == DurableJobState.Running || item.State == DurableJobState.RetryScheduled), token)) return;
            var before = JsonSerializer.Deserialize<PersonalMixDocument>(saved.PayloadJson)!;
            var hasNewHistory = await Events(db, user, await taste.UseImportedHistoryAsync(owner, token))
                .AnyAsync(item => item.State == ListeningEventState.Completed, token);
            if (hasNewHistory && before.Mixes.Values.Any(mix => PersonalMixRules.Kinds.Contains(mix.Kind) && (!mix.Readiness.Ready || mix.Songs.Count == 0)))
                await queue.EnqueueAsync(new DurableJobEnqueueRequest<Guid>(JobType,
                    $"history-resolution:{owner:N}:{clock.UtcNow.ToUnixTimeSeconds() / 900}", Guid.Empty, owner,
                    Priority: -100, AvailableAt: clock.UtcNow.AddMinutes(15)), token);
        }
        finally { writes.Release(); }
    }

    private async Task SaveAsync(AllstarrDbContext db, UserRecord user, PersonalMixDocument document, CancellationToken token, bool preview = false)
    {
        var kind = preview ? "personal-mixes-preview" : ResourceKind;
        var previous = await RowAsync(db, user, token, kind);
        var json = JsonSerializer.Serialize(document, DocumentJson);
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
        // Only the newest is ever read, and each one holds every mix.
        var oldest = (previous?.SnapshotVersion ?? 0) + 1 - KeptDocuments;
        await db.ExternalMetadataSnapshots.Where(item => item.OwnerUserId == user.Id && item.ProviderId == "allstarr" && item.ResourceKind == kind &&
            item.ExternalIdHash == Scope(user) && item.SnapshotVersion <= oldest).ExecuteDeleteAsync(token);
    }

    private async Task<(UserRecord User, PersonalMixDocument Document, int Version)> SnapshotAsync(
        Guid owner, CancellationToken token, string? timeZoneId = null)
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
                if (document.TimeZoneId != timeZoneId.Trim())
                {
                    document.TimeZoneId = timeZoneId.Trim();
                    await SaveAsync(db, user, document, token);
                }
            }
            return (user, document, (await RowAsync(db, user, token))?.SnapshotVersion ?? 0);
        }
        finally { writes.Release(); }
    }

    private async Task<(ListeningEventRecord[] Events, string Stamp)> HistoryAsync(AllstarrDbContext db, UserRecord user, CancellationToken token)
    {
        var imported = await taste.UseImportedHistoryAsync(user.Id, token);
        var events = await Events(db, user, imported).Where(item => item.UpdatedAt <= clock.UtcNow &&
            (item.ListenedAt == null || item.ListenedAt <= clock.UtcNow)).ToArrayAsync(token);
        return (events, ImportedStamp(imported, events.Where(item => item.SourceKind != "protocol")));
    }

    private static string ImportedStamp(bool imported, IEnumerable<ListeningEventRecord> events) =>
        Hash(JsonSerializer.Serialize(new
        {
            Imported = imported,
            Events = events.OrderBy(item => item.Id).Select(item => new
            { item.Id, item.Revision, item.UpdatedAt, item.CanonicalRecordingId, item.LibraryTrackId, item.TrackReference })
        }));

    private static async Task<string> InputRevisionAsync(AllstarrDbContext db, Guid owner, CancellationToken token)
    {
        var revision = await db.TasteProfiles.AsNoTracking().Where(item => item.OwnerUserId == owner)
            .Select(item => item.Revision).SingleOrDefaultAsync(token);
        var controls = await db.RuntimeSettings.AsNoTracking().Where(item => item.OwnerUserId == null || item.OwnerUserId == owner)
            .OrderBy(item => item.Id).Select(item => new { item.Id, item.Revision }).ToArrayAsync(token);
        return Hash(JsonSerializer.Serialize(new { Taste = revision, Controls = controls }));
    }

    private async Task<IReadOnlyList<MixCandidate>> RevalidateAsync(UserRecord user, IReadOnlyList<MixCandidate> candidates, CancellationToken token, SeedMixDiagnostics? diagnostics = null, GenerationInputs? inputs = null)
    {
        using var phase = diagnostics?.Phase("revalidate");
        diagnostics?.Reset("revalidated-never", "revalidated-policy", "access");
        var context = await ContextAsync(user, token);
        var access = await libraryAccess.ResolveAsync(context, token);
        await using var db = await factory.CreateDbContextAsync(token);
        var ids = candidates.Where(item => item.Song.IsLocal).Select(item => item.Song.Id).ToArray();
        var allowed = await LibraryTrackAccess.Query(db, context, access).Where(item => ids.Contains(item.BackendItemId))
            .Select(item => item.BackendItemId).ToArrayAsync(token);
        var scores = await taste.ScoreManyAsync(user.Id, candidates.Select(item => item.Song).ToArray(), token);
        var nudges = (await taste.GetAsync(user.Id, token)).Nudges;
        bool Nudge(MixCandidate item, string mode) => nudges.Any(nudge => nudge.Mode == mode && (nudge.Kind == "tag" && item.Tags.ContainsKey(nudge.Name) ||
            nudge.Kind == "artist" && string.Equals(nudge.Name, item.Song.Artist, StringComparison.OrdinalIgnoreCase) ||
            nudge.Kind == "song" && nudge.Name == PersonalMixRules.Key(item.Song)));
        return candidates.Where(item =>
            {
                if (scores[item.Song].Blocked || Nudge(item, "never")) { inputs?.Exclude("Never", item.Song.Id); return false; }
                inputs?.Diagnostics.Count("revalidate.never");
                diagnostics?.Count(item.Source, "revalidated-never");
                if (!context.Policy!.Includes(item.Song)) { inputs?.Exclude("Listening policy", item.Song.Id); return false; }
                inputs?.Diagnostics.Count("revalidate.policy");
                diagnostics?.Count(item.Source, "revalidated-policy");
                if (!(item.Song.IsLocal ? allowed.Contains(item.Song.Id) : context.Policy.IsEnabled(item.Song.ExternalProvider ?? "")))
                { inputs?.Exclude("Current access", item.Song.Id); return false; }
                inputs?.Diagnostics.Count("revalidate.access");
                diagnostics?.Count(item.Source, "access");
                return true;
            })
            .Select(item => item with { Score = scores[item.Song].Score, Less = Nudge(item, "less") }).ToArray();
    }

    private async Task<bool> TrySaveAsync(UserRecord capturedUser, PersonalMixDocument document, int version, string revision,
        string history, CancellationToken token, bool preview = false)
    {
        await writes.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            var user = await UserAsync(db, capturedUser.Id, token);
            if (Scope(user) != Scope(capturedUser)) throw new UnauthorizedAccessException();
            var imported = await taste.UseImportedHistoryAsync(user.Id, token);
            var inputs = await Events(db, user, imported).Where(item => item.SourceKind != "protocol" && item.UpdatedAt <= clock.UtcNow &&
                (item.ListenedAt == null || item.ListenedAt <= clock.UtcNow)).Select(item => new ListeningEventRecord
                {
                    Id = item.Id,
                    Revision = item.Revision,
                    UpdatedAt = item.UpdatedAt,
                    CanonicalRecordingId = item.CanonicalRecordingId,
                    LibraryTrackId = item.LibraryTrackId,
                    TrackReference = item.TrackReference
                }).ToArrayAsync(token);
            if (ImportedStamp(imported, inputs) != history) return false;
            if (((await RowAsync(db, user, token))?.SnapshotVersion ?? 0) != version ||
                await InputRevisionAsync(db, user.Id, token) != revision) return false;
            await SaveAsync(db, user, document, token, preview);
            await transaction.CommitAsync(token);
            return true;
        }
        finally { writes.Release(); }
    }

    public Task<IReadOnlyList<PersonalMix>> ListAsync(Guid owner, string? timeZoneId = null, CancellationToken token = default) =>
        ListCoreAsync(owner, timeZoneId, token);

    public Task<IReadOnlyList<PersonalMix>> ListCardsAsync(Guid owner, string? timeZoneId = null, CancellationToken token = default) =>
        ListCoreAsync(owner, timeZoneId, token, cardsOnly: true);

    public Task<IReadOnlyList<PersonalMix>> ListAsync(ProtocolExecutionContext context, string? timeZoneId = null) =>
        context.Actor?.EffectiveUserId is { } owner
            ? ListCoreAsync(owner, timeZoneId, context.CancellationToken, context)
            : Task.FromResult<IReadOnlyList<PersonalMix>>([]);

    private async Task<IReadOnlyList<PersonalMix>> ListCoreAsync(Guid owner, string? timeZoneId, CancellationToken token,
        ProtocolExecutionContext? context = null, bool cardsOnly = false)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var user = await BrowseUserAsync(db, owner, token, context);
        if (user == null) return [];
        var row = await RowAsync(db, user, token);
        if (timeZoneId != null) DurableScheduleEngine.Validate("0 4 * * *", timeZoneId);
        var saved = row == null ? null : JsonSerializer.Deserialize<PersonalMixDocument>(row.PayloadJson)!;
        PersonalMixDocument document;
        if (saved != null && (saved.AppsOptInVersion < 3 || timeZoneId != null && saved.TimeZoneId != timeZoneId.Trim()) ||
            saved == null && timeZoneId != null)
            document = (await SnapshotAsync(owner, token, timeZoneId)).Document;
        else
            document = await LoadAsync(db, user, token, saved: row, document: saved);
        return (await VisibleAsync(owner, document, token, preferLocalCopies: !cardsOnly))
            .Concat(await ExternalMixesAsync(user, token, cardsOnly: cardsOnly)).ToArray();
    }

    public async Task<PersonalMix> GetAsync(Guid owner, Guid id, CancellationToken token = default)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var user = await UserAsync(db, owner, token);
        var row = await RowAsync(db, user, token);
        var saved = row == null ? null : JsonSerializer.Deserialize<PersonalMixDocument>(row.PayloadJson)!;
        var document = saved is { AppsOptInVersion: < 3 } ? (await SnapshotAsync(owner, token)).Document
            : await LoadAsync(db, user, token, saved: row, document: saved);
        var selected = document.Mixes.SingleOrDefault(item => item.Value.Id == id);
        if (selected.Key != null)
            return (await VisibleAsync(owner, new() { Mixes = new() { [selected.Key] = selected.Value } }, token)).Single();
        return (await ExternalMixesAsync(user, token, id)).SingleOrDefault() ?? throw new KeyNotFoundException();
    }

    public async Task<IReadOnlyList<PersonalMix>> GenerateAsync(Guid owner, Guid? mixId = null, bool dryRun = false,
        string? timeZoneId = null, CancellationToken token = default, bool scheduled = false, bool allowProviderReads = true,
        bool afterNewHistory = false)
    {
        await generations.WaitAsync(token);
        var diagnostics = new MixGenerationDiagnostics();
        var generatedKinds = Array.Empty<string>();
        PersonalMixDocument? generated = null;
        var completed = false;
        try
        {
            var (user, captured, _) = await SnapshotAsync(owner, token);
            if (timeZoneId != null) DurableScheduleEngine.Validate("0 4 * * *", timeZoneId);
            var requested = mixId == null ? null : captured.Mixes.Values.SingleOrDefault(item => item.Id == mixId) ?? throw new KeyNotFoundException();
            if (requested?.Kind == "custom") throw new ArgumentException("Saved mixes do not refresh.");
            if (!scheduled && allowProviderReads && requested?.UpdatedAt > clock.UtcNow.AddMinutes(-PersonalMixRules.RefreshMinutes))
                throw new InvalidOperationException("Wait 15 minutes before refreshing this mix again.");
            if (!dryRun && timeZoneId != null) await SnapshotAsync(owner, token, timeZoneId);
            await using var db = await factory.CreateDbContextAsync(token);
            ListeningEventRecord[] events;
            string history;
            using (diagnostics.Phase("history")) (events, history) = await HistoryAsync(db, user, token);
            var kinds = requested == null ? PersonalMixRules.Kinds : scheduled && requested.Kind.StartsWith("daily-", StringComparison.Ordinal)
                ? PersonalMixRules.Kinds.Where(kind => kind.StartsWith("daily-", StringComparison.Ordinal)).ToArray() : [requested.Kind];
            generatedKinds = kinds;
            diagnostics.Count("history.events", events.Length);
            var due = kinds.Where(kind => !scheduled || !captured.FillReadAt.TryGetValue(kind, out var lastRead) ||
                DurableScheduleEngine.GetNextOccurrence(PersonalMixRules.Cron(kind), captured.TimeZoneId, lastRead) <= clock.UtcNow).ToArray();
            // Newly matched history can change who a listener's top artists are, which leaves picks made for the old ones
            // outside the recipe. An empty mix may then look again, once a day at most.
            var readKinds = allowProviderReads && !dryRun && (scheduled || requested != null || afterNewHistory)
                ? due.Where(kind => !scheduled && !afterNewHistory || !captured.FillReadAt.TryGetValue(kind, out var at) || at <= clock.UtcNow.AddDays(-1))
                    .Where(kind => !afterNewHistory || !captured.Mixes[kind].Readiness.Ready || captured.Mixes[kind].Songs.Count == 0).ToHashSet() : [];
            var inputs = new GenerationInputs
            {
                Diagnostics = diagnostics,
                Kind = readKinds.FirstOrDefault() ?? kinds[0],
                ReadKinds = readKinds,
                ReusePicks = readKinds.Count == 0
            };
            foreach (var saved in captured.FillSongs)
            {
                inputs.CachedTags[saved.Song.Id] = saved.Tags;
                if (saved.Seed != null || saved.Filled || saved.Release != null)
                    inputs.Evidence[saved.Song.Id] = (saved.Seed, null, saved.EvidenceSource ?? saved.Source, saved.ReleaseKey);
                if (saved.Filled) inputs.Filled.Add(saved.Song.Id);
                if (saved.Release != null) inputs.ReleaseDates[saved.Song.Id] = saved.Release.Value;
                if (saved.ChartTags.Length > 0) inputs.ChartTags[saved.Song.Id] = saved.ChartTags.ToHashSet();
            }
            IReadOnlyList<MixCandidate> fetched;
            using (diagnostics.Phase("candidates")) (fetched, _) = await CandidatesAsync(db, user, events, token, inputs: inputs, saved: captured.FillSongs);
            IReadOnlyDictionary<string, AudioMuseScores> features;
            using (diagnostics.Phase("features")) features = audioMuse == null || requested != null && !PersonalMixMoods.Kinds.Contains(requested.Kind)
                ? new Dictionary<string, AudioMuseScores>() : await audioMuse.MixFeaturesAsync(await ContextAsync(user, token),
                    fetched.Where(item => item.Song.IsLocal).Select(item => item.Song.Id), token);
            var styles = TopStyles(fetched).Take(4).ToArray();
            foreach (var kind in kinds.Where(kind => kind.StartsWith("daily-", StringComparison.Ordinal)))
                if (captured.Mixes[kind].CoverTag == null && kind[^1] - '1' < styles.Length)
                    captured.Mixes[kind] = captured.Mixes[kind] with { CoverTag = styles[kind[^1] - '1'] };
            inputs.Stage = "fill";
            using (diagnostics.Phase("fill")) fetched = await FillAsync(user, fetched, events, inputs, kinds, captured, features, token);
            if (readKinds.Count > 0)
            {
                var selected = kinds.SelectMany(kind => PersonalMixRules.Select(kind,
                    RecipePool(kind, captured.Mixes[kind].CoverTag, fetched, features, fetched), clock.UtcNow, []))
                    .Select(row => row.Song.Id).ToHashSet();
                await SaveFillAsync(user, fetched.OrderByDescending(row => selected.Contains(row.Song.Id)).ToArray(), readKinds, token, inputs.FreshSongs);
            }
            var exclusions = new Dictionary<string, int>(inputs.Excluded);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                inputs.Excluded = new(exclusions);
                inputs.Stage = "final";
                diagnostics.Count("save attempts");
                var (_, document, version) = await SnapshotAsync(owner, token);
                var revision = await InputRevisionAsync(db, owner, token);
                (events, history) = await HistoryAsync(db, user, token);
                var context = await ContextAsync(user, token);
                var hearts = (await new allstarr.Core.Favorites.FavoriteViewReader(factory).ReadAsync(context, token)).Where(item => item.Display.Kind == "song").ToArray();
                IReadOnlyList<MixCandidate> fresh;
                using (diagnostics.Phase("final rank")) fresh = await RankCandidatesAsync(fetched.Select(row => (row.Song, row.Source, row.ReleasedAt)).ToList(),
                    context, user, events, hearts, token, inputs, recount: true);
                IReadOnlyList<MixCandidate> candidates;
                inputs.Stage = "revalidate";
                using (diagnostics.Phase("revalidate")) candidates = await RevalidateAsync(user, fresh, token, inputs: inputs);
                if (dryRun && timeZoneId != null) document.TimeZoneId = timeZoneId.Trim();
                using (diagnostics.Phase("select")) GenerateKinds(document, captured, candidates, events, features, kinds, inputs);
                generated = document;
                using (diagnostics.Phase("save")) completed = await TrySaveAsync(user, document, version, revision, history, token, dryRun);
                if (completed)
                {
                    if (!dryRun)
                        foreach (var kind in kinds) clientEvents?.Publish(owner, ClientEvent.Mix(document.Mixes[kind].Id));
                    return document.Mixes.Values.ToArray();
                }
            }
            throw new InvalidOperationException("Listener choices changed while building this mix. Refresh again.");
        }
        finally
        {
            generations.Release();
            foreach (var kind in generatedKinds) diagnostics.Log(logger, kind, generated?.Mixes[kind].Evidence, completed);
        }
    }

    private async Task SaveFillAsync(UserRecord capturedUser, IReadOnlyList<MixCandidate>? rows, IEnumerable<string> readKinds, CancellationToken token,
        IReadOnlySet<string>? fresh = null)
    {
        await writes.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var user = await UserAsync(db, capturedUser.Id, token);
            if (Scope(user) != Scope(capturedUser)) throw new UnauthorizedAccessException();
            var document = await LoadAsync(db, user, token);
            foreach (var kind in readKinds) document.FillReadAt[kind] = clock.UtcNow;
            if (rows != null)
            {
                var old = document.FillSongs.ToDictionary(row => row.Song.Id);
                var kept = document.Mixes.Values.SelectMany(mix => mix.Songs).Select(item => item.Song.Id).ToHashSet();
                document.FillSongs = rows.Where(row => !row.Song.IsLocal || row.RelatedTo != null || row.ReleasedAt != null && row.ReleaseKey != null)
                    .OrderByDescending(row => kept.Contains(row.Song.Id)).Select(row => new MixFillSong(SafeSong(row.Song),
                        fresh?.Contains(row.Song.Id) == true ? clock.UtcNow : old.GetValueOrDefault(row.Song.Id)?.ReadAt ?? clock.UtcNow, row.Source, row.ProviderFilled,
                        row.Tags.Keys.ToArray(), row.ChartTags ?? [], row.RelatedTo, row.EvidenceSource, row.ReleasedAt, row.ReleaseKey))
                    .Concat(document.FillSongs).DistinctBy(row => row.Song.Id).Take(500).ToList();
            }
            await SaveAsync(db, user, document, token);
        }
        finally { writes.Release(); }
    }

    internal async Task GenerateJobAsync(DurableJobClaim claim, MixRefreshRequest request, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var job = await db.Jobs.AsNoTracking().SingleAsync(item => item.Id == claim.JobId, token);
        var key = job.IdempotencyKey;
        var scheduled = key.StartsWith("schedule:", StringComparison.Ordinal);
        var manual = key.StartsWith("mix-refresh:", StringComparison.Ordinal);
        var afterNewHistory = key.StartsWith("history-resolution:", StringComparison.Ordinal);
        await GenerateAsync(claim.OwnerUserId!.Value, request.MixId == Guid.Empty ? null : request.MixId,
            request.DryRun, request.TimeZoneId, token, scheduled: scheduled,
            allowProviderReads: (claim.AttemptNumber == 1 || job.LastErrorCode?.StartsWith("mix-source-read-", StringComparison.Ordinal) == true) &&
                (scheduled || manual || afterNewHistory), afterNewHistory: afterNewHistory);
    }

    private static string[] TopStyles(IReadOnlyList<MixCandidate> rows) => rows.Where(row => row.Plays > 0)
        .SelectMany(row => row.Tags.Keys.Select(tag => (Tag: tag, row.Plays))).GroupBy(item => item.Tag)
        .OrderByDescending(group => group.Sum(item => item.Plays)).ThenBy(group => group.Key, StringComparer.Ordinal)
        .Take(MixRecipes.TopStyles).Select(group => group.Key).ToArray();

    private IReadOnlyList<MixCandidate> RecipePool(string kind, string? style, IReadOnlyList<MixCandidate> rows,
        IReadOnlyDictionary<string, AudioMuseScores> features, IReadOnlyList<MixCandidate> ingredients)
    {
        if (PersonalMixMoods.Kinds.Contains(kind)) return PersonalMixMoods.Qualify(kind, rows, features);
        var styles = TopStyles(ingredients);
        var artists = ingredients.Where(row => row.Plays > 0).Select(row => row.Song.Artist).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var styledArtists = ingredients.Where(row => row.Plays > 0 && style != null && row.Tags.ContainsKey(style))
            .Select(row => row.Song.Artist).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return rows.Where(row => kind switch
        {
            "on-repeat" => row.RecentPlays > 0 || row.NinetyPlays > 0,
            "rewind" => row.Plays >= MixRecipes.For(kind).MinimumPlays && row.LastPlayed <= clock.UtcNow.AddMonths(-MixRecipes.For(kind).InactiveMonths),
            "discover-weekly" => row.RelatedTo != null && (!row.Tags.Keys.Except(row.ChartTags ?? []).Any() || row.Tags.Keys.Any(styles.Contains)) && !row.Heard && !row.Loved && row.Plays == 0,
            "release-radar" => row.ReleasedAt >= clock.UtcNow.AddDays(-PersonalMixRules.ReleaseDays) && row.ReleasedAt <= clock.UtcNow,
            _ when kind.StartsWith("daily-", StringComparison.Ordinal) => style != null && (row.Tags.ContainsKey(style) || row.Plays == 0 && !row.Heard &&
                styledArtists.Contains(row.Song.Artist)) &&
                (row.Plays > 0 || row.Loved || !row.Heard && artists.Contains(row.Song.Artist)),
            _ => false
        }).ToArray();
    }

    private void GenerateKinds(PersonalMixDocument document, PersonalMixDocument captured, IReadOnlyList<MixCandidate> candidates,
        ListeningEventRecord[] events, IReadOnlyDictionary<string, AudioMuseScores> features, string[] kinds, GenerationInputs inputs)
    {
        var completed = events.Where(item => item.State == ListeningEventState.Completed && item.ListenedAt <= clock.UtcNow).ToArray();
        var relatedSeeds = completed.GroupBy(play => play.Artist, StringComparer.OrdinalIgnoreCase).OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal).Take(MixRecipes.TopArtists).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var recentIds = RecentHistory(completed);
        var primary = completed.Count(item => item.SourceKind == "protocol" && recentIds.Contains(item.Id));
        var fallback = completed.Where(item => item.SourceKind != "protocol" && recentIds.Contains(item.Id)).ToArray();
        var historical = fallback.Length == 0 ? null : $"{fallback.Max(item => item.ListenedAt)?.AddDays(-30):dd MMM yyyy}–{fallback.Max(item => item.ListenedAt):dd MMM yyyy}";
        var kept = inputs.ReusePicks ? captured.Mixes["discover-weekly"].Songs.SelectMany(item => PersonalMixRules.IdentityKeys(item.Song)).ToHashSet() : [];
        var previous = document.DiscoverPicks.Where(item => item.Value >= clock.UtcNow.AddDays(-14) && !kept.Contains(item.Key)).Select(item => item.Key).ToHashSet();
        foreach (var key in document.DiscoverPicks.Where(item => item.Value < clock.UtcNow.AddDays(-14)).Select(item => item.Key).ToArray()) document.DiscoverPicks.Remove(key);
        foreach (var kind in kinds)
        {
            var mix = document.Mixes[kind];
            var style = mix.CoverTag ?? captured.Mixes[kind].CoverTag;
            var recipe = MixRecipes.For(kind, style);
            var removed = captured.Mixes[kind].Songs.Select(item => PersonalMixRules.Key(item.Song))
                .Except(mix.Songs.Select(item => PersonalMixRules.Key(item.Song))).ToHashSet(StringComparer.Ordinal);
            var pool = RecipePool(kind, style, candidates, features, candidates)
                .Where(item => kind != "release-radar" || inputs.ReleaseArtists.Contains(item.Song.Artist) ||
                    item.ReleaseKey != null && inputs.EligibleReleases.Contains(item.ReleaseKey))
                .Where(item => kind != "discover-weekly" || relatedSeeds.Contains(item.RelatedTo))
                .Where(item => !removed.Contains(PersonalMixRules.Key(item.Song)) &&
                    (kind != "discover-weekly" || !PersonalMixRules.IdentityKeys(item.Song).Any(previous.Contains))).ToArray();
            var selected = PersonalMixRules.Select(kind, pool, clock.UtcNow, previous);
            var mood = PersonalMixMoods.Kinds.Contains(kind);
            var ready = selected.Count >= (mood ? 25 : 1);
            var shortfall = selected.Count >= recipe.Target ? null : inputs.Playable.Length == 0
                ? "No music service is enabled; only your eligible music was available."
                : inputs.SharedReads >= MixRecipes.GenerationReads || inputs.RecipeReads.GetValueOrDefault(kind) >= MixRecipes.RecipeReads ? "No more songs were found within this mix’s service allowance."
                : inputs.Failures > 0 ? "Some music services did not respond; eligible songs from the other sources are kept."
                : "No more songs met this recipe's rules in the available music and services.";
            if (kind == "release-radar" && selected.Count == 0) shortfall = $"Your release inbox contained {inputs.InboxEntries} entries. No playable release track met this recipe and your listening policy.";
            var generation = mix.Generation + 1;
            var items = selected.Select(item => new PersonalMixItem(mix.Id, generation, SafeSong(item.Song), item.Source,
                mood ? PersonalMixMoods.Reason(kind, item, item.Song.IsLocal ? "your library" : SourceName(item.Song.ExternalProvider)) : kind switch
                {
                    "on-repeat" => item.RecentPlays > 0 ? $"Played {item.RecentPlays} times · " + (historical == null ? "last 30 days" : "live last 30 days and imported " + historical)
                        : $"Played {item.NinetyPlays} times · last 90 days",
                    "rewind" => $"Played {item.Plays} times, last in {item.LastPlayed:MMM yyyy}",
                    "release-radar" => $"New from {item.Song.Artist} · released {item.ReleasedAt:dd MMM yyyy}",
                    "discover-weekly" => $"Similar to {item.RelatedTo}" + (item.Tags.Keys.FirstOrDefault(TopStyles(candidates).Contains) is { } tag ? " · " + tag : "") + $" · via {SourceName(item.EvidenceSource)}",
                    _ => (item.Plays > 0 || item.Loved ? "Familiar" : "New to you") + " · " + style
                }, 0)).ToArray();
            var excluded = new Dictionary<string, int>(inputs.Excluded);
            excluded["Outside this recipe"] = candidates.Count - pool.Length;
            excluded["Artist, release, tier and size limits"] = pool.Length - selected.Count;
            if (kind == "release-radar")
                foreach (var entry in inputs.Diagnostics.Inbox)
                {
                    var final = candidates.Count(row => row.ReleaseKey != null && entry.Keys.Contains(row.ReleaseKey));
                    var qualified = pool.Count(row => row.ReleaseKey != null && entry.Keys.Contains(row.ReleaseKey));
                    entry.Counts["Final candidates"] = final;
                    entry.Counts["Outside recipe"] = final - qualified;
                    entry.Counts["Qualified"] = qualified;
                    entry.Counts["Shown"] = selected.Count(row => row.ReleaseKey != null && entry.Keys.Contains(row.ReleaseKey));
                    entry.Counts["Release or artist limits"] = qualified - entry.Counts["Shown"];
                }
            document.Mixes[kind] = mix with
            {
                Name = PersonalMixRules.Name(kind, style),
                Generation = generation,
                UpdatedAt = clock.UtcNow,
                Description = PersonalMixDescriptions.FromSelection(kind, style, selected, historical),
                CoverTag = style,
                RecipeVersion = recipe.Version,
                Readiness = new(ready, ready ? "Ready" : mood && inputs.Playable.Length == 0 ? PersonalMixMoods.Readiness(selected.Count).Message : shortfall ?? "No eligible songs yet."),
                Songs = items,
                Evidence = new(recipe, completed.Count(item => item.SourceKind == "protocol"), completed.Count(item => item.SourceKind != "protocol"),
                    completed.Select(item => item.ListenedAt).Min(), completed.Select(item => item.ListenedAt).Max(), kind == "on-repeat" ? historical : null,
                    kind == "on-repeat" ? primary : 0, kind == "on-repeat" ? fallback.Length : 0, inputs.Offered, pool.Length, selected.Count,
                    selected.Count(item => item.Song.IsLocal), selected.Count(item => !item.Song.IsLocal), excluded.Where(item => item.Value > 0).ToDictionary(),
                    inputs.Reads, inputs.Failures, shortfall, kind == "release-radar" ? inputs.InboxEntries : null, inputs.TagChartStatus)
            };
            if (kind == "discover-weekly") foreach (var key in selected.SelectMany(item => PersonalMixRules.IdentityKeys(item.Song))) document.DiscoverPicks[key] = clock.UtcNow;
        }
    }

    private string SourceName(string? source) => source != null && registry?.TryGet(source, out var descriptor) == true
        ? descriptor!.DisplayName : source switch { "Last.fm" or "lastfm" => "Last.fm", "deezer" => "Deezer", "qobuz" => "Qobuz", "apple-download" or "apple-musickit" => "Apple Music", _ => "your music service" };

    private static Song SafeSong(Song song) => new()
    {
        Id = song.Id,
        CanonicalRecordingId = song.CanonicalRecordingId,
        Title = song.Title,
        Artist = song.Artist,
        ArtistId = song.ArtistId,
        Artists = song.Artists,
        ArtistAliases = song.ArtistAliases,
        ArtistIds = song.ArtistIds,
        Album = song.Album,
        Duration = song.Duration,
        IsLocal = song.IsLocal,
        ExternalProvider = song.ExternalProvider,
        ExternalId = song.ExternalId,
        Isrc = song.Isrc,
        AlbumId = song.AlbumId,
        Track = song.Track,
        Genre = song.Genre,
        Genres = song.Genres,
        ExplicitContentLyrics = song.ExplicitContentLyrics
    };

    private async Task<IReadOnlyList<PersonalMix>> VisibleAsync(Guid owner, PersonalMixDocument document, CancellationToken token,
        bool preferLocalCopies = true)
    {
        var songs = document.Mixes.Values.SelectMany(mix => mix.Songs).Select(item => item.Song).ToArray();
        var blocked = await taste.BlockedSongsAsync(owner, songs, token);
        var policy = await policies.ResolveForUserAsync(owner, token);
        var visibleMixes = document.Mixes.Values.Select(mix =>
        {
            var visible = mix.Songs.Where(item => !blocked.Contains(item.Song) && policy.Includes(item.Song)).ToArray();
            var readiness = PersonalMixMoods.Kinds.Contains(mix.Kind) && mix.Generation > 0 && visible.Length < 25 && visible.Length < mix.Songs.Count
                ? new MixReadiness(false, mix.Evidence != null ? "Fewer than 25 songs remain under your current listening policy. Refresh to look for more." : PersonalMixMoods.Readiness(visible.Length).Message) : mix.Readiness;
            if (readiness.Ready && visible.Length == 0)
                readiness = new(false, "No playable songs are available for this mix yet. Refresh after more listening or new releases.");
            return mix with { Songs = visible, Readiness = readiness };
        }).ToArray();
        // Cards use the saved artwork identities. Resolve current local playback routes when opening the mix.
        return preferLocalCopies ? await PreferLocalCopiesAsync(owner, visibleMixes, token) : visibleMixes;
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
            clientEvents?.Publish(owner, ClientEvent.Mix(id));
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
            clientEvents?.Publish(owner, ClientEvent.Mix(id));
        }
        finally { writes.Release(); }
    }

    public async Task<IReadOnlyList<VirtualPlaylistReadModel>> PublishedAsync(ProtocolExecutionContext context, CancellationToken token,
        Guid? playlistId = null, bool summariesOnly = false)
    {
        if (context.Actor?.EffectiveUserId is not { } owner || !(bool)(await settings.GetAsync(PublishKey, token)).Value) return [];
        PersonalMixDocument document;
        await using var db = await factory.CreateDbContextAsync(token);
        await writes.WaitAsync(token);
        try
        {
            var user = await BrowseUserAsync(db, owner, token, context);
            if (user == null) return [];
            document = await LoadAsync(db, user, token, publishedOnly: true);
        }
        finally { writes.Release(); }
        document.Mixes = document.Mixes.Where(pair => pair.Value.Added &&
            (playlistId == null || pair.Value.Id == playlistId)).ToDictionary(pair => pair.Key, pair => pair.Value);
        var visible = summariesOnly ? document.Mixes.Values.ToArray() : await VisibleAsync(owner, document, token);
        var localIds = visible.SelectMany(mix => mix.Songs).Where(item => item.Song.IsLocal).Select(item => item.Song.Id).ToArray();
        var allowed = summariesOnly || localIds.Length == 0 ? [] : await LibraryTrackAccess.Query(db, context, await libraryAccess.ResolveAsync(context, token))
            .Where(item => localIds.Contains(item.BackendItemId)).Select(item => item.BackendItemId).ToArrayAsync(token);
        return visible.Where(mix => mix.Generation > 0 && mix.Added).Select(mix =>
            new VirtualPlaylistReadModel(PlaylistVirtualizationService.CreateProtocolId(mix.Id), mix.Id, mix.Id, mix.Name, mix.Description,
                null, "allstarr", mix.Kind, mix.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture), PlaylistLinkMode.Virtual,
                summariesOnly ? [] : mix.Songs.Where(item => !item.Song.IsLocal || allowed.Contains(item.Song.Id)).Select((item, index) => new VirtualPlaylistTrack(index, item.Song.Id, item.Song.Title, item.Song.Artist,
                    item.Song.Album, null, item.Song.Duration * 1000L, null, TrackMatchState.Accepted,
                    item.Song.ExternalProvider, item.Song.ExternalId, item.Song.IsLocal ? TrackRouteKind.Local : TrackRouteKind.External,
                    item.Song.ExternalProvider, item.Song.ExternalId)
                { PlaybackExplicitContentLyrics = item.Song.ExplicitContentLyrics, Reason = item.Reason }).ToArray())
            {
                BackendPublic = false,
                MixCover = MixCoverInput.From(mix),
                PlaylistKind = "Mix",
                Mix = new(mix.Kind, mix.Section, null, mix.UpdatedAt),
                SummaryTrackCount = summariesOnly ? mix.Songs.Count : null,
                SummaryDurationMilliseconds = summariesOnly ? mix.Songs.Sum(item => item.Song.Duration * 1000L) : null
            }).ToArray();
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
                if (row != null && row.TimeZoneId == document.TimeZoneId && row.CronExpression == PersonalMixRules.Cron(kind)) continue;
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

public sealed record MixRefreshRequest(Guid MixId, bool DryRun = false, string? TimeZoneId = null);

public sealed class PersonalMixJobHandler(PersonalMixService mixes) : IDurableJobHandler
{
    public string JobType => PersonalMixService.JobType;
    public async Task<DurableJobCompletion> ExecuteAsync(DurableJobExecutionContext context, CancellationToken cancellationToken)
    {
        if (context.Claim.OwnerUserId == null) return DurableJobCompletion.Failure("mix-owner-missing", "A linked listener is required.");
        var request = context.Claim.Payload.ValueKind == JsonValueKind.Object
            ? context.Claim.Payload.Deserialize<MixRefreshRequest>()! : new MixRefreshRequest(context.Claim.Payload.GetGuid());
        try
        {
            await mixes.GenerateJobAsync(context.Claim, request, cancellationToken);
            return DurableJobCompletion.Success();
        }
        catch (PlaylistSourceUnavailableException error)
        {
            return DurableJobCompletion.Retry("mix-source-read-" + error.Code, error.Message, error.Error?.RetryAfter);
        }
    }
}

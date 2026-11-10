using System.Text.Json;
using System.Diagnostics;
using allstarr.Core.Capabilities;
using allstarr.Services.Jellyfin;
using allstarr.Core.Matching;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Music;
using allstarr.Core.Operations;
using allstarr.Core.Routing;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace allstarr.Core.ClientApi;

public sealed class LyricsSearchReader(LyricsSearchIndex index, IDbContextFactory<AllstarrDbContext> factory,
    IBackendLibraryAccessResolver libraries, IEffectiveProviderPolicyResolver policies, IProtocolProviderGateway providers, IProviderRouter router, JellyfinProxyService jellyfin, RecordingKeptFiles kept, ILogger<LyricsSearchReader> logger,
    LyricsSearchChanges changes, IRuntimeSettingsChangeSignal settings)
{
    public Task<LyricsSearchResponse> ReadAsync(HttpContext http, ProtocolExecutionContext execution, string query, int limit)
        => ReadAsync(http, execution, query, limit, false);

    internal Task WarmAsync(ProtocolExecutionContext execution) => ReadAsync(new DefaultHttpContext(), execution, "", 1, true);

    private async Task<LyricsSearchResponse> ReadAsync(HttpContext http, ProtocolExecutionContext execution, string query, int limit, bool warmOnly)
    {
        using var matching = RequestTiming.Current?.Stage("match");
        var total = Stopwatch.StartNew();
        var stage = Stopwatch.StartNew();
        void Timed(string name)
        {
            logger.LogDebug("Lyrics search stage {Stage}: {ElapsedMs:F2} ms", name, stage.Elapsed.TotalMilliseconds);
            stage.Restart();
        }
        var token = execution.CancellationToken;
        var owner = execution.RequireActor().EffectiveUserId!.Value;
        var policy = execution.Policy ?? await policies.ResolveForUserAsync(owner, token);
        Timed("policy");
        var documents = await index.SnapshotAsync(execution);
        Timed("snapshot");
        if (documents.Count == 0) return new([], false);
        var access = documents.Any(document => document.Song.IsLocal)
            ? await libraries.ResolveAsync(execution, token) : BackendLibraryAccess.Unavailable;
        Timed("library-permissions");
        var cacheKey = (typeof(LyricsSearchReader), execution.Protocol, execution.BackendInstanceId,
            execution.VerifiedBackendPrincipalId, owner, execution.Principal!.IsAdministrator,
            JsonSerializer.Serialize(policy, ClientApiContract.JsonOptions),
            JsonSerializer.Serialize(access, ClientApiContract.JsonOptions), documents, changes.Version, settings.Version);
        var cached = index.ListenerCache.Get<Visibility>(cacheKey);
        RequestTiming.Current?.NoteCache(cached != null);
        await using var db = await factory.CreateDbContextAsync(token);
        if (cached == null)
        {
            var native = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (documents.Any(document => document.Song.IsLocal))
            {
                native = (await LibraryTrackAccess.Query(db, execution, access).Select(track => track.BackendItemId)
                    .Distinct().ToArrayAsync(token)).Select(NativeKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            Timed("native-access");
            var playable = documents.Any(document => !document.Song.IsLocal)
                ? (await providers.GetPlayableProviderOrderAsync(execution.RequireActor(), token)).ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            Timed("playable-providers");
            var canonicalIds = documents.Where(document => document.Song.ExternalProvider == "allstarr" && document.RecordingId.HasValue)
                .Select(document => document.RecordingId!.Value).Distinct().ToArray();
            var playableRecordings = (await db.ProviderTrackIdentities.AsNoTracking().Where(link => canonicalIds.Contains(link.CanonicalRecordingId) &&
                link.ResourceKind == ProviderResourceKind.Track && link.Verification == ProviderIdentityVerification.Verified &&
                link.VerificationMethod != "automatic-suggestion" && link.VerificationMethod != "source-snapshot-hash" &&
                link.VerificationMethod != CatalogIdentityPolicy.SupersededVerificationMethod &&
                link.VerificationMethod != ManualTrackAuthorityPolicy.ReleasedProviderVerificationMethod &&
                link.VerificationMethod != ManualTrackAuthorityPolicy.ReplacedProviderVerificationMethod && playable.Contains(link.ProviderId) &&
                (link.Scope == ProviderIdentityScope.Catalog || db.ProviderAccounts.Any(account => account.Id == link.ProviderAccountId && account.Enabled &&
                    (account.OwnerUserId == null || account.OwnerUserId == owner)))).Select(link => link.CanonicalRecordingId).Distinct().ToArrayAsync(token)).ToHashSet();
            Timed("playable-recordings");
            var availableSources = new Dictionary<string, string>(StringComparer.Ordinal) { ["backend"] = "public", ["kept"] = "public" };
            foreach (var source in documents.Select(document => LyricsVersions.ProviderId(document.Source)).Distinct(StringComparer.Ordinal)
                         .Where(source => source is not ("backend" or "kept") && policy.IsEnabled(source)))
            {
                var plan = await router.PlanAsync<IProviderLyricsCapability>(LyricsSearchIndex.SourceRequest(execution, source));
                if (plan.Candidates.FirstOrDefault() is { } candidate)
                    availableSources[source] = LyricsSearchIndex.AccountProvenance(candidate.Context.Account);
            }
            Timed("source-plans");
            var visible = documents.Where(document => availableSources.TryGetValue(LyricsVersions.ProviderId(document.Source), out var provenance) && provenance == document.Provenance && policy.Includes(document.Song) &&
                (document.Song.IsLocal ? native.Contains(NativeKey(document.ItemId)) :
                    (document.Owner == owner || document.PublicCatalog && document.SharedSource) && document.Song.ExternalProvider is { } provider && (provider == "allstarr" && document.RecordingId.HasValue || policy.IsEnabled(provider) && playable.Contains(provider)))).ToArray();
            Timed("visible-documents");
            cached = new(visible, playableRecordings, visible.GroupBy(document => document.ItemId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal));
            index.ListenerCache.Set(cacheKey, cached, new MemoryCacheEntryOptions
            {
                Size = Math.Max(1, visible.Length + playableRecordings.Count),
                AbsoluteExpirationRelativeToNow = LyricsSearchIndex.ListenerViewLifetime
            });
        }
        else Timed("visibility-cache");
        if (warmOnly) return new([], false);
        var visibleDocuments = cached.Documents;
        var visibleByItem = cached.ByItem;
        var rows = await db.RecordingLyricsChoices.AsNoTracking().Where(choice => choice.RevokedAt == null &&
            (choice.OwnerUserId == owner || choice.OwnerUserId == null)).ToArrayAsync(token);
        var choices = rows.GroupBy(choice => choice.CanonicalRecordingId).ToDictionary(group => group.Key,
            group => group.OrderByDescending(choice => choice.OwnerUserId == owner).First().SourceId);
        var automatic = await db.RecordingLyricsRefreshes.AsNoTracking().Where(item => item.OwnerUserId == owner && item.SourceId != "")
            .Select(item => new { item.CanonicalRecordingId, item.SourceId }).ToArrayAsync(token);
        foreach (var choice in automatic)
            if (policy.IsAutomatic(LyricsVersions.ProviderId(choice.SourceId))) choices.TryAdd(choice.CanonicalRecordingId, choice.SourceId);
        Timed("choices");
        var take = Math.Max(limit + 1, 32);
        while (true)
        {
            var result = await index.SearchAsync(execution, query, take, visibleDocuments, policy, choices);
            Timed("index");
            var nativeHits = result.Results.Where(hit => !hit.IsExternal).ToArray();
            var items = new List<JsonElement>();
            foreach (var batch in nativeHits.Chunk(100))
            {
                var (body, status) = await jellyfin.GetJsonAsync("Items", new()
                {
                    ["ids"] = string.Join(',', batch.Select(hit => hit.ItemId)),
                    ["UserId"] = execution.VerifiedBackendPrincipalId,
                    ["Fields"] = "Album,AlbumId,AlbumArtist,Artists",
                    ["IncludeItemTypes"] = "Audio"
                }, http.Request.Headers, token);
                using (body)
                {
                    if (status is 400 or 401 or 403 or 404) continue;
                    if (status != 200 || body == null) throw new HttpRequestException("Library visibility is unavailable.");
                    if (body.RootElement.TryGetProperty("Items", out var values) && values.ValueKind == JsonValueKind.Array)
                        items.AddRange(values.EnumerateArray().Where(item => item.TryGetProperty("Type", out var type) && type.GetString() == "Audio").Select(item => item.Clone()));
                }
            }
            Timed("backend-items");
            var needed = result.Results.Where(hit => hit.IsExternal)
                .Select(hit => visibleByItem[hit.ItemId]).Where(document => document.Song.ExternalProvider == "allstarr" &&
                    document.RecordingId.HasValue && !cached.PlayableRecordings.Contains(document.RecordingId.Value))
                .Select(document => document.RecordingId!.Value).Distinct().ToArray();
            var keptRecordings = new HashSet<Guid>();
            var uncheckedRecordings = new List<Guid>();
            foreach (var recording in needed)
            {
                if (index.ListenerCache.TryGetValue((cacheKey, recording), out bool available))
                {
                    if (available) keptRecordings.Add(recording);
                }
                else uncheckedRecordings.Add(recording);
            }
            if (uncheckedRecordings.Count > 0)
            {
                var available = await kept.FindAvailableAsync(execution.RequireActor(), uncheckedRecordings, token);
                foreach (var recording in uncheckedRecordings)
                {
                    index.ListenerCache.Set((cacheKey, recording), available.Contains(recording), new MemoryCacheEntryOptions
                    {
                        Size = 1,
                        AbsoluteExpirationRelativeToNow = LyricsSearchIndex.ListenerViewLifetime
                    });
                    if (available.Contains(recording)) keptRecordings.Add(recording);
                }
            }
            Timed("kept-batch");
            var accessible = new List<LyricsSearchResult>();
            foreach (var hit in result.Results)
            {
                if (hit.IsExternal)
                {
                    var document = visibleByItem[hit.ItemId];
                    if (document.Song.ExternalProvider != "allstarr" || document.RecordingId is { } recording &&
                        (cached.PlayableRecordings.Contains(recording) || keptRecordings.Contains(recording)))
                        accessible.Add(hit);
                    continue;
                }
                var item = items.FirstOrDefault(item => item.TryGetProperty("Id", out var id) && ItemLyricsReader.SameNativeItem(id.GetString(), hit.ItemId));
                if (item.ValueKind != JsonValueKind.Object) continue;
                accessible.Add(hit with { AlbumId = item.TryGetProperty("AlbumId", out var album) && album.ValueKind == JsonValueKind.String ? album.GetString() : null });
            }
            Timed("access-filter");
            if (accessible.Count > limit || !result.Truncated)
            {
                logger.LogDebug("Lyrics search total: {ElapsedMs:F2} ms; returned {Count}", total.Elapsed.TotalMilliseconds, Math.Min(limit, accessible.Count));
                return new(accessible.Take(limit).ToArray(), accessible.Count > limit);
            }
            take = Math.Min(visibleDocuments.Count, take * 2);
        }
    }

    private sealed record Visibility(IReadOnlyList<IndexedLyricsDocument> Documents, IReadOnlySet<Guid> PlayableRecordings,
        IReadOnlyDictionary<string, IndexedLyricsDocument> ByItem);

    private static string NativeKey(string id) => Guid.TryParse(id, out var native) ? native.ToString("N") : id;
}

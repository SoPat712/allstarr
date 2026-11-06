using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Services.Jellyfin;
using allstarr.Core.Matching;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Music;
using allstarr.Core.Routing;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.ClientApi;

public sealed class LyricsSearchReader(LyricsSearchIndex index, IDbContextFactory<AllstarrDbContext> factory,
    IBackendLibraryAccessResolver libraries, IEffectiveProviderPolicyResolver policies, IProtocolProviderGateway providers, IProviderRouter router, JellyfinProxyService jellyfin, RecordingKeptFiles kept)
{
    public async Task<LyricsSearchResponse> ReadAsync(HttpContext http, ProtocolExecutionContext execution, string query, int limit)
    {
        var token = execution.CancellationToken;
        var owner = execution.RequireActor().EffectiveUserId!.Value;
        var policy = execution.Policy ?? await policies.ResolveForUserAsync(owner, token);
        var documents = await index.SnapshotAsync(execution);
        if (documents.Count == 0) return new([], false);
        await using var db = await factory.CreateDbContextAsync(token);
        var native = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (documents.Any(document => document.Song.IsLocal))
        {
            var access = await libraries.ResolveAsync(execution, token);
            native = (await LibraryTrackAccess.Query(db, execution, access).Select(track => track.BackendItemId)
                .Distinct().ToArrayAsync(token)).Select(NativeKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        var playable = documents.Any(document => !document.Song.IsLocal)
            ? (await providers.GetPlayableProviderOrderAsync(execution.RequireActor(), token)).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
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
        var availableSources = new Dictionary<string, string>(StringComparer.Ordinal) { ["backend"] = "public", ["kept"] = "public" };
        foreach (var source in documents.Select(document => LyricsVersions.ProviderId(document.Source)).Distinct(StringComparer.Ordinal)
                     .Where(source => source is not ("backend" or "kept") && policy.IsEnabled(source)))
        {
            var plan = await router.PlanAsync<IProviderLyricsCapability>(LyricsSearchIndex.SourceRequest(execution, source));
            if (plan.Candidates.FirstOrDefault() is { } candidate)
                availableSources[source] = LyricsSearchIndex.AccountProvenance(candidate.Context.Account);
        }
        var visible = documents.Where(document => availableSources.TryGetValue(LyricsVersions.ProviderId(document.Source), out var provenance) && provenance == document.Provenance && policy.Includes(document.Song) &&
            (document.Song.IsLocal ? native.Contains(NativeKey(document.ItemId)) :
                (document.Owner == owner || document.PublicCatalog && document.SharedSource) && document.Song.ExternalProvider is { } provider && (provider == "allstarr" && document.RecordingId.HasValue || policy.IsEnabled(provider) && playable.Contains(provider)))).ToArray();
        var rows = await db.RecordingLyricsChoices.AsNoTracking().Where(choice => choice.RevokedAt == null &&
            (choice.OwnerUserId == owner || choice.OwnerUserId == null)).ToArrayAsync(token);
        var choices = rows.GroupBy(choice => choice.CanonicalRecordingId).ToDictionary(group => group.Key,
            group => group.OrderByDescending(choice => choice.OwnerUserId == owner).First().SourceId);
        var automatic = await db.RecordingLyricsRefreshes.AsNoTracking().Where(item => item.OwnerUserId == owner && item.SourceId != "")
            .Select(item => new { item.CanonicalRecordingId, item.SourceId }).ToArrayAsync(token);
        foreach (var choice in automatic)
            if (policy.IsAutomatic(LyricsVersions.ProviderId(choice.SourceId))) choices.TryAdd(choice.CanonicalRecordingId, choice.SourceId);
        var take = Math.Max(limit + 1, 32);
        while (true)
        {
            var result = await index.SearchAsync(execution, query, take, visible, policy, choices);
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
            var accessible = new List<LyricsSearchResult>();
            foreach (var hit in result.Results)
            {
                if (hit.IsExternal)
                {
                    var document = visible.First(candidate => candidate.ItemId == hit.ItemId);
                    if (document.Song.ExternalProvider != "allstarr" || document.RecordingId is { } recording &&
                        (playableRecordings.Contains(recording) || await kept.FindAsync(execution.RequireActor(), recording, token) != null))
                        accessible.Add(hit);
                    continue;
                }
                var item = items.FirstOrDefault(item => item.TryGetProperty("Id", out var id) && ItemLyricsReader.SameNativeItem(id.GetString(), hit.ItemId));
                if (item.ValueKind != JsonValueKind.Object) continue;
                accessible.Add(hit with { AlbumId = item.TryGetProperty("AlbumId", out var album) && album.ValueKind == JsonValueKind.String ? album.GetString() : null });
            }
            if (accessible.Count > limit || !result.Truncated)
                return new(accessible.Take(limit).ToArray(), accessible.Count > limit);
            take = Math.Min(visible.Length, take * 2);
        }
    }

    private static string NativeKey(string id) => Guid.TryParse(id, out var native) ? native.ToString("N") : id;
}

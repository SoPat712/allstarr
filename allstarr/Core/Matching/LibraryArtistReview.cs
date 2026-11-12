using System.Text.Json;
using System.Text.Json.Serialization;
using allstarr.Core.Capabilities;
using allstarr.Core.Protocols;
using allstarr.Services.Common;
using Microsoft.AspNetCore.WebUtilities;

namespace allstarr.Core.Matching;

public sealed record ArtistReviewLibraryArtist(string Id, string Name, int SongCount, string? ImageUrl);
public sealed record ArtistReviewService(
    [property: JsonPropertyName("providerId")] string ProviderId,
    [property: JsonPropertyName("providerName")] string ProviderName,
    [property: JsonPropertyName("artistId")] string ArtistId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("scope")] string? Scope,
    [property: JsonPropertyName("sharedSongs")] int? SharedSongs,
    [property: JsonPropertyName("sharedSongTitles"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? SharedSongTitles = null,
    [property: JsonPropertyName("checkedAt"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? CheckedAt = null,
    [property: JsonPropertyName("reason"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason = null);
public sealed record ArtistReviewRow(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("songCount")] int SongCount,
    [property: JsonPropertyName("imageUrl")] string? ImageUrl,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("services")] IReadOnlyList<ArtistReviewService> Services)
{
    public static string StatusFor(IEnumerable<string> states)
    {
        var all = states.ToHashSet(StringComparer.Ordinal);
        return all.Contains("candidate") ? "needs-look" : all.Contains("same") ? "decided" :
            all.Contains("not-same") ? "apart" : all.Contains("songs") ? "linked" : "library-only";
    }
}
public sealed record ArtistReviewCounts(
    [property: JsonPropertyName("needsLook")] int NeedsLook,
    [property: JsonPropertyName("linked")] int Linked,
    [property: JsonPropertyName("decided")] int Decided,
    [property: JsonPropertyName("apart")] int Apart,
    [property: JsonPropertyName("all")] int All);
public sealed record ArtistReviewPage(
    [property: JsonPropertyName("counts")] ArtistReviewCounts Counts,
    [property: JsonPropertyName("artists")] IReadOnlyList<ArtistReviewRow> Artists,
    [property: JsonPropertyName("nextCursor")] string? NextCursor);

public sealed class LibraryArtistReview(LibraryArtistIndex index, IProviderRegistry registry, IBackendLibraryAccessResolver access,
    IBackendLibraryPermissionSource permissions, IHttpClientFactory clients, IApplicationCache cache)
{
    private sealed record Cursor(string Scope, int Songs, string Id);

    public async Task<ArtistReviewPage> ListAsync(ProtocolExecutionContext context, string filter, string? q, string? cursor, int limit,
        CancellationToken token)
    {
        if (filter is not ("needs-look" or "linked" or "decided" or "apart" or "all") || limit is < 1 or > 100 || q?.Length > 300)
            throw new ArgumentException("The artist filter, search, or limit is invalid.");
        var library = await ReadLibraryAsync(context, token);
        var query = FuzzyMatcher.NormalizeForMatching(q ?? "");
        var scope = CanonicalCatalogKeys.Hash(JsonSerializer.Serialize(new
        {
            context.Protocol,
            context.BackendInstanceId,
            context.VerifiedBackendPrincipalId,
            filter,
            query
        }));
        Cursor? after = null;
        if (cursor != null)
        {
            try { after = JsonSerializer.Deserialize<Cursor>(WebEncoders.Base64UrlDecode(cursor)); }
            catch (Exception error) when (error is FormatException or JsonException) { throw new ArgumentException("The artist cursor is invalid."); }
            if (after == null || after.Scope != scope) throw new ArgumentException("The artist cursor is invalid.");
        }
        var owner = context.RequireActor().EffectiveUserId!.Value;
        var selected = library.Where(artist => FuzzyMatcher.NormalizeForMatching(artist.Name).Contains(query, StringComparison.Ordinal)).ToArray();
        var parts = await index.ReadReviewAsync(owner, selected.Select(artist => artist.Name).ToArray(), token);
        var rows = selected.Select(artist => Shape(artist, parts.GetValueOrDefault(artist.Name), false)).ToArray();
        var counts = new ArtistReviewCounts(rows.Count(row => row.Status == "needs-look"), rows.Count(row => row.Status == "linked"),
            rows.Count(row => row.Status == "decided"), rows.Count(row => row.Status == "apart"), rows.Length);
        var page = rows.Where(row => (filter == "all" || row.Status == filter) && (after == null || row.SongCount < after.Songs ||
                row.SongCount == after.Songs && string.CompareOrdinal(row.Id, after.Id) > 0))
            .OrderByDescending(row => row.SongCount).ThenBy(row => row.Id, StringComparer.Ordinal).Take(limit + 1).ToArray();
        var shown = page.Take(limit).ToArray();
        var details = await index.ReadReviewAsync(owner, shown.Select(row => row.Name).ToArray(), token, detail: true);
        return new(counts, shown.Select(row => Shape(selected.First(artist => artist.Id == row.Id), details.GetValueOrDefault(row.Name), false)).ToArray(),
            page.Length <= limit ? null : WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new Cursor(scope, shown[^1].SongCount, shown[^1].Id))));
    }

    public async Task<ArtistReviewRow?> DetailAsync(ProtocolExecutionContext context, string id, CancellationToken token)
    {
        var artist = (await ReadLibraryAsync(context, token)).FirstOrDefault(artist => artist.Id == id);
        if (artist == null) return null;
        var parts = await index.ReadReviewAsync(context.RequireActor().EffectiveUserId!.Value, [artist.Name], token, detail: true);
        return Shape(artist, parts.GetValueOrDefault(artist.Name), true);
    }

    private ArtistReviewRow Shape(ArtistReviewLibraryArtist artist, LibraryArtistReviewParts? parts, bool detail)
    {
        parts ??= new([], [], [], [], null);
        var keys = parts.Songs.Select(link => (link.ProviderId, link.ArtistId)).Concat(parts.Fixes.Select(fix => (fix.ProviderId, fix.ArtistId)))
            .Concat(parts.Candidates.Select(candidate => (candidate.ProviderId, candidate.ArtistId)))
            .Concat(detail ? parts.Evidence.Select(item => (item.ProviderId, item.ArtistId)) : []).Distinct();
        var services = keys.Select(key =>
        {
            var fix = parts.Fixes.FirstOrDefault(fix => fix.ProviderId == key.ProviderId && fix.ArtistId == key.ArtistId);
            var songs = parts.Songs.Any(link => link.ProviderId == key.ProviderId && link.ArtistId == key.ArtistId);
            var evidence = parts.Evidence.Concat(parts.Candidates).FirstOrDefault(item => item.ProviderId == key.ProviderId && item.ArtistId == key.ArtistId);
            var state = fix != null ? fix.Same ? "same" : "not-same" : songs ? "songs" : "candidate";
            return new ArtistReviewService(key.ProviderId, registry.TryGet(key.ProviderId, out var provider) ? provider!.DisplayName : key.ProviderId,
                key.ArtistId, evidence?.Name ?? artist.Name, state, fix == null ? null : fix.Household ? "household" : "you",
                evidence?.SharedSongs ?? (songs ? null : 0), detail && songs ? evidence?.SongTitles ?? [] : null,
                detail ? parts.CheckedAt : null, detail && !(fix?.Same ?? songs) ? fix != null ? "Marked as not the same artist" :
                    evidence?.Reason ?? "same name, no shared songs" : null);
        }).ToArray();
        // Song evidence below the threshold does not create a review candidate.
        var states = services.Where(service => service.State != "candidate" || parts.Candidates.Any(candidate =>
            candidate.ProviderId == service.ProviderId && candidate.ArtistId == service.ArtistId)).Select(service => service.State);
        return new(artist.Id, artist.Name, artist.SongCount, artist.ImageUrl, ArtistReviewRow.StatusFor(states), services);
    }

    public async Task<IReadOnlyList<ArtistReviewLibraryArtist>> ReadLibraryAsync(ProtocolExecutionContext context, CancellationToken token)
    {
        var allowed = await access.ResolveAsync(context, token);
        if (!allowed.Succeeded) throw new BackendLibraryAccessException(allowed);
        var key = "artist-review:library:" + CanonicalCatalogKeys.Hash(JsonSerializer.Serialize(new
        {
            context.Protocol,
            context.BackendInstanceId,
            context.VerifiedBackendPrincipalId,
            allowed.LibraryIds
        }));
        if (await cache.GetAsync<ArtistReviewLibraryArtist[]>(key) is { } saved) return saved;
        using var authentication = await permissions.CreateRequestAsync(context, token) ?? throw new UnauthorizedAccessException();
        var artists = new List<ArtistReviewLibraryArtist>();
        foreach (var library in allowed.LibraryIds)
            for (var offset = 0; ; offset += 200)
            {
                var jellyfin = context.Protocol == ProtocolKind.Jellyfin;
                var parameters = jellyfin ? $"UserId={Uri.EscapeDataString(context.VerifiedBackendPrincipalId)}&ParentId={Uri.EscapeDataString(library)}&Recursive=true&Limit=200&StartIndex={offset}&Fields=ItemCounts,ImageTags&SortBy=SortName" :
                    $"musicFolderId={Uri.EscapeDataString(library)}&query=&artistCount=200&artistOffset={offset}&songCount=0&albumCount=0";
                using var request = new HttpRequestMessage(authentication.Method,
                    new Uri(new Uri(authentication.RequestUri!, "."), jellyfin ? "Artists?" + parameters : "search3.view"));
                foreach (var header in authentication.Headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                if (!jellyfin) request.Content = new StringContent(await authentication.Content!.ReadAsStringAsync(token) + "&" + parameters,
                    System.Text.Encoding.UTF8, "application/x-www-form-urlencoded");
                using var client = clients.CreateClient(JellyfinMatchLibraryClient.HttpClientName);
                using var response = await client.SendAsync(request, token);
                response.EnsureSuccessStatusCode();
                using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
                var root = document.RootElement;
                if (!jellyfin)
                {
                    if (root.TryGetProperty("subsonic-response", out var envelope)) root = envelope;
                    if (Text(root, "status") == "failed") throw new HttpRequestException("Library artists are unavailable.");
                    root = root.TryGetProperty("searchResult3", out var search) ? search : default;
                }
                var items = root.ValueKind == JsonValueKind.Object && root.TryGetProperty(jellyfin ? "Items" : "artist", out var array)
                    ? array.EnumerateArray().ToArray() : [];
                foreach (var item in items)
                {
                    var id = Text(item, jellyfin ? "Id" : "id");
                    var name = Text(item, jellyfin ? "Name" : "name");
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) continue;
                    var count = item.TryGetProperty(jellyfin ? "SongCount" : "songCount", out var number) &&
                        number.ValueKind == JsonValueKind.Number && number.TryGetInt32(out var songs) ? songs : -1;
                    var image = Text(item, "artistImageUrl");
                    artists.Add(new(id, name, count, Uri.TryCreate(image, UriKind.Absolute, out var url) && url.Scheme == "https" ? image : null));
                }
                if (items.Length < 200) break;
            }
        var result = artists.GroupBy(artist => artist.Id, StringComparer.Ordinal).Select(group => group.MaxBy(artist => artist.SongCount)!).ToArray();
        if (result.Any(artist => artist.SongCount < 0))
        {
            var counts = await index.SongCountsAsync(context.RequireActor().EffectiveUserId!.Value,
                result.Where(artist => artist.SongCount < 0).Select(artist => artist.Name).ToArray(), token);
            result = result.Select(artist => artist.SongCount < 0 ? artist with { SongCount = counts.GetValueOrDefault(artist.Name) } : artist).ToArray();
        }
        await cache.SetAsync(key, result, TimeSpan.FromMinutes(2));
        return result;
    }

    private static string? Text(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

using System.ComponentModel;
using System.Text.Json.Serialization;
using allstarr.Core.Capabilities;
using allstarr.Core.ClientApi;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Services.Local;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.ClientApiV2;

public sealed record V2MixRow(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("aliases")] IReadOnlyList<string> Aliases,
    [property: JsonPropertyName("libraryItemId"), Description("The media server item when the song is in the library.")] string? LibraryItemId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("artists")] IReadOnlyList<V2Ref> Artists,
    [property: JsonPropertyName("album")] V2AlbumRef? Album,
    [property: JsonPropertyName("durationMs")] long? DurationMs,
    [property: JsonPropertyName("artwork")] V2Artwork? Artwork,
    [property: JsonPropertyName("isExternal")] bool IsExternal,
    [property: JsonPropertyName("provider")] V2Provider? Provider);

public sealed record V2MixRows(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset? UpdatedAt,
    [property: JsonPropertyName("rows")] IReadOnlyList<V2MixRow> Rows);

/// <summary>A song's ids as a full track read gives them.</summary>
public sealed record V2RowId(string Id, IReadOnlyList<string> Aliases, string? LibraryItemId);

/// <summary>
/// Works out the track id a full read gives each song without reading the songs: its recording when one is known, the
/// library copy of a service song the listener owns, and otherwise the song's own id. Light rows use it so their ids
/// match the tracks an app reads later.
/// </summary>
public sealed class V2RowIds(ITrackIdentityService identities, ILibraryIndexService libraryIndex, IProviderRegistry registry,
    IDbContextFactory<AllstarrDbContext> factory)
{
    public async Task<IReadOnlyDictionary<string, V2RowId>> ResolveAsync(ProtocolExecutionContext execution, IEnumerable<string> ids)
    {
        var token = execution.CancellationToken;
        var sources = ids.Distinct(StringComparer.Ordinal).ToArray();
        var external = sources.Where(IsExternal).ToArray();
        IReadOnlyDictionary<string, CanonicalRecordingIdentity?> known = external.Length == 0
            ? new Dictionary<string, CanonicalRecordingIdentity?>()
            : await identities.ResolveRecordingsAsync(execution.RequireActor(), external, token);
        // As the full read does: a song from a service that can't play it reads as its recording, and a service song that is
        // also in the library reads as the library copy.
        var targets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in external)
            if (known.GetValueOrDefault(id) is { } recording &&
                RecordingOnlySongs.IsRecordingOnly(registry, LocalLibraryService.ParseExternalResource(id).provider))
                targets[id] = CanonicalRecordingRecord.DefaultPublicId(recording.Id);
        var lookups = external.Where(id => known.GetValueOrDefault(id) != null).Select(id =>
        {
            var parsed = LocalLibraryService.ParseExternalResource(id);
            var identity = known[id]!;
            return new LibraryRecordingLookup(id, identity.Id, identity.Isrc, registry.ResolveProviderId(parsed.provider!), parsed.externalId);
        }).ToArray();
        if (lookups.Length > 0)
            foreach (var pair in await libraryIndex.FindKnownRecordingTracksAsync(execution, lookups, token)) targets[pair.Key] = pair.Value;
        var native = sources.Select(id => targets.GetValueOrDefault(id) ?? id).Where(id => !IsExternal(id)).Distinct(StringComparer.Ordinal).ToArray();
        var stored = await LibraryRecordingsAsync(execution, native, token);
        var result = new Dictionary<string, V2RowId>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            var target = targets.GetValueOrDefault(source) ?? source;
            var inLibrary = !IsExternal(target);
            var recording = (inLibrary && stored.TryGetValue(NativeKey(target), out var owned) ? owned : null) ?? known.GetValueOrDefault(source)?.Id;
            var id = recording is { } value ? V2Ids.Track(value) : V2Ids.TrackSource(target);
            var aliases = new[] { source, target, recording is { } canonical ? CanonicalRecordingRecord.DefaultPublicId(canonical) : null }
                .OfType<string>().Where(alias => alias != id).Distinct(StringComparer.Ordinal).ToArray();
            result[source] = new(id, aliases, inLibrary ? target : null);
        }
        return result;
    }

    private async Task<Dictionary<string, Guid?>> LibraryRecordingsAsync(ProtocolExecutionContext execution, string[] ids, CancellationToken token)
    {
        if (ids.Length == 0 || execution.Actor?.EffectiveUserId is not { } owner) return [];
        var lookupIds = ids.SelectMany(id => Guid.TryParse(id, out var parsed) ? new[] { id, parsed.ToString("N"), parsed.ToString("D") } : [id])
            .Distinct(StringComparer.Ordinal).ToArray();
        var protocol = execution.Protocol.ToString().ToLowerInvariant();
        await using var db = await factory.CreateDbContextAsync(token);
        var rows = await db.LibraryTracks.AsNoTracking().Where(item => item.OwnerUserId == owner && item.Protocol == protocol &&
                item.BackendInstanceId == execution.BackendInstanceId && lookupIds.Contains(item.BackendItemId))
            .Select(item => new { item.BackendItemId, item.CanonicalRecordingId }).ToArrayAsync(token);
        return rows.GroupBy(item => NativeKey(item.BackendItemId)).ToDictionary(group => group.Key,
            group => group.Select(item => item.CanonicalRecordingId).FirstOrDefault(item => item != null), StringComparer.Ordinal);
    }

    internal static bool IsExternal(string id) => id.StartsWith("ext-", StringComparison.Ordinal);

    private static string NativeKey(string id) => Guid.TryParse(id, out var parsed) ? parsed.ToString("N") : id;
}

/// <summary>A mix's songs as light rows read from the stored mix, for apps that read listener facts separately.</summary>
public sealed class V2MixRowsReader(MixReader mixes, V2RowIds rowIds, LibraryProviderResolver libraryProviders)
{
    public async Task<V2MixRows> ReadAsync(HttpContext http, ProtocolExecutionContext execution, Guid mixId)
    {
        var mix = await mixes.ItemsAsync(http, execution, mixId);
        var ids = await rowIds.ResolveAsync(execution, mix.Items.Select(item => item.ItemId));
        var libraryProvider = ids.Values.Any(id => id.LibraryItemId != null) ? await libraryProviders.ResolveAsync(http, execution) : null;
        var rows = new List<V2MixRow>();
        foreach (var item in mix.Items)
        {
            var row = ids[item.ItemId];
            var inLibrary = row.LibraryItemId != null;
            var provider = inLibrary ? libraryProvider : item.Provider;
            rows.Add(new(row.Id, row.Aliases, row.LibraryItemId, item.Title,
                string.IsNullOrWhiteSpace(item.Artist) ? [] : [new(Valid(item.ArtistId, V2IdKind.Artist) ? V2Ids.Artist(item.ArtistId!) : null, item.Artist)],
                string.IsNullOrWhiteSpace(item.Album) ? null : new(Valid(item.AlbumId, V2IdKind.Album) ? V2Ids.Album(item.AlbumId!) : null, item.Album),
                item.DurationMs, Artwork(item.ArtworkUrl), !inLibrary, provider == null ? null : Provider(provider)));
        }
        return new(V2Ids.Mix(mixId), mix.UpdatedAt, rows);
    }

    // A stored cover is the media server's image address for an item; the native form names the same item and tag.
    private static V2Artwork? Artwork(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("/Items/", StringComparison.Ordinal)) return null;
        var path = url.Split('?', 2);
        var parts = path[0].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4 || parts[2] != "Images" || parts[3] != "Primary") return null;
        var tag = path.Length > 1 ? Tag(path[1]) : null;
        return V2ArtworkUrls.For(Uri.UnescapeDataString(parts[1]), tag);
    }

    private static string? Tag(string query) =>
        Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(query).TryGetValue("tag", out var tag) ? tag.ToString() : null;

    private static V2Provider Provider(ItemDetailsProvider source)
    {
        var provider = V2ProviderUrls.For(source);
        return new(provider.Id, provider.Name, provider.IconUrl, provider.GlyphUrl);
    }

    private static bool Valid(string? id, V2IdKind kind) => !string.IsNullOrWhiteSpace(id) && V2Ids.IsValidSource(id, kind);
}

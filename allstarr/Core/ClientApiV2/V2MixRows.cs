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

/// <summary>
/// A mix's songs as light rows read from the stored mix, for apps that read listener facts separately. Each row carries
/// the track id a full read gives the same song: its recording when one is known, the library copy of a service song the
/// listener owns, and otherwise the song's own id.
/// </summary>
public sealed class V2MixRowsReader(MixReader mixes, ITrackIdentityService identities, ILibraryIndexService libraryIndex,
    IProviderRegistry registry, IDbContextFactory<AllstarrDbContext> factory, LibraryProviderResolver libraryProviders)
{
    public async Task<V2MixRows> ReadAsync(HttpContext http, ProtocolExecutionContext execution, Guid mixId)
    {
        var token = execution.CancellationToken;
        var mix = await mixes.ItemsAsync(http, execution, mixId);
        var sources = mix.Items.Select(item => item.ItemId).Distinct(StringComparer.Ordinal).ToArray();
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
        var nativeRecordings = await NativeRecordingsAsync(execution, native, token);
        var libraryProvider = native.Length == 0 ? null : await libraryProviders.ResolveAsync(http, execution);
        var rows = new List<V2MixRow>();
        foreach (var item in mix.Items)
        {
            var target = targets.GetValueOrDefault(item.ItemId) ?? item.ItemId;
            var inLibrary = !IsExternal(target);
            Guid? recording = inLibrary && nativeRecordings.TryGetValue(NativeKey(target), out var owned) ? owned : known.GetValueOrDefault(item.ItemId)?.Id;
            var id = recording is { } value ? V2Ids.Track(value) : V2Ids.TrackSource(target);
            var aliases = new[] { item.ItemId, target, recording is { } canonical ? CanonicalRecordingRecord.DefaultPublicId(canonical) : null }
                .OfType<string>().Where(alias => alias != id).Distinct(StringComparer.Ordinal).ToArray();
            var provider = inLibrary ? libraryProvider : item.Provider;
            rows.Add(new(id, aliases, inLibrary ? target : null, item.Title,
                string.IsNullOrWhiteSpace(item.Artist) ? [] : [new(Valid(item.ArtistId, V2IdKind.Artist) ? V2Ids.Artist(item.ArtistId!) : null, item.Artist)],
                string.IsNullOrWhiteSpace(item.Album) ? null : new(Valid(item.AlbumId, V2IdKind.Album) ? V2Ids.Album(item.AlbumId!) : null, item.Album),
                item.DurationMs, Artwork(item.ArtworkUrl), !inLibrary, provider == null ? null : Provider(provider)));
        }
        return new(V2Ids.Mix(mixId), mix.UpdatedAt, rows);
    }

    private async Task<Dictionary<string, Guid>> NativeRecordingsAsync(ProtocolExecutionContext execution, string[] ids, CancellationToken token)
    {
        if (ids.Length == 0 || execution.Actor?.EffectiveUserId is not { } owner) return [];
        var lookupIds = ids.SelectMany(id => Guid.TryParse(id, out var parsed) ? new[] { id, parsed.ToString("N"), parsed.ToString("D") } : [id])
            .Distinct(StringComparer.Ordinal).ToArray();
        var protocol = execution.Protocol.ToString().ToLowerInvariant();
        await using var db = await factory.CreateDbContextAsync(token);
        var rows = await db.LibraryTracks.AsNoTracking().Where(item => item.OwnerUserId == owner && item.Protocol == protocol &&
                item.BackendInstanceId == execution.BackendInstanceId && lookupIds.Contains(item.BackendItemId) && item.CanonicalRecordingId != null)
            .Select(item => new { item.BackendItemId, item.CanonicalRecordingId }).ToArrayAsync(token);
        return rows.GroupBy(item => NativeKey(item.BackendItemId)).ToDictionary(group => group.Key, group => group.First().CanonicalRecordingId!.Value, StringComparer.Ordinal);
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

    private static bool IsExternal(string id) => id.StartsWith("ext-", StringComparison.Ordinal);

    private static string NativeKey(string id) => Guid.TryParse(id, out var parsed) ? parsed.ToString("N") : id;
}

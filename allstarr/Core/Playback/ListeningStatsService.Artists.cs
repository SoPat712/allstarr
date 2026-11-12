using System.Text.Json;
using System.Text.RegularExpressions;
using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Intelligence;
using allstarr.Core.Storage;
using allstarr.Services.Jellyfin;
using allstarr.Services.Local;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Playback;

internal sealed record ListeningArtist(string Name, string? Id = null, string? LibraryCover = null, string? CoverUrl = null);

public sealed partial class ListeningStatsService
{
    internal static async Task<Dictionary<(string, Guid?, string?), ListeningArtist[]>> ArtistCreditsAsync(AllstarrDbContext db,
        IntelligenceScope scope, IQueryable<ListeningEventRecord> query, CancellationToken token,
        JellyfinProxyService? jellyfin = null, IBackendLibraryAccessResolver? libraryAccess = null, IHeaderDictionary? headers = null)
    {
        var sources = await query.Select(item => new
        {
            item.TrackReference,
            item.CanonicalRecordingId,
            item.LibraryTrackId,
            item.ProviderTrackIdentityId,
            item.Artist,
            item.MusicBrainzFactsJson
        }).Distinct().ToArrayAsync(token);
        var recordings = sources.Select(item => item.CanonicalRecordingId).OfType<Guid>().Distinct().ToArray();
        var identities = sources.Select(item => item.ProviderTrackIdentityId).OfType<Guid>().Distinct().ToArray();
        var trackIds = sources.Select(item => item.LibraryTrackId).OfType<Guid>().Distinct().ToArray();
        var references = sources.Select(item => item.TrackReference).Distinct().ToArray();
        var tracks = Array.Empty<LibraryTrackRecord>();
        var native = new Dictionary<string, ListeningArtist[]>(StringComparer.OrdinalIgnoreCase);
        var nativeCovers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (scope.Protocol == "jellyfin" && jellyfin != null && libraryAccess != null && sources.Length > 0)
        {
            var candidates = await db.LibraryTracks.AsNoTracking().Where(item => item.Protocol == scope.Protocol && item.BackendInstanceId == scope.BackendInstanceId &&
                (trackIds.Contains(item.Id) || references.Contains(item.BackendItemId) || item.CanonicalRecordingId != null && recordings.Contains(item.CanonicalRecordingId.Value))).ToArrayAsync(token);
            var access = candidates.Length == 0 ? null : await libraryAccess.ResolveUserAsync(scope.OwnerUserId, token);
            if (access?.Context != null) tracks = candidates.Where(item => LibraryTrackAccess.Allows(item, access.Context, access.Access)).ToArray();
            foreach (var batch in tracks.Select(item => item.BackendItemId).Distinct().Chunk(100))
            {
                token.ThrowIfCancellationRequested();
                JsonDocument? body;
                int status;
                try
                {
                    (body, status) = await jellyfin.GetJsonAsync($"Users/{Uri.EscapeDataString(access!.Context!.VerifiedBackendPrincipalId)}/Items",
                    new() { ["ids"] = string.Join(',', batch), ["fields"] = "ArtistItems,Artists,AlbumId,ImageTags", ["limit"] = "100" }, headers, token);
                }
                catch (HttpRequestException) { continue; }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { continue; }
                using (body)
                {
                    if (status != 200 || body == null || Field(body.RootElement, "Items") is not { ValueKind: JsonValueKind.Array } items) continue;
                    foreach (var item in items.EnumerateArray())
                    {
                        var id = Text(item, "Id");
                        if (id == null || !batch.Contains(id, StringComparer.OrdinalIgnoreCase)) continue;
                        var cover = Text(item, "AlbumId") ?? id;
                        nativeCovers[id] = cover;
                        var credits = Field(item, "ArtistItems");
                        native[id] = credits.ValueKind == JsonValueKind.Array ? credits.EnumerateArray()
                            .Where(credit => !string.IsNullOrWhiteSpace(Text(credit, "Name")))
                            .Select(credit => new ListeningArtist(Text(credit, "Name")!, Text(credit, "Id"), cover)).ToArray() : [];
                        if (native[id].Length == 0 && Field(item, "Artists") is { ValueKind: JsonValueKind.Array } names)
                            native[id] = names.EnumerateArray().Where(name => name.ValueKind == JsonValueKind.String)
                                .Select(name => new ListeningArtist(name.GetString()!, LibraryCover: cover)).ToArray();
                    }
                }
            }
        }
        var metadata = await ArtistMetadataAsync(db, scope, recordings, identities, token);
        var byRecording = metadata.ToLookup(item => item.Identity.CanonicalRecordingId);
        var byIdentity = metadata.ToLookup(item => item.Identity.Id);
        var byTrack = tracks.ToLookup(item => item.Id);
        var byReference = tracks.ToLookup(item => item.BackendItemId, StringComparer.OrdinalIgnoreCase);
        var byLocalRecording = tracks.Where(item => item.CanonicalRecordingId != null).ToLookup(item => item.CanonicalRecordingId!.Value);
        var result = new Dictionary<(string, Guid?, string?), ListeningArtist[]>();
        var covers = new Dictionary<(string, Guid?, string?), (string? Library, string? Url)>();
        foreach (var source in sources)
        {
            token.ThrowIfCancellationRequested();
            var track = source.LibraryTrackId is { } local ? byTrack[local].FirstOrDefault() : byReference[source.TrackReference].FirstOrDefault();
            track ??= source.CanonicalRecordingId is { } recording ? byLocalRecording[recording].FirstOrDefault() : null;
            var credits = track == null ? [] : native.GetValueOrDefault(track.BackendItemId) ?? [];
            var saved = source.ProviderTrackIdentityId is { } identity ? byIdentity[identity].FirstOrDefault() :
                source.CanonicalRecordingId is { } canonical ? byRecording[canonical].FirstOrDefault() : default;
            string? coverUrl = null;
            if (credits.Length == 0 && saved.Snapshot != null)
                try
                {
                    using var document = JsonDocument.Parse(saved.Snapshot.PayloadJson);
                    if (TrackIdentityService.ReadDisplaySong(saved.Snapshot.PayloadJson, saved.Snapshot.ProviderId, saved.Identity.ExternalId) is { } song)
                    {
                        coverUrl = song.CoverArtUrlLarge ?? song.CoverArtUrl;
                        if (Field(document.RootElement, "Artists") is { ValueKind: JsonValueKind.Array } artists && artists.GetArrayLength() > 0)
                            credits = song.Artists.Select((name, index) =>
                            {
                                var id = song.ArtistIds.ElementAtOrDefault(index) ?? (index == 0 ? song.ArtistId : null);
                                var parsed = LocalLibraryService.ParseExternalResource(id ?? "");
                                return new ListeningArtist(name, CatalogIdentityPolicy.IsPlaybackEligible(saved.Identity) && parsed.isExternal &&
                                    parsed.type == "artist" && parsed.provider == saved.Snapshot.ProviderId ? id : null,
                                    CoverUrl: song.CoverArtUrlLarge ?? song.CoverArtUrl);
                            }).ToArray();
                    }
                }
                catch (JsonException) { }
            if (credits.Length == 0 && source.MusicBrainzFactsJson != null)
                try
                {
                    using var facts = JsonDocument.Parse(source.MusicBrainzFactsJson);
                    var names = Field(facts.RootElement, "artist-credit");
                    if (names.ValueKind == JsonValueKind.Array) credits = names.EnumerateArray()
                        .Select(item => Text(item, "name") ?? Text(Field(item, "artist"), "name"))
                        .OfType<string>().Select(name => new ListeningArtist(name)).ToArray();
                }
                catch (JsonException) { }
            var key = (source.TrackReference, source.CanonicalRecordingId, source.Artist);
            var libraryCover = track == null ? null : nativeCovers.GetValueOrDefault(track.BackendItemId);
            covers[key] = (libraryCover, coverUrl);
            credits = credits.Select(item => item with { LibraryCover = item.LibraryCover ?? libraryCover, CoverUrl = item.CoverUrl ?? coverUrl }).ToArray();
            if (credits.Length > 0 || !result.ContainsKey(key)) result[key] = credits.Where(item => !string.IsNullOrWhiteSpace(item.Name))
                .Select(item => item with { Name = item.Name.Trim() }).DistinctBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        var known = result.Values.SelectMany(items => items).Select(item => item.Name).Append("Tyler, The Creator").Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(name => Regex.IsMatch(name, @", | & | feat\. | ft\. | x ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .OrderByDescending(name => name.Length).ToArray();
        foreach (var key in result.Where(item => item.Value.Length == 0).Select(item => item.Key).ToArray())
            result[key] = SplitArtistNames(key.Item3, known).Select(name => new ListeningArtist(name,
                LibraryCover: covers[key].Library, CoverUrl: covers[key].Url)).ToArray();
        return result;
    }

    internal static async Task<(ExternalMetadataSnapshotRecord Snapshot, ProviderTrackIdentityRecord Identity)[]> ArtistMetadataAsync(
        AllstarrDbContext db, IntelligenceScope scope, Guid[] recordings, Guid[] identities, CancellationToken token)
    {
        // Keep each indexed lookup small so cancellation can stop between synchronous SQLite reads.
        const int batchSize = 100;
        var found = new Dictionary<Guid, ProviderTrackIdentityRecord>();
        foreach (var batch in identities.Chunk(batchSize))
        {
            token.ThrowIfCancellationRequested();
            foreach (var identity in await db.ProviderTrackIdentities.AsNoTracking()
                .Where(item => batch.Contains(item.Id) && item.ResourceKind == ProviderResourceKind.Track).ToArrayAsync(token))
                found[identity.Id] = identity;
        }
        foreach (var batch in recordings.Chunk(batchSize))
        {
            token.ThrowIfCancellationRequested();
            foreach (var identity in await db.ProviderTrackIdentities.AsNoTracking()
                .Where(item => batch.Contains(item.CanonicalRecordingId) && item.ResourceKind == ProviderResourceKind.Track).ToArrayAsync(token))
                found[identity.Id] = identity;
        }
        if (found.Count == 0) return [];
        token.ThrowIfCancellationRequested();
        var principal = await db.Users.Where(item => item.Id == scope.OwnerUserId).Select(item => item.BackendPrincipalId).FirstAsync(token);
        var accounts = await db.ProviderAccounts.AsNoTracking().Where(item => item.Enabled &&
            (item.OwnerUserId == null || item.OwnerUserId == scope.OwnerUserId)).Select(item => item.Id).ToArrayAsync(token);
        var result = new List<(ExternalMetadataSnapshotRecord Snapshot, ProviderTrackIdentityRecord Identity)>();
        foreach (var provider in found.Values.GroupBy(item => item.ProviderId))
        {
            var byHash = provider.ToLookup(item => item.ExternalIdHash);
            foreach (var account in accounts.Select(item => (Guid?)item).Prepend(null))
                foreach (var kind in new[] { "track", "playback-metadata" })
                    foreach (var batch in byHash.Select(item => item.Key).Chunk(batchSize))
                    {
                        token.ThrowIfCancellationRequested();
                        var snapshots = await db.ExternalMetadataSnapshots.AsNoTracking().Where(item =>
                            item.OwnerUserId == scope.OwnerUserId && item.Protocol == scope.Protocol && item.BackendInstanceId == scope.BackendInstanceId &&
                            item.BackendPrincipalId == principal && item.ProviderId == provider.Key && item.ProviderAccountId == account &&
                            item.ResourceKind == kind && batch.Contains(item.ExternalIdHash)).ToArrayAsync(token);
                        token.ThrowIfCancellationRequested();
                        foreach (var snapshot in snapshots)
                            foreach (var identity in byHash[snapshot.ExternalIdHash])
                                if (identity.Scope == ProviderIdentityScope.Catalog || identity.Scope == ProviderIdentityScope.Account &&
                                    identity.ProviderAccountId != null && identity.ProviderAccountId == snapshot.ProviderAccountId)
                                    result.Add((snapshot, identity));
                    }
        }
        return result.OrderByDescending(item => item.Snapshot.RetrievedAt).ToArray();
    }

    private static string[] SplitArtistNames(string? value, IReadOnlyList<string> known)
    {
        var protectedText = value ?? "";
        for (var index = 0; index < known.Count; index++)
            protectedText = protectedText.Replace(known[index], $"\u0001{index}\u0002", StringComparison.OrdinalIgnoreCase);
        return Regex.Split(protectedText, @", | & | feat\. | ft\. | x ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Select(name => Regex.Replace(name, "\u0001(\\d+)\u0002", match => known[int.Parse(match.Groups[1].Value)]).Trim())
            .Where(name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static JsonElement Field(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        ? value.EnumerateObject().FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value : default;
    private static string? Text(JsonElement value, string name) => Field(value, name) is { ValueKind: JsonValueKind.String } text ? text.GetString() : null;
}

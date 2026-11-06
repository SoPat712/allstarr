using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Enrichment;

public sealed record PromotionNamingPreviewRequest(string? Script = null, Guid[]? LibraryTrackIds = null, Guid[]? RecordingIds = null);
public sealed record PromotionNamingExample(Guid? LibraryTrackId, Guid? RecordingId, string Title, string Kind,
    string? RelativePath, IReadOnlyList<PromotionNamingError> Errors);
public sealed record PromotionNamingPreview(string Script, bool ScriptOnly, IReadOnlyList<PromotionNamingToken> Tokens,
    IReadOnlyList<PromotionNamingExample> Examples, IReadOnlyList<PromotionNamingError> Errors);

public sealed class PromotionNamingPreviewService(IDbContextFactory<AllstarrDbContext> factory, PromotionProfiles profiles,
    IBackendLibraryAccessResolver libraryAccess)
{
    private sealed record Example(Guid? LibraryTrackId, Guid? RecordingId, Song Song);

    public async Task<PromotionNamingPreview> PreviewAsync(ProtocolExecutionContext context, PromotionNamingPreviewRequest request,
        CancellationToken token = default)
    {
        var profile = await profiles.GetMineAsync(context, token);
        var script = request.Script ?? profile.Values.NamingScript;
        var errors = PromotionNaming.Validate(script);
        var builder = PromotionNamingTokens.TryParse(script, out var tokens);
        if (errors.Count > 0) return new(script, !builder, tokens, [], errors);
        var libraryIds = request.LibraryTrackIds?.Distinct().ToArray() ?? [];
        var recordingIds = request.RecordingIds?.Distinct().ToArray() ?? [];
        if (libraryIds.Length + recordingIds.Length > 32 || libraryIds.Contains(Guid.Empty) || recordingIds.Contains(Guid.Empty))
            throw new ArgumentException("Choose at most thirty-two real songs.");
        var explicitSelection = libraryIds.Length + recordingIds.Length > 0;
        await using var db = await factory.CreateDbContextAsync(token);
        var user = await PromotionProfiles.RequireUserAsync(db, context, token);
        var access = await libraryAccess.ResolveAsync(context, token);
        var tracksQuery = LibraryTrackAccess.Query(db, context, access);
        if (explicitSelection) tracksQuery = tracksQuery.Where(item => libraryIds.Contains(item.Id) ||
            item.CanonicalRecordingId.HasValue && recordingIds.Contains(item.CanonicalRecordingId.Value));
        var tracks = await tracksQuery.OrderBy(item => item.BackendItemId).ThenBy(item => item.Id).ToArrayAsync(token);
        var keptQuery = from route in db.Set<RecordingKeptRouteEntity>().AsNoTracking()
                        join file in db.ManagedFiles.AsNoTracking() on route.ManagedFileId equals file.Id
                        where route.PublishedByUserId == user.Id && file.RemovedAt == null
                        select new { Route = route, file.CanonicalPath };
        if (explicitSelection) keptQuery = keptQuery.Where(item => recordingIds.Contains(item.Route.CanonicalRecordingId));
        var kept = await keptQuery.OrderBy(item => item.Route.CanonicalRecordingId).ToArrayAsync(token);
        var candidates = tracks.GroupBy(item => (item.BackendInstanceId, item.BackendItemId)).Select(group => group.First())
            .Select(item => new Example(item.Id, item.CanonicalRecordingId, new Song
            {
                Id = item.BackendItemId, Title = item.Title, Artist = item.Artist, Album = item.Album ?? "",
                AlbumArtist = item.AlbumArtist, Container = Extension(item.FilePath), CanonicalRecordingId = item.CanonicalRecordingId,
                ProviderIds = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["musicbrainz_recordingid"] = item.MusicBrainzRecordingId ?? "",
                    ["musicbrainz_albumid"] = item.MusicBrainzReleaseId ?? "",
                    ["musicbrainz_artistid"] = item.MusicBrainzArtistId ?? ""
                }
            })).ToList();
        foreach (var item in kept)
        {
            if (candidates.Any(candidate => candidate.RecordingId == item.Route.CanonicalRecordingId)) continue;
            try
            {
                var metadata = JsonSerializer.Deserialize<KeptRecordingMetadata>(item.Route.DisplayMetadataJson ?? "null");
                if (metadata == null) continue;
                candidates.Add(new(null, item.Route.CanonicalRecordingId, new Song
                {
                    Title = metadata.Title, Artist = metadata.Artists.FirstOrDefault() ?? "Unknown", Album = metadata.Album ?? "",
                    AlbumArtist = metadata.AlbumArtist, Track = metadata.Track, Container = Extension(item.CanonicalPath),
                    CanonicalRecordingId = item.Route.CanonicalRecordingId
                }));
            }
            catch (JsonException) { }
        }
        var available = candidates.Where(item => item.RecordingId.HasValue).Select(item => item.RecordingId!.Value).Distinct().ToArray();
        var snapshots = await (from snapshot in db.ExternalMetadataSnapshots.AsNoTracking()
                               join identity in db.ProviderTrackIdentities.AsNoTracking() on snapshot.ProviderTrackIdentityId equals identity.Id
                               where snapshot.OwnerUserId == user.Id && available.Contains(identity.CanonicalRecordingId) &&
                                   snapshot.ResourceKind == "track" && identity.Verification == ProviderIdentityVerification.Verified &&
                                   snapshot.ProviderId == identity.ProviderId && snapshot.ExternalIdHash == identity.ExternalIdHash &&
                                   (snapshot.ProviderAccountId == null || db.ProviderAccounts.Any(account => account.Id == snapshot.ProviderAccountId &&
                                       account.Enabled && (account.OwnerUserId == null || account.OwnerUserId == user.Id)))
                               orderby snapshot.RetrievedAt descending, snapshot.SnapshotVersion descending, snapshot.Id
                               select new { identity.CanonicalRecordingId, identity.ProviderId, identity.ExternalId, snapshot.PayloadJson })
            .ToArrayAsync(token);
        var snapshotsByRecording = snapshots.ToLookup(item => item.CanonicalRecordingId);
        for (var index = 0; index < candidates.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var candidate = candidates[index];
            if (!candidate.RecordingId.HasValue) continue;
            foreach (var snapshot in snapshotsByRecording[candidate.RecordingId.Value])
            {
                try
                {
                    var stored = TrackIdentityService.ReadDisplaySong(snapshot.PayloadJson, snapshot.ProviderId, snapshot.ExternalId);
                    if (stored == null) continue;
                    stored.Container = candidate.Song.Container;
                    foreach (var pair in candidate.Song.ProviderIds.Where(pair => pair.Value.Length > 0)) stored.ProviderIds.TryAdd(pair.Key, pair.Value);
                    candidates[index] = candidate with { Song = stored };
                    break;
                }
                catch (JsonException) { }
            }
        }
        if (explicitSelection && (libraryIds.Except(candidates.Where(item => item.LibraryTrackId.HasValue).Select(item => item.LibraryTrackId!.Value)).Any() ||
            recordingIds.Except(candidates.Where(item => item.RecordingId.HasValue).Select(item => item.RecordingId!.Value)).Any()))
            throw new UnauthorizedAccessException("These examples are unavailable to this listener.");
        var selected = explicitSelection ? candidates : SelectExamples(candidates);
        var paths = new List<string>();
        var rendered = new List<PromotionNamingExample>();
        foreach (var candidate in selected)
        {
            var result = PromotionNaming.Render(script, PromotionSongFields.FromSong(candidate.Song), paths);
            if (result.RelativePath != null) paths.Add(result.RelativePath);
            rendered.Add(new(candidate.LibraryTrackId, candidate.RecordingId, candidate.Song.Title, Kind(candidate.Song), result.RelativePath, result.Errors));
        }
        return new(script, !builder, tokens, rendered, []);
    }

    private static List<Example> SelectExamples(List<Example> candidates)
    {
        var selected = new List<Example>();
        foreach (var kind in new[] { "album", "multi-disc", "compilation", "single" })
        {
            var match = candidates.FirstOrDefault(item => !selected.Contains(item) && Kind(item.Song) == kind);
            if (match != null) selected.Add(match);
        }
        selected.AddRange(candidates.Where(item => !selected.Contains(item)).Take(4 - selected.Count));
        return selected;
    }

    private static string Kind(Song song) => song.AlbumArtist?.Equals("Various Artists", StringComparison.OrdinalIgnoreCase) == true ? "compilation" :
        song.TotalDiscs > 1 || song.DiscNumber > 1 ? "multi-disc" :
        string.IsNullOrWhiteSpace(song.Album) || song.TotalTracks == 1 ? "single" : "album";

    private static string Extension(string path) => string.IsNullOrWhiteSpace(Path.GetExtension(path)) ? "flac" : Path.GetExtension(path).TrimStart('.');
}

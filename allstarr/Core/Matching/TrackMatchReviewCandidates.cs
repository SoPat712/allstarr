using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Core.Storage;
using allstarr.Services.Spotify;

namespace allstarr.Core.Matching;

public static class TrackMatchReviewCandidates
{
    public static object[] Build(
        ExternalMetadataSnapshotRecord snapshot,
        TrackMatchRecord? decision,
        TrackRouteProjection projection,
        IReadOnlyDictionary<Guid, LibraryTrackRecord> library,
        IReadOnlySet<string> playableProviders,
        IReadOnlySet<Guid> indexedLibraryTrackIds,
        bool routesOnly = false)
    {
        JsonArray candidates;
        try { candidates = JsonNode.Parse(decision?.CandidateResultsJson ?? "[]") as JsonArray ?? []; }
        catch (JsonException) { candidates = []; }
        var current = projection.LibraryTrack;
        if (current == null && projection.ReviewState == TrackMatchState.Ambiguous && decision?.LibraryTrackId is { } selectedId)
            current = library.GetValueOrDefault(selectedId);
        var route = current == null ? projection.PrimaryProviderRoute : null;
        if (current != null || route != null)
        {
            var metadata = Metadata(snapshot.PayloadJson);
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var source = new ExternalTrackMatchSnapshot(snapshot.Id.ToString("N"), snapshot.ProviderId, snapshot.Id.ToString("N"),
                metadata.Title ?? "Unknown", metadata.Artist ?? "Unknown", metadata.Album, null, metadata.DurationMilliseconds,
                metadata.Isrc, null, metadata.IsExplicit)
            {
                DurationProvenance = metadata.DurationProvenance
            };
            var fallback = current != null
                ? new TrackMatchDecisionEngine().ScoreCandidates(source, [ToCandidate(current)]).Single()
                : new TrackMatchCandidateScore(Guid.Empty, $"ext-{route!.ProviderId}-song-{route.ExternalId}", decision?.Confidence ?? 0,
                    [], [], Title: metadata.Title, Artist: metadata.Artist, Album: metadata.Album,
                    DurationMilliseconds: metadata.DurationMilliseconds, SourceIsrc: metadata.Isrc,
                    ProviderTrackIds: new Dictionary<string, string> { [route.ProviderId] = route.ExternalId }, IsLocal: false);
            var existing = candidates.OfType<JsonObject>().FirstOrDefault(node =>
            {
                var item = JsonSerializer.SerializeToElement(node);
                if (current != null)
                    return (Text(item, "libraryTrackId") ?? Text(item, "LibraryTrackId")) == current.Id.ToString() ||
                           (Text(item, "backendItemId") ?? Text(item, "BackendItemId")) == current.BackendItemId;
                var ids = Element(item, "providerTrackIds", "ProviderTrackIds");
                return ids is { ValueKind: JsonValueKind.Object } && ids.Value.EnumerateObject().Any(pair =>
                    SameProvider(pair.Name, route!.ProviderId) && pair.Value.ValueKind == JsonValueKind.String && pair.Value.GetString() == route.ExternalId);
            });
            if (existing == null)
            {
                fallback = fallback with
                {
                    Confidence = decision?.Confidence ?? fallback.Confidence,
                    Reasons = decision == null ? fallback.Reasons : ParseArray(decision.ReasonsJson),
                    Warnings = decision == null ? fallback.Warnings : ParseArray(decision.WarningsJson)
                };
                existing = JsonSerializer.SerializeToNode(fallback, options)!.AsObject();
                candidates.Insert(0, existing);
            }
            existing["isCurrent"] = true;
            existing["isLocal"] = current != null;
            if (current != null) existing["libraryTrackId"] = current.Id.ToString();
            if (route != null)
            {
                var providerIds = new JsonObject { [route.ProviderId] = route.ExternalId };
                if ((existing["providerTrackIds"] ?? existing["ProviderTrackIds"]) is JsonObject savedIds)
                    foreach (var pair in savedIds.Where(pair => !SameProvider(pair.Key, route.ProviderId)))
                        providerIds[pair.Key] = pair.Value?.DeepClone();
                existing["providerTrackIds"] = providerIds;
            }
        }
        return ParseCandidates(candidates.ToJsonString(), playableProviders, indexedLibraryTrackIds, routesOnly);
    }

    private static object[] ParseCandidates(
        string? json,
        IReadOnlySet<string> playableProviders,
        IReadOnlySet<Guid> indexedLibraryTrackIds,
        bool routesOnly)
    {
        try
        {
            using var document = JsonDocument.Parse(json ?? "[]");
            if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
            return document.RootElement.EnumerateArray()
                .Where(item => Boolean(item, "isCurrent") == true || CredibleCandidate(item))
                .Select(item =>
            {
                var libraryTrackId = Text(item, "libraryTrackId") ?? Text(item, "LibraryTrackId");
                var providerTrackIds = Element(item, "providerTrackIds", "ProviderTrackIds");
                var hasPlayableProvider = HasProviderTrackId(providerTrackIds, playableProviders);
                var isLocal = Boolean(item, "isLocal") ?? Boolean(item, "IsLocal") ??
                    (Guid.TryParse(libraryTrackId, out var id) && indexedLibraryTrackIds.Contains(id)
                        ? true
                        : hasPlayableProvider ? false : null);
                return new
                {
                    libraryTrackId,
                    isCurrent = Boolean(item, "isCurrent") == true,
                    backendItemId = Text(item, "backendItemId") ?? Text(item, "BackendItemId"),
                    confidence = Number(item, "confidence") ?? Number(item, "Confidence"),
                    title = Text(item, "title") ?? Text(item, "Title"),
                    isBestVersion = Boolean(item, "isBestVersion") ?? Boolean(item, "IsBestVersion") ?? false,
                    artist = Text(item, "artist") ?? Text(item, "Artist"),
                    album = Text(item, "album") ?? Text(item, "Album"),
                    durationMilliseconds = Number(item, "durationMilliseconds") ??
                                           Number(item, "DurationMilliseconds") ??
                                           (Number(item, "durationSeconds") ?? Number(item, "DurationSeconds")) * 1000,
                    sourceIsrc = Text(item, "sourceIsrc") ?? Text(item, "SourceIsrc"),
                    candidateIsrc = Text(item, "candidateIsrc") ?? Text(item, "CandidateIsrc"),
                    normalizedSourceTitle = Text(item, "normalizedSourceTitle") ?? Text(item, "NormalizedSourceTitle"),
                    normalizedCandidateTitle = Text(item, "normalizedCandidateTitle") ?? Text(item, "NormalizedCandidateTitle"),
                    artistOverlap = Number(item, "artistOverlap") ?? Number(item, "ArtistOverlap"),
                    albumEvidence = Number(item, "albumEvidence") ?? Number(item, "AlbumEvidence"),
                    durationDeltaMilliseconds = Number(item, "durationDeltaMilliseconds") ??
                                                Number(item, "DurationDeltaMilliseconds") ??
                                                (Number(item, "durationDeltaSeconds") ?? Number(item, "DurationDeltaSeconds")) * 1000,
                    isLocal,
                    providerTrackIds,
                    components = Element(item, "components", "Components"),
                    reasons = Element(item, "reasons", "Reasons"),
                    warnings = Element(item, "warnings", "Warnings")
                };
            }).Where(item => item.isLocal == true && item.libraryTrackId != null ||
                             item.isLocal == false && HasProviderTrackId(item.providerTrackIds, playableProviders))
                .Select(item => routesOnly ? new { item.libraryTrackId, item.isLocal, item.providerTrackIds } : (object)item)
                .ToArray();
        }
        catch (JsonException) { return []; }
    }

    private static bool CredibleCandidate(JsonElement item)
    {
        var components = Element(item, "components", "Components");
        if (components is not { ValueKind: JsonValueKind.Object }) return true;
        var title = Number(components.Value, "title") ?? Number(components.Value, "Title");
        var artist = Number(components.Value, "artist") ?? Number(components.Value, "Artist");
        return (title == null || title >= 0.75) && (artist == null || artist >= 0.7);
    }

    private static JsonElement? Element(JsonElement root, string camelName, string pascalName) =>
        root.TryGetProperty(camelName, out var value) || root.TryGetProperty(pascalName, out value)
            ? value.Clone()
            : null;

    private static bool HasProviderTrackId(
        JsonElement? value,
        IReadOnlySet<string> playableProviders) =>
        value is { ValueKind: JsonValueKind.Object } &&
        value.Value.EnumerateObject().Any(item =>
            playableProviders.Contains(ExternalTrackPlaybackPolicy.Normalize(item.Name)) &&
            item.Value.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(item.Value.GetString()));

    private static double? Number(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out var number) ? number : null;

    private static bool? Boolean(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static (string? Title, string? Artist, string? Album, string? ArtworkUrl, string? Isrc, long? DurationMilliseconds, bool? IsExplicit, string? DurationProvenance) Metadata(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var title = Text(root, "title") ?? Text(root, "Title") ?? Text(root, "name") ?? Text(root, "Name");
            return (
                title,
                TrackSnapshotMetadata.Artist(root),
                Text(root, "album") ?? Text(root, "Album") ?? Text(root, "albumTitle") ?? Text(root, "AlbumTitle"),
                Text(root, "artworkUrl") ?? Text(root, "ArtworkUrl") ?? Text(root, "coverUrl") ?? Text(root, "CoverUrl") ??
                    Text(root, "imageUrl") ?? Text(root, "ImageUrl") ??
                    Text(root, "artworkReference") ?? Text(root, "ArtworkReference"),
                Text(root, "isrc") ?? Text(root, "Isrc") ?? Text(root, "ISRC"),
                DurationMilliseconds(root),
                TrackSnapshotMetadata.IsExplicit(root),
                TrackSnapshotMetadata.DurationProvenance(root));
        }
        catch (JsonException) { return default; }
    }

    private static LocalTrackMatchCandidate ToCandidate(LibraryTrackRecord item) => new(
        item.Id,
        item.OwnerUserId,
        item.BackendInstanceId,
        item.BackendLibraryId,
        item.BackendItemId,
        item.CanonicalRecordingId,
        item.Title,
        item.Artist,
        item.Album,
        item.AlbumArtist,
        item.DurationMilliseconds,
        item.Isrc,
        item.MusicBrainzRecordingId,
        null,
        null);

    private static long? DurationMilliseconds(JsonElement root)
    {
        if ((root.TryGetProperty("durationMilliseconds", out var value) ||
             root.TryGetProperty("DurationMilliseconds", out value) ||
             root.TryGetProperty("durationMs", out value) ||
             root.TryGetProperty("DurationMs", out value)) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetDouble(out var milliseconds))
            return checked((long)Math.Round(milliseconds));
        return (root.TryGetProperty("durationSeconds", out value) ||
                root.TryGetProperty("DurationSeconds", out value)) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetDouble(out var seconds)
            ? checked((long)Math.Round(seconds * 1000d))
            : null;
    }

    private static string? Text(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string[] ParseArray(string? json)
    { try { return JsonSerializer.Deserialize<string[]>(json ?? "[]") ?? []; } catch (JsonException) { return []; } }

    private static bool SameProvider(string left, string right) =>
        ExternalTrackPlaybackPolicy.Normalize(left) == ExternalTrackPlaybackPolicy.Normalize(right);
}

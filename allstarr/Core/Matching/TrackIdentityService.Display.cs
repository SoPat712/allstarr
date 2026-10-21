using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Services.Local;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Matching;

public sealed partial class TrackIdentityService
{
    private static readonly JsonSerializerOptions DisplayJson = new() { PropertyNameCaseInsensitive = true };

    public async Task<IReadOnlyList<Song>> GetStoredSongsAsync(
        ProviderActorContext actor, string publicId, CancellationToken cancellationToken = default)
    {
        EnsureStorageReady();
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await ValidateActorAsync(db, actor, cancellationToken);
        var recording = await FindRecordingByPublicIdAsync(db, actor, publicId, cancellationToken);
        var userId = actor.EffectiveUserId;
        if (recording == null)
        {
            var parsed = LocalLibraryService.ParseExternalResource(publicId);
            if (!parsed.isExternal || parsed.type != "song") return [];
            var provider = _registry?.ResolveProviderId(parsed.provider!) ?? parsed.provider!;
            var hash = CanonicalCatalogKeys.Hash(parsed.externalId!);
            var direct = await db.ExternalMetadataSnapshots.AsNoTracking().Where(item =>
                    item.ProviderId == provider && item.ExternalIdHash == hash && item.ResourceKind == "track" &&
                    item.OwnerUserId == userId && (item.ProviderAccountId == null || db.ProviderAccounts.Any(account =>
                        account.Id == item.ProviderAccountId && account.Enabled &&
                        (account.OwnerUserId == null || account.OwnerUserId == userId))))
                .ToArrayAsync(cancellationToken);
            foreach (var snapshot in direct.OrderByDescending(item => item.RetrievedAt).ThenByDescending(item => item.SnapshotVersion))
            {
                try
                {
                    var song = ReadDisplaySong(snapshot.PayloadJson, provider, parsed.externalId!);
                    if (song == null) continue;
                    song.Id = $"ext-{provider}-song-{parsed.externalId}";
                    return [song];
                }
                catch (JsonException) { }
            }
            return [];
        }
        var links = await db.ProviderTrackIdentities.AsNoTracking().Where(item =>
            item.CanonicalRecordingId == recording.Id && item.ResourceKind == ProviderResourceKind.Track &&
            item.Verification == ProviderIdentityVerification.Verified &&
            item.VerificationMethod != "automatic-suggestion" &&
            item.VerificationMethod != "source-snapshot-hash" &&
            item.VerificationMethod != CatalogIdentityPolicy.SupersededVerificationMethod &&
            item.VerificationMethod != ManualTrackAuthorityPolicy.ReleasedProviderVerificationMethod &&
            item.VerificationMethod != ManualTrackAuthorityPolicy.ReplacedProviderVerificationMethod &&
            (item.Scope == ProviderIdentityScope.Catalog || userId != null && db.ProviderAccounts.Any(account =>
                account.Id == item.ProviderAccountId && account.Enabled &&
                (account.OwnerUserId == null || account.OwnerUserId == userId))))
            .ToArrayAsync(cancellationToken);
        var providers = links.Select(item => item.ProviderId).Distinct().ToArray();
        var hashes = links.Select(item => item.ExternalIdHash).Distinct().ToArray();
        var snapshots = await db.ExternalMetadataSnapshots.AsNoTracking().Where(item =>
            providers.Contains(item.ProviderId) && hashes.Contains(item.ExternalIdHash) && item.ResourceKind == "track" &&
            (item.OwnerUserId == userId || userId == null && item.ProviderAccountId == null) &&
            (item.ProviderAccountId == null || db.ProviderAccounts.Any(account =>
                account.Id == item.ProviderAccountId && account.Enabled &&
                (account.OwnerUserId == null || account.OwnerUserId == userId))))
            .ToArrayAsync(cancellationToken);
        var evidence = await (from match in db.TrackMatches.AsNoTracking()
                              join snapshot in db.ExternalMetadataSnapshots.AsNoTracking() on match.ExternalSnapshotId equals snapshot.Id
                              where match.CanonicalRecordingId == recording.Id && match.OwnerUserId == userId &&
                                    snapshot.OwnerUserId == userId && match.State == TrackMatchState.Accepted &&
                                    match.Confidence >= match.Threshold &&
                                    !db.TrackMatches.Any(later => later.ExternalSnapshotId == match.ExternalSnapshotId &&
                                        (later.DecisionVersion > match.DecisionVersion || later.DecisionVersion == match.DecisionVersion &&
                                            later.DecidedAt > match.DecidedAt)) &&
                                    (snapshot.ProviderAccountId == null || db.ProviderAccounts.Any(account =>
                                        account.Id == snapshot.ProviderAccountId && account.Enabled &&
                                        (account.OwnerUserId == null || account.OwnerUserId == userId)))
                              orderby match.DecidedAt descending, match.DecisionVersion descending
                              select match.CandidateResultsJson).ToArrayAsync(cancellationToken);
        var songs = new List<Song>();
        foreach (var link in links)
        {
            var snapshot = snapshots.Where(item => item.ProviderId == link.ProviderId &&
                    item.ExternalIdHash == link.ExternalIdHash &&
                    (item.ProviderTrackIdentityId == null || item.ProviderTrackIdentityId == link.Id) &&
                    (link.Scope == ProviderIdentityScope.Catalog || item.ProviderAccountId == link.ProviderAccountId))
                .OrderByDescending(item => item.RetrievedAt).ThenByDescending(item => item.SnapshotVersion)
                .ThenBy(item => item.Id).FirstOrDefault();
            Song? song = null;
            if (snapshot != null)
            {
                try { song = ReadDisplaySong(snapshot.PayloadJson, link.ProviderId, link.ExternalId); }
                catch (JsonException) { }
            }
            if (song == null)
                foreach (var payload in evidence)
                {
                    try
                    {
                        using var document = JsonDocument.Parse(payload);
                        var candidate = TrackMatchTargetResolver.FindCandidate(document.RootElement, link.ProviderId, link.ExternalId);
                        if (candidate == null) continue;
                        song = ReadDisplaySong(candidate.Value.GetRawText(), link.ProviderId, link.ExternalId);
                        if (song != null) break;
                    }
                    catch (JsonException) { }
                }
            song ??= new Song { ExternalProvider = link.ProviderId, ExternalId = link.ExternalId };
            song.Id = recording.PublicId;
            song.CanonicalRecordingId = recording.Id;
            song.Isrc = Identifier(recording, RecordingIdentifierKinds.Isrc) ?? song.Isrc;
            songs.Add(song);
        }
        return songs;
    }

    private static Song? ReadDisplaySong(string payload, string provider, string externalId)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        static JsonElement Property(JsonElement value, params string[] names) => value.ValueKind == JsonValueKind.Object
            ? value.EnumerateObject().FirstOrDefault(item => names.Contains(item.Name, StringComparer.OrdinalIgnoreCase)).Value
            : default;
        static string? Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        string? Field(params string[] names) => Text(Property(root, names));
        if (Field("ExternalProvider") != null && JsonSerializer.Deserialize<Song>(payload, DisplayJson) is { } retained &&
            !string.IsNullOrWhiteSpace(retained.Title) && !string.IsNullOrWhiteSpace(retained.Artist))
        {
            retained.ExternalProvider = provider;
            retained.ExternalId = externalId;
            retained.IsLocal = false;
            return retained;
        }
        long? Number(params string[] names) => Property(root, names) is { ValueKind: JsonValueKind.Number } value &&
            value.TryGetInt64(out var number) ? number : null;
        string? Resource(JsonElement value, string kind)
        {
            var id = Text(value) ?? Text(Property(value, "Value"));
            if (string.IsNullOrWhiteSpace(id)) return null;
            return id.StartsWith("ext-", StringComparison.Ordinal) ? id : $"ext-{provider}-{kind}-{id}";
        }
        var title = Field("Title");
        var artists = new List<string>();
        var artistIds = new List<string>();
        if (Property(root, "Artists") is { ValueKind: JsonValueKind.Array } credits)
            foreach (var credit in credits.EnumerateArray())
            {
                var name = Text(credit) ?? Text(Property(credit, "Name"));
                if (string.IsNullOrWhiteSpace(name)) continue;
                artists.Add(name);
                artistIds.Add(Resource(Property(credit, "ArtistId"), "artist") ?? string.Empty);
            }
        var artist = Field("Artist") ?? artists.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist)) return null;
        if (artists.Count == 0) artists.Add(artist);
        if (Property(root, "ArtistIds") is { ValueKind: JsonValueKind.Array } ids)
            artistIds = ids.EnumerateArray().Select(id => Resource(id, "artist") ?? string.Empty).ToList();
        var duration = Number("DurationMilliseconds", "DurationMs") / 1000 ?? Number("Duration");
        var artwork = Field("CoverArtUrl", "ArtworkUrl", "ArtworkReference") ??
            Text(Property(Property(root, "Artwork"), "PublicUri"));
        return new Song
        {
            ExternalProvider = provider,
            ExternalId = externalId,
            Title = title,
            Artist = artist,
            Artists = artists,
            ArtistIds = artistIds,
            ArtistId = Resource(Property(root, "ArtistId"), "artist") ?? artistIds.FirstOrDefault(),
            Album = Field("Album", "AlbumTitle") ?? string.Empty,
            AlbumId = Resource(Property(root, "AlbumId"), "album"),
            AlbumArtist = Field("AlbumArtist"),
            Year = (int?)Number("Year"),
            CoverArtUrl = artwork,
            CoverArtUrlLarge = Field("CoverArtUrlLarge") ?? artwork,
            Duration = duration is >= 0 and <= int.MaxValue ? (int)duration.Value : null,
            Isrc = Field("Isrc", "CandidateIsrc"),
            Track = (int?)Number("Track", "TrackNumber"),
            DiscNumber = (int?)Number("DiscNumber"),
            TotalTracks = (int?)Number("TotalTracks"),
            Genre = Field("Genre"),
            ReleaseDate = Field("ReleaseDate"),
            Bitrate = (int?)Number("Bitrate"),
            ExplicitContentLyrics = (int?)Number("ExplicitContentLyrics") ??
                (Property(root, "IsExplicit").ValueKind switch { JsonValueKind.True => 1, JsonValueKind.False => 0, _ => (int?)null })
        };
    }
}

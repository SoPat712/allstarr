using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace allstarr.Core.Intelligence;

public sealed partial class ListeningHistoryResolution
{
    private RunState? retained;
    private sealed record RunState(Guid JobId, int Events, int Tracks, string Libraries, LibraryTrackRecord[] Library,
        Dictionary<string, LocalTrackMatchCandidate[]> Metadata, Dictionary<Guid, StoredTarget> Targets,
        Dictionary<string, LocalTrackMatchCandidate[]> Spotify, IGrouping<string, ListeningEventRecord>[] Groups)
    {
        public HashSet<string> Completed { get; } = [];
    }

    private async Task<RunState> LoadAsync(AllstarrDbContext db, ProtocolExecutionContext context, BackendLibraryAccess access,
        Guid jobId, IntelligenceScope scope, CancellationToken token)
    {
        var count = await db.ListeningEvents.CountAsync(item => item.OwnerUserId == scope.OwnerUserId &&
            item.Protocol == scope.Protocol && item.BackendInstanceId == scope.BackendInstanceId, token);
        var query = LibraryTrackAccess.Query(db, context, access);
        var trackCount = await query.CountAsync(token);
        var libraries = string.Join("\n", access.LibraryIds.Order(StringComparer.Ordinal));
        if (retained is { } previous && previous.JobId == jobId && previous.Events == count &&
            previous.Tracks == trackCount && previous.Libraries == libraries) return previous;
        var tracks = await query.ToArrayAsync(token);
        var (metadata, targets, spotify) = await StoredIndexAsync(db, context, tracks, token);
        var recent = await Receipts(db, scope).Where(item => item.PayloadJson == ReceiptPayload && item.RetrievedAt > clock.UtcNow.AddDays(-7))
            .Select(item => item.ExternalIdHash).ToHashSetAsync(token);
        var groups = (await MatchingEvents(db, scope).ToArrayAsync(token)).GroupBy(SongKey)
            .Where(group => !recent.Contains(group.Key)).OrderByDescending(group => group.Count(item => item.State == ListeningEventState.Completed))
            .ThenBy(group => group.Key, StringComparer.Ordinal).ToArray();
        return retained = new(jobId, count, trackCount, libraries, tracks, metadata, targets, spotify, groups);
    }

    private sealed record StoredTarget(LibraryTrackRecord? Library, Song? Song, ProviderTrackIdentityRecord? Identity = null);

    private static async Task<(Dictionary<string, LocalTrackMatchCandidate[]> Metadata,
        Dictionary<Guid, StoredTarget> Targets, Dictionary<string, LocalTrackMatchCandidate[]> Spotify)> StoredIndexAsync(
        AllstarrDbContext db, ProtocolExecutionContext context, LibraryTrackRecord[] tracks, CancellationToken token)
    {
        var user = context.RequireActor().EffectiveUserId;
        var candidates = new List<LocalTrackMatchCandidate>();
        var targets = new Dictionary<Guid, StoredTarget>();
        foreach (var track in tracks)
        {
            candidates.Add(new(track.Id, track.OwnerUserId, track.BackendInstanceId, track.BackendLibraryId, track.BackendItemId,
                track.CanonicalRecordingId, track.Title, track.Artist, track.Album, track.AlbumArtist, track.DurationMilliseconds,
                track.Isrc, track.MusicBrainzRecordingId, null));
            targets[track.Id] = new(track, null);
        }
        var links = (await db.ProviderTrackIdentities.AsNoTracking().Where(item => item.ResourceKind == ProviderResourceKind.Track &&
            db.CanonicalRecordings.Any(recording => recording.Id == item.CanonicalRecordingId && recording.MergedIntoId == null) &&
            (item.Scope == ProviderIdentityScope.Catalog || db.ProviderAccounts.Any(account => account.Id == item.ProviderAccountId &&
                account.Enabled && (account.OwnerUserId == user || account.OwnerUserId == null)))).ToArrayAsync(token))
            .Where(CatalogIdentityPolicy.IsPlaybackEligible).ToArray();
        var byId = links.ToDictionary(item => item.Id);
        var byKey = links.GroupBy(item => (item.ProviderId, item.ExternalIdHash, item.ProviderAccountId))
            .ToDictionary(group => group.Key, group => group.First());
        var snapshots = await db.ExternalMetadataSnapshots.AsNoTracking().Where(item => item.OwnerUserId == user &&
            (item.ResourceKind == "track" || item.ResourceKind == "playback-metadata")).ToArrayAsync(token);
        var seen = new HashSet<Guid>();
        foreach (var snapshot in snapshots.OrderByDescending(item => item.RetrievedAt).ThenByDescending(item => item.SnapshotVersion))
        {
            var link = snapshot.ProviderTrackIdentityId is { } id ? byId.GetValueOrDefault(id) :
                byKey.GetValueOrDefault((snapshot.ProviderId, snapshot.ExternalIdHash, snapshot.ProviderAccountId));
            if (link == null || seen.Contains(link.Id)) continue;
            try
            {
                var song = TrackIdentityService.ReadDisplaySong(snapshot.PayloadJson, link.ProviderId, link.ExternalId);
                if (song == null) continue;
                Add(link.Id, link.CanonicalRecordingId, song, link);
                seen.Add(link.Id);
            }
            catch (JsonException) { }
        }
        var events = await db.ListeningEvents.AsNoTracking().Where(item => item.OwnerUserId == user &&
            item.Protocol == context.Protocol.ToString().ToLower() && item.BackendInstanceId == context.BackendInstanceId &&
            item.CanonicalRecordingId != null && db.CanonicalRecordings.Any(recording => recording.Id == item.CanonicalRecordingId && recording.MergedIntoId == null))
            .Select(item => new { item.CanonicalRecordingId, item.Title, item.Artist, item.Album, item.DurationMilliseconds, item.Isrc, item.ProviderTrackReference })
            .Distinct().ToArrayAsync(token);
        foreach (var play in events.DistinctBy(item => (item.CanonicalRecordingId, item.Title, item.Artist)))
            Add(Guid.NewGuid(), play.CanonicalRecordingId!.Value, new Song
            {
                Title = play.Title ?? "",
                Artist = play.Artist ?? "",
                Album = play.Album ?? "",
                Isrc = play.Isrc,
                Duration = (int?)(play.DurationMilliseconds / 1000),
                ExternalProvider = "allstarr",
                ExternalId = play.CanonicalRecordingId.Value.ToString("D")
            });
        var byRecording = candidates.Where(item => item.CanonicalRecordingId != null).GroupBy(item => item.CanonicalRecordingId!.Value)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var spotify = events.Where(item => item.ProviderTrackReference?.StartsWith("spotify:track:", StringComparison.Ordinal) == true)
            .Select(item => (Reference: item.ProviderTrackReference!, Recording: item.CanonicalRecordingId!.Value))
            .Concat(links.Where(item => item.ProviderId == "spotify").Select(item =>
                (Reference: item.ExternalId.StartsWith("spotify:track:", StringComparison.Ordinal) ? item.ExternalId : "spotify:track:" + item.ExternalId,
                    Recording: item.CanonicalRecordingId)))
            .GroupBy(item => item.Reference).Where(group => group.Select(item => item.Recording).Distinct().Count() == 1)
            .ToDictionary(group => group.Key, group => Deduplicate(byRecording.GetValueOrDefault(group.First().Recording) ?? []));
        return (candidates.GroupBy(item => MetadataKey(item.Title, item.Artist)).ToDictionary(group => group.Key, group => Deduplicate(group)), targets, spotify);

        void Add(Guid id, Guid recording, Song song, ProviderTrackIdentityRecord? identity = null)
        {
            if (string.IsNullOrWhiteSpace(song.Title) || string.IsNullOrWhiteSpace(song.Artist)) return;
            candidates.Add(new(id, user!.Value, context.BackendInstanceId, null, $"ext-{song.ExternalProvider}-song-{song.ExternalId}", recording,
                song.Title, song.Artist, song.Album, song.AlbumArtist, song.Duration * 1000L, song.Isrc, null,
                song.ExplicitContentLyrics switch { 1 => true, 0 or 3 => false, _ => null }, IsLocal: false));
            targets[id] = new(null, song, identity);
        }

        static LocalTrackMatchCandidate[] Deduplicate(IEnumerable<LocalTrackMatchCandidate> values) =>
            values.DistinctBy(item => item.CanonicalRecordingId ?? item.LibraryTrackId).ToArray();
    }
}

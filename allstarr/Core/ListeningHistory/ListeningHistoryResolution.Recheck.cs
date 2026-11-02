using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Intelligence;

public sealed record HistoryRecheckSong(string Title, string? Artist, string? Album, string MatchedTitle, string? MatchedArtist,
    string? MatchedAlbum, string Verdict, int Plays);
public sealed record HistoryRecheck(int Songs, int Unchecked, int Doubtful, int DoubtfulPlays, IReadOnlyList<HistoryRecheckSong> Examples);

public sealed partial class ListeningHistoryResolution
{
    // Past plays matched before the matcher learned better can sit on the wrong song, and each one then lends its title
    // to the next play of that name. This checks every matched song against the recording's own details from the
    // library or a source, never against other plays, and only reads.
    public async Task<HistoryRecheck> RecheckAsync(IntelligenceScope scope, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var user = await db.Users.AsNoTracking().SingleAsync(item => item.Id == scope.OwnerUserId, token);
        var context = new ProtocolExecutionContext(Enum.Parse<ProtocolKind>(user.BackendType, true), user.BackendInstanceId,
            user.BackendPrincipalId, new(user.Id, user.BackendType, user.BackendInstanceId, user.BackendPrincipalId, user.DisplayName, user.IsAdmin),
            "history-recheck", clock.UtcNow.AddMinutes(10), token);
        var access = await libraryAccess.ResolveAsync(context, token);
        if (!access.Succeeded) throw new UnauthorizedAccessException();
        var tracks = await LibraryTrackAccess.Query(db, context, access).ToArrayAsync(token);
        var (metadata, _, spotify) = await StoredIndexAsync(db, context, tracks, token, includeHistory: false);
        var byRecording = metadata.Values.SelectMany(item => item).Where(item => item.CanonicalRecordingId != null)
            .GroupBy(item => item.CanonicalRecordingId!.Value)
            .ToDictionary(group => group.Key, group => group.DistinctBy(item => item.LibraryTrackId).ToArray());
        var matched = await db.ListeningEvents.AsNoTracking().Where(item => item.OwnerUserId == scope.OwnerUserId &&
                item.Protocol == scope.Protocol && item.BackendInstanceId == scope.BackendInstanceId &&
                (item.SourceKind == "import" || item.SourceKind == "listenbrainz-api") && item.CanonicalRecordingId != null)
            .GroupBy(item => new { item.CanonicalRecordingId, item.Title, item.Artist, item.Album, item.DurationMilliseconds, item.Isrc, item.ProviderTrackReference })
            .Select(group => new { group.Key, Plays = group.Count() })
            .ToArrayAsync(token);
        var matchScope = new TrackMatchScope(user.Id, user.BackendInstanceId, null, 2, 1, access.LibraryIds.ToHashSet());
        var unread = 0;
        var doubtful = new List<HistoryRecheckSong>();
        foreach (var song in matched)
        {
            token.ThrowIfCancellationRequested();
            var recording = song.Key.CanonicalRecordingId!.Value;
            if (song.Key.ProviderTrackReference is { } reference && spotify.GetValueOrDefault(reference) is { Length: > 0 } exact &&
                exact.Any(item => item.CanonicalRecordingId == recording)) continue;
            if (byRecording.GetValueOrDefault(recording) is not { Length: > 0 } candidates) { unread++; continue; }
            var play = new ListeningEventRecord
            {
                Title = song.Key.Title,
                Artist = song.Key.Artist,
                Album = song.Key.Album,
                DurationMilliseconds = song.Key.DurationMilliseconds,
                Isrc = song.Key.Isrc
            };
            var decision = matcher.Decide(matchScope, Source("recheck", play), candidates, null);
            var selected = candidates.FirstOrDefault(item => item.LibraryTrackId == decision.SelectedLibraryTrackId);
            if (selected != null && Accepted(decision) && SameEdition(play.Title, selected.Title)) continue;
            var shown = selected ?? candidates[0];
            doubtful.Add(new(song.Key.Title ?? "", song.Key.Artist, song.Key.Album, shown.Title, shown.Artist, shown.Album,
                selected != null && Accepted(decision) ? "edition" : decision.Warnings.FirstOrDefault() ?? decision.State.ToString(), song.Plays));
        }
        return new(matched.Length, unread, doubtful.Count, doubtful.Sum(item => item.Plays),
            doubtful.OrderByDescending(item => item.Plays).ThenBy(item => item.Title, StringComparer.Ordinal).Take(200).ToArray());
    }
}

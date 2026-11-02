using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Intelligence;

public sealed record HistoryRecheckSong(Guid RecordingId, string Title, string? Artist, string? Album, string MatchedTitle,
    string? MatchedArtist, string? MatchedAlbum, string Verdict, int Plays);
public sealed record HistoryUnmatchRequest(Guid RecordingId, string Title, string? Artist);
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
            var shown = selected ?? candidates[0];
            // The matcher accepts a title that contains the other, so "A Man Without Love" passed as "Lonely Is a Man
            // Without Love"; it also doubts the same song on another album. What decides is whether title, version and
            // main artist agree, plus the matcher's own doubt about a version note such as "Taylor's Version".
            if (SameTitle(play, shown) && SameEdition(play.Title, shown.Title) && SameArtist(play, shown) &&
                !decision.Warnings.Contains("title_annotation_mismatch_review")) continue;
            var accepted = selected != null && Accepted(decision) && SameEdition(play.Title, selected.Title);
            doubtful.Add(new(recording, song.Key.Title ?? "", song.Key.Artist, song.Key.Album, shown.Title, shown.Artist, shown.Album,
                accepted ? "different-title" : selected != null && Accepted(decision) ? "edition" : decision.Warnings.FirstOrDefault() ?? decision.State.ToString(), song.Plays));
        }
        // Plays of one song can come from several album versions; unmatching acts on all of them, so they show as one.
        var songs = doubtful.GroupBy(item => (item.RecordingId, item.Title, item.Artist))
            .Select(group => group.OrderByDescending(item => item.Plays).First() with { Plays = group.Sum(item => item.Plays) })
            .ToArray();
        return new(matched.Length, unread, songs.Length, songs.Sum(item => item.Plays),
            songs.OrderByDescending(item => item.Plays).ThenBy(item => item.Title, StringComparer.Ordinal).Take(200).ToArray());
    }

    // The listener said these plays are not this song. They return to unmatched and are never matched again, since the
    // matcher that linked them would only link them the same way.
    public async Task<int> UnmatchAsync(IntelligenceScope scope, HistoryUnmatchRequest request, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var user = await db.Users.AsNoTracking().SingleAsync(item => item.Id == scope.OwnerUserId, token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var plays = db.ListeningEvents.Where(item => item.OwnerUserId == scope.OwnerUserId && item.Protocol == scope.Protocol &&
            item.BackendInstanceId == scope.BackendInstanceId && (item.SourceKind == "import" || item.SourceKind == "listenbrainz-api") &&
            item.CanonicalRecordingId == request.RecordingId && item.Title == request.Title && item.Artist == request.Artist);
        var found = await plays.AsNoTracking().Select(item => new ListeningEventRecord
        {
            Id = item.Id,
            ProviderId = item.ProviderId,
            ProviderTrackReference = item.ProviderTrackReference,
            Title = item.Title,
            Artist = item.Artist,
            ImportProvenance = item.ImportProvenance
        }).ToArrayAsync(token);
        if (found.Length == 0) return 0;
        var songs = found.GroupBy(SongKey, StringComparer.Ordinal).ToArray();
        var count = 0;
        foreach (var song in songs)
        {
            var ids = song.Select(play => play.Id).ToArray();
            var placeholder = "history:" + song.Key;
            count += await plays.Where(item => ids.Contains(item.Id)).ExecuteUpdateAsync(update => update
                .SetProperty(item => item.CanonicalRecordingId, (Guid?)null)
                .SetProperty(item => item.LibraryTrackId, (Guid?)null)
                .SetProperty(item => item.TrackReference, placeholder)
                .SetProperty(item => item.ProviderTrackIdentityId, (Guid?)null)
                .SetProperty(item => item.Revision, item => item.Revision + 1), token);
        }
        foreach (var import in found.GroupBy(play => play.ImportProvenance?.Split(':').ElementAtOrDefault(1)))
        {
            if (!Guid.TryParse(import.Key, out var importId)) continue;
            var moved = import.Count();
            await db.ListeningHistoryImports.Where(item => item.Id == importId && item.OwnerUserId == scope.OwnerUserId &&
                    item.Protocol == scope.Protocol && item.BackendInstanceId == scope.BackendInstanceId)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(item => item.ResolvedRows, item => Math.Max(0, item.ResolvedRows - moved))
                    .SetProperty(item => item.UnresolvedRows, item => item.UnresolvedRows + moved)
                    .SetProperty(item => item.UpdatedAt, clock.UtcNow)
                    .SetProperty(item => item.Revision, item => item.Revision + 1), token);
        }
        foreach (var key in songs.Select(song => song.Key))
        {
            var kind = ResourceKind;
            var version = await Receipts(db, scope).Where(item => item.ExternalIdHash == key)
                .Select(item => (int?)item.SnapshotVersion).MaxAsync(token) ?? 0;
            db.ExternalMetadataSnapshots.Add(new()
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = scope.OwnerUserId,
                Protocol = scope.Protocol,
                BackendInstanceId = scope.BackendInstanceId,
                BackendPrincipalId = user.BackendPrincipalId,
                ProviderId = "allstarr",
                ResourceKind = kind,
                ExternalIdHash = key,
                SnapshotVersion = version + 1,
                RetrievedAt = clock.UtcNow,
                PayloadJson = UnmatchedPayload,
                PayloadSha256 = ListeningHistoryImportService.Hash(UnmatchedPayload),
                CorrelationId = "history-unmatch"
            });
        }
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return count;
    }

    private static bool SameArtist(ListeningEventRecord play, LocalTrackMatchCandidate matched) =>
        FuzzyMatcher.NormalizeForMatching(FuzzyMatcher.SplitCredits(play.Artist).FirstOrDefault() ?? "") ==
        FuzzyMatcher.NormalizeForMatching(FuzzyMatcher.SplitCredits(matched.Artist).FirstOrDefault() ?? "");

    private static bool SameTitle(ListeningEventRecord play, LocalTrackMatchCandidate matched) =>
        FuzzyMatcher.NormalizeTitleForMatching(play.Title ?? "", play.Artist, matched.Artist) ==
        FuzzyMatcher.NormalizeTitleForMatching(matched.Title, matched.Artist, play.Artist);
}

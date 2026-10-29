using System.Globalization;
using allstarr.Core.Identity;
using allstarr.Core.Favorites;
using allstarr.Core.Intelligence;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Services.Local;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Music;

public sealed partial class PersonalMixService
{
    private async Task<IReadOnlyList<MixCandidate>> CandidatesAsync(AllstarrDbContext db, UserRecord user,
        ListeningEventRecord[] events, CancellationToken token)
    {
        var context = new ProtocolExecutionContext(Enum.Parse<ProtocolKind>(user.BackendType, true), user.BackendInstanceId,
            user.BackendPrincipalId, new AllstarrPrincipal(user.Id, user.BackendType, user.BackendInstanceId,
                user.BackendPrincipalId, user.DisplayName, user.IsAdmin), "mixes", clock.UtcNow.AddMinutes(5), token)
        { Policy = await policies.ResolveForUserAsync(user.Id, token), IsReadOnly = true };
        var access = await libraryAccess.ResolveAsync(context, token);
        var known = events.Select(item => item.LibraryTrackId).OfType<Guid>().Distinct().ToArray();
        var library = await LibraryTrackAccess.Query(db, context, access).OrderByDescending(item => known.Contains(item.Id))
            .ThenBy(item => item.BackendItemId).Take(600).ToArrayAsync(token);
        var rows = new List<(Song Song, string Source, DateTimeOffset? Release)>();
        var hearts = (await new FavoriteViewReader(factory).ReadAsync(context, token)).Where(item => item.Display.Kind == "song").Take(300).ToArray();
        rows.AddRange(hearts.Select(item => (item.Song(), "heart", (DateTimeOffset?)null)));
        foreach (var track in library) rows.Add((new Song
        {
            Id = track.BackendItemId,
            CanonicalRecordingId = track.CanonicalRecordingId,
            Title = track.Title,
            Artist = track.Artist,
            Album = track.Album ?? "",
            Duration = (int?)(track.DurationMilliseconds / 1000),
            Isrc = track.Isrc,
            IsLocal = true
        }, "library", null));
        var lookups = 0;
        var history = events.Where(item => item.State == ListeningEventState.Completed)
            .GroupBy(item => item.CanonicalRecordingId?.ToString("D") ?? item.TrackReference).ToArray();
        var top = history.OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal).Take(PersonalMixRules.TopSongs);
        var recent = history.OrderByDescending(group => group.Count(item => item.ListenedAt >= clock.UtcNow.AddDays(-30)))
            .ThenBy(group => group.Key, StringComparer.Ordinal).Take(30);
        foreach (var group in recent.Concat(top).DistinctBy(group => group.Key))
        {
            var play = group.OrderByDescending(item => item.ListenedAt).First();
            var existing = rows.FirstOrDefault(item => item.Song.Id == play.TrackReference || play.CanonicalRecordingId != null && item.Song.CanonicalRecordingId == play.CanonicalRecordingId).Song;
            if (existing != null) { existing.CanonicalRecordingId ??= play.CanonicalRecordingId; continue; }
            var parsed = LocalLibraryService.ParseExternalResource(play.TrackReference);
            try
            {
                Song? song = null;
                if (parsed.isExternal && parsed.type == "song") song = await providers.GetSongAsync(context, parsed.provider!, parsed.externalId!);
                else if (++lookups <= 30 && !string.IsNullOrWhiteSpace(play.Title) && !string.IsNullOrWhiteSpace(play.Artist))
                {
                    var seed = new Song
                    {
                        Title = play.Title,
                        Artist = play.Artist,
                        Album = play.Album ?? "",
                        Duration = (int?)(play.DurationMilliseconds / 1000),
                        Isrc = play.Isrc
                    };
                    song = (await providers.SearchAutomaticSongsAsync(context, play.Title + " " + play.Artist, 3))
                        .FirstOrDefault(candidate => MusicSearchService.MetadataRecordingsMatch(seed, candidate));
                }
                if (song != null) { song.CanonicalRecordingId ??= play.CanonicalRecordingId; rows.Add((song, "history", null)); }
            }
            catch (Exception) when (!token.IsCancellationRequested) { }
        }
        // Three familiar seeds bound source work; reuse radio's provider and similar-track fallbacks.
        foreach (var seed in rows.Where(item => !item.Song.IsLocal).Take(3).ToArray())
        {
            if (string.IsNullOrWhiteSpace(seed.Song.ExternalProvider) || string.IsNullOrWhiteSpace(seed.Song.ExternalId)) continue;
            try
            {
                var found = await radio.SimilarSongsAsync(context, seed.Song.ExternalProvider, seed.Song.ExternalId, seed.Song.Id, 50);
                rows.AddRange(found.Select(song => (song, "radio", (DateTimeOffset?)null)));
            }
            catch (Exception) when (!token.IsCancellationRequested) { }
        }
        var albums = new List<(string Provider, string Id, DateTimeOffset At)>();
        foreach (var entry in (await releases.ReadAsync(user.Id, token)).Entries)
            if (ReleaseDate(entry.ReleaseDate) is { } at && at >= clock.UtcNow.AddDays(-PersonalMixRules.ReleaseDays))
                albums.AddRange(entry.Sources.Select(source => (source.ProviderId, source.AlbumId, at)));
        foreach (var frequent in events.Where(play => play.State == ListeningEventState.Completed && !string.IsNullOrWhiteSpace(play.Artist))
            .GroupBy(play => play.Artist!, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() >= 5)
            .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal).Take(3))
        {
            try
            {
                var seed = rows.Select(item => item.Song).FirstOrDefault(song => !song.IsLocal && song.ArtistId != null &&
                    string.Equals(song.Artist, frequent.Key, StringComparison.OrdinalIgnoreCase));
                var found = seed == null ? (await providers.SearchAsync(context, frequent.Key, 0, 0, 1)).Artists
                    .FirstOrDefault(artist => string.Equals(artist.Name, frequent.Key, StringComparison.OrdinalIgnoreCase)) : null;
                var provider = seed?.ExternalProvider ?? found?.ExternalProvider;
                var id = seed?.ArtistId ?? found?.ExternalId;
                if (provider == null || id == null) continue;
                var artist = LocalLibraryService.ParseExternalResource(id);
                foreach (var album in await providers.GetArtistAlbumsAsync(context, provider, artist.isExternal ? artist.externalId! : id))
                    if (ReleaseDate(album.ReleaseDate) is { } at && at >= clock.UtcNow.AddDays(-PersonalMixRules.ReleaseDays) &&
                        album.ExclusionReason == null && album.ReleaseType is not ("compilation" or "reissue" or "remaster"))
                        albums.Add((album.ExternalProvider ?? provider, album.ExternalId ?? album.Id, at));
            }
            catch (Exception) when (!token.IsCancellationRequested) { }
        }
        foreach (var album in albums.Distinct().OrderBy(item => item.Provider, StringComparer.Ordinal).ThenBy(item => item.Id, StringComparer.Ordinal).Take(20))
        {
            try
            {
                var found = await providers.GetAlbumAsync(context, album.Provider, album.Id);
                if (found != null) rows.AddRange(found.Songs.Select(song => (song, "release", (DateTimeOffset?)album.At)));
            }
            catch (Exception) when (!token.IsCancellationRequested) { }
        }
        var unique = new List<(Song Song, string Source, DateTimeOffset? Release)>();
        foreach (var row in rows.OrderByDescending(item => item.Release).ThenByDescending(item => item.Song.IsLocal).ThenBy(item => item.Song.Id, StringComparer.Ordinal))
            if (unique.Count < 1000 && !unique.Any(other => MusicMixService.SameRadioRecording(row.Song, other.Song))) unique.Add(row);
        var songs = unique.ToArray();
        var profiles = await tags.GetTags(songs.Select(item => item.Song.CanonicalRecordingId).OfType<Guid>().Distinct().ToArray(), token);
        var scores = await taste.ScoreManyAsync(user.Id, songs.Select(item => item.Song).ToArray(), token);
        var listening = events.SelectMany(play => PersonalMixRules.IdentityKeys(new Song
        {
            Id = play.TrackReference,
            CanonicalRecordingId = play.CanonicalRecordingId,
            Title = play.Title ?? "",
            Artist = play.Artist ?? "",
            Isrc = play.Isrc
        })
            .Select(key => (Key: key, Play: play))).GroupBy(item => item.Key)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Play).ToArray());
        var result = new List<MixCandidate>();
        foreach (var row in songs)
        {
            var score = scores[row.Song];
            if (score.Blocked || context.Policy.Includes(row.Song) == false || string.IsNullOrWhiteSpace(row.Song.Id) ||
                string.IsNullOrWhiteSpace(row.Song.Title) || string.IsNullOrWhiteSpace(row.Song.Artist)) continue;
            try { if (!row.Song.IsLocal && !await providers.CanPlaySongAsync(context, row.Song)) continue; }
            catch (Exception) when (!token.IsCancellationRequested) { continue; }
            var matched = PersonalMixRules.IdentityKeys(row.Song).SelectMany(key => listening.GetValueOrDefault(key) ?? [])
                .DistinctBy(play => play.Id).ToArray();
            var played = matched.Where(play => play.State == ListeningEventState.Completed).ToArray();
            var skipped = matched.Any(play => play.State == ListeningEventState.Skipped && play.UpdatedAt >= clock.UtcNow.AddDays(-PersonalMixRules.SkipDays) &&
                play.PositionTicks is { } ticks && (ticks < TimeSpan.FromSeconds(30).Ticks || ticks / 10000d < play.DurationMilliseconds * .25));
            var features = row.Song.CanonicalRecordingId is { } id && profiles.TryGetValue(id, out var profile)
                ? profile.Tags.Where(item => item.Score > 0).ToDictionary(item => item.Tag, item => item.Score) : new Dictionary<string, double>();
            result.Add(new(row.Song.WithTasteReason(row.Song.TasteReason ?? score.Reasons.FirstOrDefault()), features,
                played.Length, played.Count(play => play.ListenedAt >= clock.UtcNow.AddDays(-30)),
                matched.Select(play => (DateTimeOffset?)(play.ListenedAt ?? play.UpdatedAt)).DefaultIfEmpty(hearts.FirstOrDefault(item => item.ItemId == row.Song.Id)?.FavoritedAt).Max(),
                skipped, row.Source, score.Score, row.Release, matched.Length > 0, hearts.Any(item => item.ItemId == row.Song.Id)));
        }
        return result;
    }

    private DateTimeOffset? ReleaseDate(string? value) => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
        DateTimeStyles.None, out var date) && date <= DateOnly.FromDateTime(clock.UtcNow.UtcDateTime)
        ? new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null;
}

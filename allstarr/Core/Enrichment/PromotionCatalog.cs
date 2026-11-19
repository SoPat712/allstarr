using System.Text.Json;
using allstarr.Core.Matching;
using allstarr.Core.Providers.AppleMusicKit;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Services.Common;
using allstarr.Services.Local;
using allstarr.Services.MusicBrainz;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Enrichment;

public interface IPromotionCatalog
{
    Task<IReadOnlyList<Song>> ResolveAsync(ProtocolExecutionContext context, Guid recordingId, string unit, CancellationToken token);
}

public sealed class PromotionCatalog(IDbContextFactory<AllstarrDbContext> factory, ITrackIdentityService identities,
    IProtocolProviderGateway providers, IMusicBrainzCatalogClient musicBrainz) : IPromotionCatalog
{
    public async Task<IReadOnlyList<Song>> ResolveAsync(ProtocolExecutionContext context, Guid recordingId, string unit, CancellationToken token)
    {
        var recording = await identities.ResolveRecordingAsync(context.RequireActor(), recordingId.ToString("D"), token)
            ?? throw new KeyNotFoundException("The recording is unavailable.");
        var stored = await identities.GetStoredSongsAsync(context.RequireActor(), recordingId.ToString("D"), token);
        var song = stored.FirstOrDefault(item => !item.IsLocal && !string.IsNullOrWhiteSpace(item.AlbumId)) ??
            await providers.GetSongAsync(context, "allstarr", recordingId.ToString("D")) ??
            throw new InvalidOperationException("The recording has no release metadata.");
        if (NeedsCurrentMetadata(song) && !song.IsLocal &&
            song.ExternalProvider is { } source && !string.Equals(source, "allstarr", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(song.ExternalId))
        {
            var current = await providers.GetExactSongAsync(context, source, song.ExternalId);
            if (current != null)
            {
                var cached = song;
                song = current;
                if (SameIdentity(cached, current)) MergeMissing(song, cached, allowTrack: true);
            }
        }
        song.CanonicalRecordingId = recording.Id;
        song.Isrc ??= recording.Isrc;
        if (recording.MusicBrainzRecordingId != null) song.ProviderIds["musicbrainz_recordingid"] = recording.MusicBrainzRecordingId;
        var releaseId = song.ProviderIds.GetValueOrDefault("musicbrainz_releaseid") ?? song.ProviderIds.GetValueOrDefault("musicbrainz_albumid");
        if (releaseId == null)
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var tagsJson = await (from route in db.Set<allstarr.Core.ManagedFiles.RecordingKeptRouteEntity>()
                                  join application in db.MetadataEnrichmentApplications on route.ManagedFileId equals application.ManagedArtifactId
                                  join plan in db.MetadataEnrichmentPlans on application.PlanId equals plan.Id
                                  where route.CanonicalRecordingId == recording.Id && application.State == MetadataEnrichmentApplicationState.Applied
                                  orderby plan.CreatedAt descending
                                  select plan.TagsJson).FirstOrDefaultAsync(token);
            if (tagsJson != null)
            {
                var tags = JsonSerializer.Deserialize<Dictionary<string, string>>(tagsJson);
                if (tags?.GetValueOrDefault("musicbrainz_recordingid") == recording.MusicBrainzRecordingId)
                    releaseId = tags?.GetValueOrDefault("musicbrainz_releaseid");
            }
        }
        Album? providerAlbum = null;
        if (song.ExternalProvider != null && !string.IsNullOrWhiteSpace(song.AlbumId))
        {
            var album = LocalLibraryService.ParseExternalResource(song.AlbumId);
            providerAlbum = album is { isExternal: true, type: "album" }
                ? await providers.GetAlbumAsync(context, album.provider!, album.externalId!)
                : await providers.GetAlbumAsync(context, song.ExternalProvider, song.AlbumId);
            if (providerAlbum != null) InheritAlbumMetadata(providerAlbum);
        }
        // Without a release named for the song, its ISRC names the recording on MusicBrainz, and the one release of that
        // recording that agrees with the album the service gave is tagged from. The songs stay the service's own, with
        // the ids that play them; they take the release's names, places and identities.
        var found = releaseId == null ? await FindReleaseAsync(song, providerAlbum, token) : null;
        if (Guid.TryParse(releaseId, out var parsed))
        {
            var release = await musicBrainz.LookupReleaseByMbidAsync(parsed.ToString("D"), token)
                ?? throw new InvalidOperationException("The selected release is unavailable.");
            var media = release.Media ?? [];
            if (media.Count == 0 || media.Any(disc => disc.Tracks == null || disc.Tracks.Count != disc.TrackCount))
                throw new InvalidOperationException("The selected release tracklist is incomplete.");
            var tracks = new List<Song>();
            foreach (var disc in media.OrderBy(item => item.Position))
                foreach (var item in disc.Tracks!.OrderBy(item => item.Position))
                {
                    var mbid = item.Recording?.Id;
                    if (!Guid.TryParse(mbid, out _)) throw new InvalidOperationException("A release track has no recording identity.");
                    var isrc = item.Recording?.Isrcs?.FirstOrDefault();
                    var known = providerAlbum?.Songs.FirstOrDefault(candidate => isrc != null &&
                        string.Equals(candidate.Isrc, isrc, StringComparison.OrdinalIgnoreCase));
                    var id = mbid == recording.MusicBrainzRecordingId ? recording.Id :
                        (await identities.CreateRecordingAsync(context.RequireActor(), context.CorrelationId, isrc, mbid, token)).Recording.Id;
                    var track = known ?? new Song();
                    track.CanonicalRecordingId = id;
                    track.Isrc = isrc ?? track.Isrc;
                    track.Title = item.Title ?? item.Recording?.Title ?? track.Title;
                    track.Artist = Credit(item.ArtistCredit ?? item.Recording?.ArtistCredit ?? release.ArtistCredit);
                    track.AlbumArtist = Credit(release.ArtistCredit);
                    track.Album = release.Title ?? song.Album;
                    track.Track = item.Position;
                    track.TotalTracks = disc.TrackCount;
                    track.DiscNumber = disc.Position;
                    track.TotalDiscs = media.Count;
                    track.ReleaseDate = release.Date;
                    track.Year = int.TryParse(release.Date?.Split('-')[0], out var year) ? year : null;
                    track.Duration = (item.Length ?? item.Recording?.Length) / 1000;
                    track.ProviderIds["musicbrainz_recordingid"] = mbid!;
                    Identify(track, release, disc, item);
                    track.ProviderIds["musicbrainz_releaseid"] = parsed.ToString("D");
                    track.ProviderIds["musicbrainz_albumid"] = parsed.ToString("D");
                    tracks.Add(track);
                }
            if (!tracks.Any(item => item.CanonicalRecordingId == recording.Id))
                throw new InvalidOperationException("The selected release does not contain this recording.");
            return unit == "song" ? tracks.Where(item => item.CanonicalRecordingId == recording.Id).Take(1).ToArray() : tracks;
        }
        if (providerAlbum == null || providerAlbum.Songs.Count == 0)
        {
            if (unit != "song") throw new PromotionException(PromotionFailure.ReleaseMetadataUnavailable);
            Name([song], song, found);
            return [song];
        }
        if (unit != "song" && providerAlbum.SongCount is > 0 && providerAlbum.SongCount != providerAlbum.Songs.Count)
            throw new PromotionException(PromotionFailure.ReleaseMetadataUnavailable);

        var selected = SelectTrack(providerAlbum.Songs, song, recording.Isrc ?? song.Isrc);
        if (selected != null)
        {
            if (SameIdentity(selected, song)) MergeMissing(selected, song, allowTrack: true);
            selected.CanonicalRecordingId = recording.Id;
        }

        if (unit == "song")
        {
            if (selected == null) throw new PromotionException(PromotionFailure.ReleaseMetadataUnavailable);
            Name([selected], selected, found);
            return [selected];
        }

        foreach (var track in providerAlbum.Songs)
        {
            if (track == selected) continue;
            if (track.CanonicalRecordingId == null && track.ExternalProvider != null && track.ExternalId != null)
                track.CanonicalRecordingId = (await identities.ResolveRecordingAsync(context.RequireActor(), track.Id, token))?.Id;
            if (track.CanonicalRecordingId == null)
                track.CanonicalRecordingId = (await identities.CreateRecordingAsync(context.RequireActor(), context.CorrelationId,
                    track.Isrc, track.ProviderIds.GetValueOrDefault("musicbrainz_recordingid"), token)).Recording.Id;
        }
        if (selected == null)
            throw new PromotionException(PromotionFailure.ReleaseMetadataUnavailable);
        Name(providerAlbum.Songs, selected, found);
        return providerAlbum.Songs;
    }

    private const int MaximumReleaseLookups = 4;

    private sealed record FoundRelease(MusicBrainzRelease Release, string RecordingId, MusicBrainzMedium Disc, MusicBrainzReleaseTrack Track);

    private async Task<FoundRelease?> FindReleaseAsync(Song song, Album? providerAlbum, CancellationToken token)
    {
        var album = FuzzyMatcher.NormalizeAlbumForMatching(song.Album);
        if (string.IsNullOrWhiteSpace(song.Isrc) || album.Length == 0) return null;
        try
        {
            // The ISRC names the recording; the releases it is on come with the recording itself.
            var named = await musicBrainz.LookupByIsrcAsync(song.Isrc, token);
            var recording = Guid.TryParse(named?.Id, out _) ? await musicBrainz.LookupByMbidAsync(named!.Id!, token) : null;
            if (!Guid.TryParse(recording?.Id, out _) || recording!.Releases is not { Count: > 0 } releases) return null;
            var total = providerAlbum?.SongCount is > 0 and var counted ? counted : providerAlbum?.Songs.Count is > 0 and var listed ? listed : song.TotalTracks;
            // The service's own release day first, then a worldwide release, then the earliest: one answer however they are listed.
            var candidates = releases.Where(release => Guid.TryParse(release.Id, out _) &&
                    FuzzyMatcher.NormalizeAlbumForMatching(release.Title) == album &&
                    (release.Status == null || release.Status.Equals("Official", StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(release => song.ReleaseDate != null && release.Date == song.ReleaseDate)
                .ThenByDescending(release => song.Year is { } year && release.Date?.StartsWith(year.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal) == true)
                .ThenByDescending(release => release.Country == "XW")
                .ThenBy(release => release.Date ?? "9999", StringComparer.Ordinal).ThenBy(release => release.Id, StringComparer.Ordinal)
                .Take(MaximumReleaseLookups).ToArray();
            foreach (var candidate in candidates)
            {
                var release = await musicBrainz.LookupReleaseByMbidAsync(candidate.Id!, token);
                var media = release?.Media ?? [];
                if (media.Count == 0 || media.Any(disc => disc.Tracks == null || disc.Tracks.Count != disc.TrackCount)) continue;
                var places = media.SelectMany(disc => disc.Tracks!.Select(track => (Disc: disc, Track: track)))
                    .Where(place => place.Track.Recording?.Id == recording.Id).ToArray();
                // The song is on it once, where the service says it is, among as many songs.
                if (places.Length != 1 || song.Track is > 0 && places[0].Track.Position != song.Track ||
                    total is > 0 && media.Sum(disc => disc.TrackCount) != total) continue;
                return new(release!, recording.Id!, places[0].Disc, places[0].Track);
            }
        }
        // MusicBrainz not answering leaves the service's own tags; it never stops an add.
        catch (Exception error) when (error is MusicBrainzLookupException or HttpRequestException or System.Text.Json.JsonException) { }
        return null;
    }

    // The service's songs take the found release's names and identities: the song the release was found by, and, for a
    // whole album, every other song at the same place with the same title. An album where any song fits nowhere keeps
    // the service's tags for all of them, so one album is never tagged two ways.
    private static void Name(IReadOnlyList<Song> songs, Song selected, FoundRelease? found)
    {
        if (found == null) return;
        var places = found.Release.Media!.SelectMany(disc => disc.Tracks!.Select(item => (Disc: disc, Item: item))).ToArray();
        var placed = songs.Select(song => (Song: song, Place: song == selected ? (found.Disc, found.Track) :
            places.Where(place => place.Disc.Position == (song.DiscNumber ?? 1) && place.Item.Position == song.Track &&
                    FuzzyMatcher.NormalizeTitleForMatching(place.Item.Title ?? place.Item.Recording?.Title ?? "", song.Artist) ==
                    FuzzyMatcher.NormalizeTitleForMatching(song.Title, song.Artist))
                .Select(place => ((MusicBrainzMedium Disc, MusicBrainzReleaseTrack Item)?)place).FirstOrDefault())).ToArray();
        if (placed.Any(item => item.Place == null) || placed.Select(item => item.Place!.Value.Item).Distinct().Count() != placed.Length) return;
        var release = found.Release;
        foreach (var (song, place) in placed)
        {
            var (disc, item) = place!.Value;
            song.Title = item.Title ?? item.Recording?.Title ?? song.Title;
            song.Album = release.Title ?? song.Album;
            song.Track = item.Position;
            song.TotalTracks = disc.TrackCount;
            song.DiscNumber = disc.Position;
            song.TotalDiscs = release.Media!.Count;
            if (release.Date is { Length: > 0 } date)
            {
                song.ReleaseDate = date;
                song.Year = ReleaseYear(date) ?? song.Year;
            }
            if (Guid.TryParse(item.Recording?.Id, out _)) song.ProviderIds["musicbrainz_recordingid"] = item.Recording!.Id!;
            Identify(song, release, disc, item);
        }
    }

    // What a release says about one of its songs, as the tags a music tagger writes.
    private static void Identify(Song track, MusicBrainzRelease release, MusicBrainzMedium disc, MusicBrainzReleaseTrack item)
    {
        var credit = item.ArtistCredit ?? item.Recording?.ArtistCredit ?? release.ArtistCredit;
        if (Guid.TryParse(release.Id, out var releaseId))
        {
            track.ProviderIds["musicbrainz_releaseid"] = releaseId.ToString("D");
            track.ProviderIds["musicbrainz_albumid"] = releaseId.ToString("D");
        }
        Add(track, "musicbrainz_releasegroupid", release.ReleaseGroup?.Id);
        Add(track, "musicbrainz_artistid", credit?.FirstOrDefault()?.Artist?.Id);
        Add(track, "musicbrainz_albumartistid", release.ArtistCredit?.FirstOrDefault()?.Artist?.Id);
        Add(track, "musicbrainz_releasetrackid", item.Id);
        Add(track, "originaldate", release.ReleaseGroup?.FirstReleaseDate);
        Add(track, "originalyear", ReleaseYear(release.ReleaseGroup?.FirstReleaseDate)?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add(track, "releasecountry", release.Country);
        Add(track, "releasestatus", release.Status?.ToLowerInvariant());
        Add(track, "releasetype", string.Join(';', new[] { release.ReleaseGroup?.PrimaryType }.Concat(release.ReleaseGroup?.SecondaryTypes ?? [])
            .OfType<string>().Where(type => type.Length > 0).Select(type => type.ToLowerInvariant())));
        Add(track, "media", disc.Format);
        Add(track, "barcode", release.Barcode);
        Add(track, "label", release.LabelInfo?.Select(info => info.Label?.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)));
        Add(track, "catalognumber", release.LabelInfo?.Select(info => info.CatalogNumber).FirstOrDefault(number => !string.IsNullOrWhiteSpace(number)));
        Add(track, "artists", string.Join(';', (credit ?? []).Select(part => part.Name ?? part.Artist?.Name).OfType<string>().Where(name => name.Length > 0)));
        Add(track, "artistsort", SortCredit(credit));
        Add(track, "albumartistsort", SortCredit(release.ArtistCredit));
    }

    private static string SortCredit(IEnumerable<MusicBrainzArtistCredit>? credits) =>
        string.Concat((credits ?? []).Select(item => (item.Artist?.SortName ?? item.Name ?? item.Artist?.Name ?? "") + item.JoinPhrase));

    private static bool NeedsCurrentMetadata(Song song) =>
        string.IsNullOrWhiteSpace(song.AlbumId) || song.Track is null or <= 0 || song.Year is null or <= 0;

    private static bool SameIdentity(Song left, Song right) =>
        !string.IsNullOrWhiteSpace(left.ExternalId) &&
        string.Equals(NormalizeProvider(left.ExternalProvider), NormalizeProvider(right.ExternalProvider), StringComparison.Ordinal) &&
        string.Equals(left.ExternalId, right.ExternalId, StringComparison.Ordinal);

    private static Song? SelectTrack(IReadOnlyList<Song> tracks, Song source, string? isrc)
    {
        var exact = tracks.Where(track => SameIdentity(track, source)).ToArray();
        if (exact.Length == 1) return exact[0];
        if (exact.Length > 1) return null;

        var byIsrc = string.IsNullOrWhiteSpace(isrc)
            ? []
            : tracks.Where(track => string.Equals(track.Isrc, isrc, StringComparison.OrdinalIgnoreCase)).ToArray();
        return byIsrc.Length == 1 ? byIsrc[0] : null;
    }

    private static void InheritAlbumMetadata(Album album)
    {
        foreach (var song in album.Songs)
        {
            if (string.IsNullOrWhiteSpace(song.Album)) song.Album = album.Title;
            if (string.IsNullOrWhiteSpace(song.AlbumId) && !string.IsNullOrWhiteSpace(album.Id)) song.AlbumId = album.Id;
            song.ReleaseDate ??= album.ReleaseDate;
            if (song.Year is null or <= 0) song.Year = album.Year ?? ReleaseYear(album.ReleaseDate);
            song.TotalTracks ??= album.SongCount;
            if (string.IsNullOrWhiteSpace(song.AlbumArtist) && !string.IsNullOrWhiteSpace(album.Artist))
                song.AlbumArtist = album.Artist;
            if (song.AlbumArtists.Count == 0 && album.Artists.Count > 0)
                song.AlbumArtists = album.Artists.ToList();
        }
    }

    private static void MergeMissing(Song target, Song source, bool allowTrack)
    {
        if (string.IsNullOrWhiteSpace(target.Title)) target.Title = source.Title;
        if (string.IsNullOrWhiteSpace(target.Artist)) target.Artist = source.Artist;
        if (string.IsNullOrWhiteSpace(target.Album)) target.Album = source.Album;
        target.AlbumId ??= source.AlbumId;
        target.ReleaseDate ??= source.ReleaseDate;
        if (target.Year is null or <= 0) target.Year = source.Year;
        target.AlbumArtist ??= source.AlbumArtist;
        if (target.AlbumArtists.Count == 0) target.AlbumArtists = source.AlbumArtists.ToList();
        target.Isrc ??= source.Isrc;
        foreach (var pair in source.ProviderIds) target.ProviderIds.TryAdd(pair.Key, pair.Value);
        if (allowTrack)
        {
            if (target.Track is null or <= 0) target.Track = source.Track;
            target.DiscNumber ??= source.DiscNumber;
            target.TotalTracks ??= source.TotalTracks;
            target.TotalDiscs ??= source.TotalDiscs;
        }
    }

    private static string NormalizeProvider(string? provider) =>
        AppleMusicClient.NormalizeProviderId(provider ?? "");

    private static int? ReleaseYear(string? date) =>
        date?.Length >= 4 && int.TryParse(date[..4], out var year) && year > 0 ? year : null;

    private static string Credit(IEnumerable<MusicBrainzArtistCredit>? credits) =>
        string.Concat((credits ?? []).Select(item => (item.Name ?? item.Artist?.Name ?? "") + item.JoinPhrase));
    private static void Add(Song song, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) song.ProviderIds[key] = value;
    }
}

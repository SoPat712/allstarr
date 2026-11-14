using System.Text.Json;
using allstarr.Core.Matching;
using allstarr.Core.Providers.AppleMusicKit;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
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
                    track.ProviderIds["musicbrainz_releaseid"] = parsed.ToString("D");
                    track.ProviderIds["musicbrainz_albumid"] = parsed.ToString("D");
                    Add(track, "musicbrainz_releasegroupid", release.ReleaseGroup?.Id);
                    Add(track, "musicbrainz_artistid", (item.ArtistCredit ?? item.Recording?.ArtistCredit ?? release.ArtistCredit)?.FirstOrDefault()?.Artist?.Id);
                    Add(track, "originaldate", release.ReleaseGroup?.FirstReleaseDate);
                    Add(track, "releasecountry", release.Country);
                    Add(track, "releasetype", release.ReleaseGroup?.PrimaryType);
                    Add(track, "media", disc.Format);
                    tracks.Add(track);
                }
            if (!tracks.Any(item => item.CanonicalRecordingId == recording.Id))
                throw new InvalidOperationException("The selected release does not contain this recording.");
            return unit == "song" ? tracks.Where(item => item.CanonicalRecordingId == recording.Id).Take(1).ToArray() : tracks;
        }
        if (providerAlbum == null || providerAlbum.Songs.Count == 0)
        {
            if (unit == "song") return [song];
            throw new PromotionException(PromotionFailure.ReleaseMetadataUnavailable);
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
        return providerAlbum.Songs;
    }

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

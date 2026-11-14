using System.Text.Json;
using allstarr.Core.Matching;
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
        if (unit == "song") return [song];
        if (providerAlbum == null || providerAlbum.Songs.Count == 0 ||
            providerAlbum.SongCount is > 0 && providerAlbum.SongCount != providerAlbum.Songs.Count)
            throw new InvalidOperationException("A complete provider album tracklist is required.");
        foreach (var track in providerAlbum.Songs)
        {
            if (track.ExternalProvider == song.ExternalProvider && track.ExternalId == song.ExternalId ||
                !string.IsNullOrWhiteSpace(recording.Isrc) && string.Equals(track.Isrc, recording.Isrc, StringComparison.OrdinalIgnoreCase))
                track.CanonicalRecordingId = recording.Id;
            if (track.CanonicalRecordingId == null && track.ExternalProvider != null && track.ExternalId != null)
                track.CanonicalRecordingId = (await identities.ResolveRecordingAsync(context.RequireActor(), track.Id, token))?.Id;
            if (track.CanonicalRecordingId == null)
                track.CanonicalRecordingId = (await identities.CreateRecordingAsync(context.RequireActor(), context.CorrelationId,
                    track.Isrc, track.ProviderIds.GetValueOrDefault("musicbrainz_recordingid"), token)).Recording.Id;
        }
        if (!providerAlbum.Songs.Any(item => item.CanonicalRecordingId == recording.Id))
            throw new InvalidOperationException("The provider album does not contain this recording.");
        return providerAlbum.Songs;
    }

    private static string Credit(IEnumerable<MusicBrainzArtistCredit>? credits) =>
        string.Concat((credits ?? []).Select(item => (item.Name ?? item.Artist?.Name ?? "") + item.JoinPhrase));
    private static void Add(Song song, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) song.ProviderIds[key] = value;
    }
}

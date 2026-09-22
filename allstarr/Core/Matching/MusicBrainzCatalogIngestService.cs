using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Storage;
using allstarr.Services.MusicBrainz;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Matching;

public sealed record MusicBrainzCatalogGraph(
    MusicBrainzRelease Release,
    MusicBrainzReleaseGroup ReleaseGroup,
    IReadOnlyCollection<MusicBrainzArtist> Artists);

public sealed record MusicBrainzCatalogSource(
    string SourceId,
    string SourceRevision,
    DateTimeOffset ObservedAt,
    DateTimeOffset RefreshAfter);

public sealed record MusicBrainzCatalogIngestResult(
    Guid ReleaseGroupId,
    Guid ReleaseId,
    int ArtistCount,
    int RecordingCount,
    int ReleaseTrackCount,
    int EntitiesCreated,
    CanonicalCatalogEvidenceResult Evidence);

public interface IMusicBrainzCatalogIngestService
{
    Task<MusicBrainzCatalogIngestResult> IngestAsync(
        ProviderActorContext actor,
        MusicBrainzCatalogGraph graph,
        MusicBrainzCatalogSource source,
        CancellationToken cancellationToken = default);
}

public sealed class MusicBrainzCatalogIngestService(
    IDbContextFactory<AllstarrDbContext> contextFactory,
    DurableStorageState storageState) : IMusicBrainzCatalogIngestService
{
    public async Task<MusicBrainzCatalogIngestResult> IngestAsync(
        ProviderActorContext actor,
        MusicBrainzCatalogGraph graph,
        MusicBrainzCatalogSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureStorageReady();
        var ownerUserId = actor.EffectiveUserId ??
            throw new UnauthorizedAccessException("Catalog ingestion requires a scoped user.");
        var releaseMbid = Mbid(graph.Release.Id, "release");
        var releaseGroupMbid = Mbid(graph.ReleaseGroup.Id, "release group");
        if (!string.Equals(graph.Release.ReleaseGroup?.Id, releaseGroupMbid, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The release does not belong to the supplied release group.", nameof(graph));
        ValidateSource(source);
        ValidateTracks(graph.Release.Media);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        if (!await db.Users.AnyAsync(item =>
                item.TenantId == actor.TenantId &&
                item.Id == ownerUserId &&
                item.Status == PlatformUserStatus.Active,
                cancellationToken))
            throw new UnauthorizedAccessException("The catalog owner is not active in the requested tenant.");

        var created = 0;
        var artistsByMbid = new Dictionary<string, CanonicalArtistRecord>(StringComparer.OrdinalIgnoreCase);
        var suppliedArtists = graph.Artists
            .Where(item => Guid.TryParse(item.Id, out _))
            .GroupBy(item => item.Id!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        async Task<CanonicalArtistRecord> ResolveArtistAsync(MusicBrainzArtistCredit credit)
        {
            var embedded = credit.Artist ?? throw new ArgumentException("Artist credit is missing its artist identity.", nameof(graph));
            var mbid = Mbid(embedded.Id, "artist");
            if (artistsByMbid.TryGetValue(mbid, out var cached)) return cached;
            var details = suppliedArtists.GetValueOrDefault(mbid) ?? embedded;
            var existing = await db.CanonicalArtists.SingleOrDefaultAsync(item =>
                item.TenantId == actor.TenantId && item.MusicBrainzArtistId == mbid, cancellationToken);
            if (existing == null)
            {
                existing = new CanonicalArtistRecord
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = actor.TenantId,
                    MusicBrainzArtistId = mbid,
                    CreatedAt = source.ObservedAt,
                    Revision = 1
                };
                db.CanonicalArtists.Add(existing);
                created++;
            }
            ApplyArtist(existing, details, source.ObservedAt);
            artistsByMbid.Add(mbid, existing);
            return existing;
        }

        var releaseCredits = Credits(
            graph.ReleaseGroup.ArtistCredit,
            graph.Release.ArtistCredit,
            Tracks(graph.Release.Media).SelectMany(track =>
                Credits(track.ArtistCredit, track.Recording?.ArtistCredit)));
        foreach (var credit in releaseCredits) await ResolveArtistAsync(credit);
        foreach (var track in Tracks(graph.Release.Media))
            foreach (var credit in Credits(track.ArtistCredit, track.Recording?.ArtistCredit, releaseCredits))
                await ResolveArtistAsync(credit);

        var releaseGroup = await db.CanonicalReleaseGroups.SingleOrDefaultAsync(item =>
            item.TenantId == actor.TenantId && item.MusicBrainzReleaseGroupId == releaseGroupMbid,
            cancellationToken);
        if (releaseGroup == null)
        {
            releaseGroup = new CanonicalReleaseGroupRecord
            {
                Id = Guid.CreateVersion7(),
                TenantId = actor.TenantId,
                MusicBrainzReleaseGroupId = releaseGroupMbid,
                CreatedAt = source.ObservedAt,
                Revision = 1
            };
            db.CanonicalReleaseGroups.Add(releaseGroup);
            created++;
        }
        ApplyReleaseGroup(releaseGroup, graph.ReleaseGroup, source.ObservedAt);

        var release = await db.CanonicalReleases.SingleOrDefaultAsync(item =>
            item.TenantId == actor.TenantId && item.MusicBrainzReleaseId == releaseMbid,
            cancellationToken);
        if (release == null)
        {
            release = new CanonicalReleaseRecord
            {
                Id = Guid.CreateVersion7(),
                TenantId = actor.TenantId,
                MusicBrainzReleaseId = releaseMbid,
                CreatedAt = source.ObservedAt,
                Revision = 1
            };
            db.CanonicalReleases.Add(release);
            created++;
        }
        ApplyRelease(release, releaseGroup.Id, graph.Release, source.ObservedAt);

        var recordings = new Dictionary<string, CanonicalRecordingRecord>(StringComparer.OrdinalIgnoreCase);
        var releaseTracks = new List<(CanonicalReleaseTrackRecord Entity, MusicBrainzReleaseTrack Source)>();
        foreach (var (medium, track) in TracksWithMedia(graph.Release.Media))
        {
            var recordingSource = track.Recording!;
            var recordingMbid = Mbid(recordingSource.Id, "recording");
            if (!recordings.TryGetValue(recordingMbid, out var recording))
            {
                recording = await db.CanonicalRecordings.SingleOrDefaultAsync(item =>
                    item.TenantId == actor.TenantId && item.MusicBrainzRecordingId == recordingMbid,
                    cancellationToken);
                if (recording == null)
                {
                    recording = new CanonicalRecordingRecord
                    {
                        Id = Guid.CreateVersion7(),
                        TenantId = actor.TenantId,
                        CreatedByUserId = ownerUserId,
                        MusicBrainzRecordingId = recordingMbid,
                        CreatedAt = source.ObservedAt,
                        Revision = 1
                    };
                    db.CanonicalRecordings.Add(recording);
                    created++;
                }
                ApplyRecording(recording, recordingSource, track, source.ObservedAt);
                recordings.Add(recordingMbid, recording);
            }

            var trackMbid = Mbid(track.Id, "release track");
            var releaseTrack = await db.CanonicalReleaseTracks.SingleOrDefaultAsync(item =>
                item.TenantId == actor.TenantId && item.MusicBrainzTrackId == trackMbid,
                cancellationToken);
            if (releaseTrack == null)
            {
                releaseTrack = new CanonicalReleaseTrackRecord
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = actor.TenantId,
                    MusicBrainzTrackId = trackMbid,
                    CreatedAt = source.ObservedAt,
                    Revision = 1
                };
                db.CanonicalReleaseTracks.Add(releaseTrack);
                created++;
            }
            ApplyReleaseTrack(releaseTrack, release.Id, recording.Id, medium.Position, track, source.ObservedAt);
            releaseTracks.Add((releaseTrack, track));
        }

        await ReplaceReleaseGroupCreditsAsync(db, actor.TenantId, releaseGroup.Id, releaseCredits, artistsByMbid, cancellationToken);
        foreach (var pair in recordings)
        {
            var sourceRecording = releaseTracks.First(item =>
                string.Equals(item.Source.Recording!.Id, pair.Key, StringComparison.OrdinalIgnoreCase)).Source.Recording!;
            var track = releaseTracks.First(item =>
                string.Equals(item.Source.Recording!.Id, pair.Key, StringComparison.OrdinalIgnoreCase)).Source;
            await ReplaceRecordingCreditsAsync(
                db,
                actor.TenantId,
                pair.Value.Id,
                Credits(track.ArtistCredit, sourceRecording.ArtistCredit, releaseCredits),
                artistsByMbid,
                cancellationToken);
        }

        var evidence = new CanonicalCatalogEvidenceResult(0, 0, 0, 0);
        foreach (var artist in artistsByMbid.Values)
            evidence = Add(evidence, await RecordEvidenceAsync(db, actor, CanonicalCatalogEntityKind.Artist,
                artist.Id, artist.MusicBrainzArtistId!, Metadata(artist), source, cancellationToken));
        evidence = Add(evidence, await RecordEvidenceAsync(db, actor, CanonicalCatalogEntityKind.ReleaseGroup,
            releaseGroup.Id, releaseGroupMbid, Metadata(releaseGroup), source, cancellationToken));
        evidence = Add(evidence, await RecordEvidenceAsync(db, actor, CanonicalCatalogEntityKind.Release,
            release.Id, releaseMbid, Metadata(release), source, cancellationToken));
        foreach (var recording in recordings.Values)
            evidence = Add(evidence, await RecordEvidenceAsync(db, actor, CanonicalCatalogEntityKind.Recording,
                recording.Id, recording.MusicBrainzRecordingId!, Metadata(recording), source, cancellationToken));
        foreach (var (entity, _) in releaseTracks)
            evidence = Add(evidence, await RecordEvidenceAsync(db, actor, CanonicalCatalogEntityKind.ReleaseTrack,
                entity.Id, entity.MusicBrainzTrackId!, Metadata(entity), source, cancellationToken));

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new MusicBrainzCatalogIngestResult(
            releaseGroup.Id,
            release.Id,
            artistsByMbid.Count,
            recordings.Count,
            releaseTracks.Count,
            created,
            evidence);
    }

    private static async Task<CanonicalCatalogEvidenceResult> RecordEvidenceAsync(
        AllstarrDbContext db,
        ProviderActorContext actor,
        CanonicalCatalogEntityKind kind,
        Guid id,
        string mbid,
        object metadata,
        MusicBrainzCatalogSource source,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(metadata);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
        return await CanonicalCatalogEvidenceStore.RecordInContextAsync(
            db,
            actor,
            new CanonicalCatalogEntityReference(kind, id),
            new CanonicalCatalogSourceStamp(
                source.SourceId,
                source.SourceRevision,
                hash,
                1,
                source.ObservedAt,
                source.RefreshAfter),
            [new CanonicalCatalogAliasInput("musicbrainz", mbid)],
            [new CanonicalCatalogFactInput("metadata", json)],
            cancellationToken);
    }

    private static async Task ReplaceReleaseGroupCreditsAsync(
        AllstarrDbContext db,
        Guid tenantId,
        Guid releaseGroupId,
        IReadOnlyList<MusicBrainzArtistCredit> credits,
        IReadOnlyDictionary<string, CanonicalArtistRecord> artists,
        CancellationToken cancellationToken)
    {
        var current = await db.CanonicalReleaseGroupArtists.Where(item =>
            item.TenantId == tenantId && item.CanonicalReleaseGroupId == releaseGroupId).ToListAsync(cancellationToken);
        var desired = credits.Select((credit, position) => new CanonicalReleaseGroupArtistRecord
        {
            TenantId = tenantId,
            CanonicalReleaseGroupId = releaseGroupId,
            CanonicalArtistId = artists[Mbid(credit.Artist?.Id, "artist")].Id,
            Position = position,
            CreditName = Text(credit.Name, 500) ?? Text(credit.Artist?.Name, 500),
            JoinPhrase = Text(credit.JoinPhrase, 50) ?? string.Empty
        }).ToList();
        if (CreditsEqual(
                current.Select(item => (item.Position, item.CanonicalArtistId, item.CreditName, item.JoinPhrase)),
                desired.Select(item => (item.Position, item.CanonicalArtistId, item.CreditName, item.JoinPhrase))))
            return;
        db.CanonicalReleaseGroupArtists.RemoveRange(current);
        db.CanonicalReleaseGroupArtists.AddRange(desired);
    }

    private static async Task ReplaceRecordingCreditsAsync(
        AllstarrDbContext db,
        Guid tenantId,
        Guid recordingId,
        IReadOnlyList<MusicBrainzArtistCredit> credits,
        IReadOnlyDictionary<string, CanonicalArtistRecord> artists,
        CancellationToken cancellationToken)
    {
        var current = await db.CanonicalRecordingArtists.Where(item =>
            item.TenantId == tenantId && item.CanonicalRecordingId == recordingId).ToListAsync(cancellationToken);
        var desired = credits.Select((credit, position) => new CanonicalRecordingArtistRecord
        {
            TenantId = tenantId,
            CanonicalRecordingId = recordingId,
            CanonicalArtistId = artists[Mbid(credit.Artist?.Id, "artist")].Id,
            Position = position,
            CreditName = Text(credit.Name, 500) ?? Text(credit.Artist?.Name, 500),
            JoinPhrase = Text(credit.JoinPhrase, 50) ?? string.Empty
        }).ToList();
        if (CreditsEqual(
                current.Select(item => (item.Position, item.CanonicalArtistId, item.CreditName, item.JoinPhrase)),
                desired.Select(item => (item.Position, item.CanonicalArtistId, item.CreditName, item.JoinPhrase))))
            return;
        db.CanonicalRecordingArtists.RemoveRange(current);
        db.CanonicalRecordingArtists.AddRange(desired);
    }

    private static bool CreditsEqual(
        IEnumerable<(int Position, Guid ArtistId, string? Name, string Join)> first,
        IEnumerable<(int Position, Guid ArtistId, string? Name, string Join)> second) =>
        first.OrderBy(item => item.Position).SequenceEqual(second.OrderBy(item => item.Position));

    private static void ApplyArtist(CanonicalArtistRecord target, MusicBrainzArtist source, DateTimeOffset now)
    {
        var name = Required(source.Name, "artist name", 500);
        var changed = target.Name != name || target.SortName != (Text(source.SortName, 500) ?? name) ||
            target.Disambiguation != Text(source.Disambiguation, 500) || target.IsProvisional;
        target.Name = name;
        target.SortName = Text(source.SortName, 500) ?? name;
        target.Disambiguation = Text(source.Disambiguation, 500);
        target.IsProvisional = false;
        (target.Revision, target.UpdatedAt) = Touch(changed, target.Revision, target.UpdatedAt, now);
    }

    private static void ApplyReleaseGroup(CanonicalReleaseGroupRecord target, MusicBrainzReleaseGroup source, DateTimeOffset now)
    {
        var title = Required(source.Title, "release-group title", 500);
        var secondary = JsonSerializer.Serialize((source.SecondaryTypes ?? []).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray());
        var changed = target.Title != title || target.PrimaryType != Text(source.PrimaryType, 100) ||
            target.SecondaryTypesJson != secondary || target.FirstReleaseDate != Date(source.FirstReleaseDate) || target.IsProvisional;
        target.Title = title;
        target.PrimaryType = Text(source.PrimaryType, 100);
        target.SecondaryTypesJson = secondary;
        target.FirstReleaseDate = Date(source.FirstReleaseDate);
        target.IsProvisional = false;
        (target.Revision, target.UpdatedAt) = Touch(changed, target.Revision, target.UpdatedAt, now);
    }

    private static void ApplyRelease(CanonicalReleaseRecord target, Guid groupId, MusicBrainzRelease source, DateTimeOffset now)
    {
        var title = Required(source.Title, "release title", 500);
        var country = Text(source.Country, 2)?.ToUpperInvariant();
        var changed = target.CanonicalReleaseGroupId != groupId || target.Title != title ||
            target.Status != Text(source.Status, 100) || target.CountryCode != country ||
            target.ReleaseDate != Date(source.Date) || target.Barcode != Text(source.Barcode, 100) || target.IsProvisional;
        target.CanonicalReleaseGroupId = groupId;
        target.Title = title;
        target.Status = Text(source.Status, 100);
        target.CountryCode = country;
        target.ReleaseDate = Date(source.Date);
        target.Barcode = Text(source.Barcode, 100);
        target.IsProvisional = false;
        (target.Revision, target.UpdatedAt) = Touch(changed, target.Revision, target.UpdatedAt, now);
    }

    private static void ApplyRecording(CanonicalRecordingRecord target, MusicBrainzRecording source, MusicBrainzReleaseTrack track, DateTimeOffset now)
    {
        var title = Required(source.Title ?? track.Title, "recording title", 500);
        var duration = source.Length ?? track.Length;
        var isrc = source.Isrcs?.Select(value => value.Trim().ToUpperInvariant()).FirstOrDefault(value => value.Length == 12);
        var changed = target.Title != title || target.Disambiguation != Text(source.Disambiguation, 500) ||
            target.DurationMilliseconds != duration || target.Isrc != (isrc ?? target.Isrc) || target.IsProvisional;
        target.Title = title;
        target.Disambiguation = Text(source.Disambiguation, 500);
        target.DurationMilliseconds = duration;
        target.Isrc = isrc ?? target.Isrc;
        target.IsProvisional = false;
        (target.Revision, target.UpdatedAt) = Touch(changed, target.Revision, target.UpdatedAt, now);
    }

    private static void ApplyReleaseTrack(CanonicalReleaseTrackRecord target, Guid releaseId, Guid recordingId, int mediumPosition, MusicBrainzReleaseTrack source, DateTimeOffset now)
    {
        var title = Required(source.Title ?? source.Recording?.Title, "release-track title", 500);
        var changed = target.CanonicalReleaseId != releaseId || target.CanonicalRecordingId != recordingId ||
            target.MediumPosition != mediumPosition || target.TrackPosition != source.Position ||
            target.Title != title || target.DurationMilliseconds != (source.Length ?? source.Recording?.Length);
        target.CanonicalReleaseId = releaseId;
        target.CanonicalRecordingId = recordingId;
        target.MediumPosition = mediumPosition;
        target.TrackPosition = source.Position;
        target.Title = title;
        target.DurationMilliseconds = source.Length ?? source.Recording?.Length;
        (target.Revision, target.UpdatedAt) = Touch(changed, target.Revision, target.UpdatedAt, now);
    }

    private static (long Revision, DateTimeOffset UpdatedAt) Touch(
        bool changed,
        long revision,
        DateTimeOffset updatedAt,
        DateTimeOffset now) =>
        !changed && updatedAt != default
            ? (revision, updatedAt)
            : (updatedAt == default ? revision : revision + 1, now);

    private static object Metadata(CanonicalArtistRecord item) => new
    {
        item.Name,
        item.SortName,
        item.Disambiguation,
        item.MusicBrainzArtistId
    };

    private static object Metadata(CanonicalReleaseGroupRecord item) => new
    {
        item.Title,
        item.PrimaryType,
        item.SecondaryTypesJson,
        item.FirstReleaseDate,
        item.MusicBrainzReleaseGroupId
    };

    private static object Metadata(CanonicalReleaseRecord item) => new
    {
        item.Title,
        item.Status,
        item.CountryCode,
        item.ReleaseDate,
        item.Barcode,
        item.MusicBrainzReleaseId
    };

    private static object Metadata(CanonicalRecordingRecord item) => new
    {
        item.Title,
        item.Disambiguation,
        item.DurationMilliseconds,
        item.Isrc,
        item.MusicBrainzRecordingId
    };

    private static object Metadata(CanonicalReleaseTrackRecord item) => new
    {
        item.MediumPosition,
        item.TrackPosition,
        item.Title,
        item.DurationMilliseconds,
        item.MusicBrainzTrackId
    };

    private static IReadOnlyList<MusicBrainzArtistCredit> Credits(params IEnumerable<MusicBrainzArtistCredit>?[] candidates) =>
        candidates.FirstOrDefault(items => items?.Any() == true)?.ToArray() ?? [];

    private static IEnumerable<MusicBrainzReleaseTrack> Tracks(IEnumerable<MusicBrainzMedium>? media) =>
        TracksWithMedia(media).Select(item => item.Track);

    private static IEnumerable<(MusicBrainzMedium Medium, MusicBrainzReleaseTrack Track)> TracksWithMedia(IEnumerable<MusicBrainzMedium>? media) =>
        (media ?? []).SelectMany(medium => (medium.Tracks ?? []).Select(track => (medium, track)));

    private static void ValidateTracks(IEnumerable<MusicBrainzMedium>? media)
    {
        var tracks = TracksWithMedia(media).ToArray();
        if (tracks.Length == 0) throw new ArgumentException("The release has no media tracks.", nameof(media));
        if (tracks.Any(item => item.Medium.Position <= 0 || item.Track.Position <= 0 || item.Track.Recording == null))
            throw new ArgumentException("Every release track requires positive positions and a recording.", nameof(media));
        if (tracks.GroupBy(item => (item.Medium.Position, item.Track.Position)).Any(group => group.Count() > 1))
            throw new ArgumentException("Release-track positions must be unique.", nameof(media));
    }

    private static string Mbid(string? value, string field) =>
        Guid.TryParse(value, out var parsed) && parsed != Guid.Empty
            ? parsed.ToString("D")
            : throw new ArgumentException($"A valid MusicBrainz {field} ID is required.");

    private static string Required(string? value, string field, int maximum) =>
        Text(value, maximum) ?? throw new ArgumentException($"A {field} is required.");

    private static string? Text(string? value, int maximum)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return normalized is null || normalized.Length <= maximum
            ? normalized
            : throw new ArgumentOutOfRangeException(nameof(value));
    }

    private static string? Date(string? value)
    {
        var normalized = Text(value, 10);
        return normalized == null || normalized.Length is 4 or 7 or 10
            ? normalized
            : throw new ArgumentException("Catalog dates must use MusicBrainz partial ISO form.");
    }

    private static void ValidateSource(MusicBrainzCatalogSource source)
    {
        _ = Required(source.SourceId, "catalog source ID", 100);
        _ = Required(source.SourceRevision, "catalog source revision", 500);
        if (source.RefreshAfter < source.ObservedAt)
            throw new ArgumentException("Catalog refresh cannot precede observation.", nameof(source));
    }

    private static CanonicalCatalogEvidenceResult Add(CanonicalCatalogEvidenceResult first, CanonicalCatalogEvidenceResult second) => new(
        first.AliasesCreated + second.AliasesCreated,
        first.AliasesSeen + second.AliasesSeen,
        first.FactsCreated + second.FactsCreated,
        first.FactsSuperseded + second.FactsSuperseded);

    private void EnsureStorageReady()
    {
        if (storageState.GetSnapshot().Readiness != DurableStorageReadiness.Ready)
            throw new InvalidOperationException("Durable storage is not ready.");
    }
}

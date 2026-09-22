using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Services.MusicBrainz;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Matching;

public interface IMusicBrainzCatalogRefreshService
{
    Task<MusicBrainzCatalogIngestResult> RefreshReleaseAsync(
        ProviderActorContext actor,
        string releaseMbid,
        CancellationToken cancellationToken = default);
}

public sealed class MusicBrainzCatalogRefreshService(
    IMusicBrainzCatalogClient client,
    IMusicBrainzCatalogIngestService ingest,
    IPlatformClock clock) : IMusicBrainzCatalogRefreshService
{
    public const int MaximumArtistsPerRelease = 64;
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromDays(7);

    public async Task<MusicBrainzCatalogIngestResult> RefreshReleaseAsync(
        ProviderActorContext actor,
        string releaseMbid,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var release = await client.LookupReleaseByMbidAsync(releaseMbid, cancellationToken) ??
            throw new KeyNotFoundException("The catalog release does not exist.");
        var releaseGroupId = release.ReleaseGroup?.Id ??
            throw new InvalidDataException("The catalog release is missing its release-group identity.");
        var releaseGroup = await client.LookupReleaseGroupByMbidAsync(releaseGroupId, cancellationToken) ??
            throw new InvalidDataException("The catalog release group does not exist.");
        var artistIds = Credits(release, releaseGroup)
            .Select(credit => credit.Artist?.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (artistIds.Length == 0 || artistIds.Length > MaximumArtistsPerRelease)
            throw new InvalidDataException("The catalog release has an invalid artist-credit fan-out.");

        var artists = new List<MusicBrainzArtist>(artistIds.Length);
        foreach (var artistId in artistIds)
        {
            var artist = await client.LookupArtistByMbidAsync(artistId!, cancellationToken) ??
                throw new InvalidDataException("A credited catalog artist does not exist.");
            artists.Add(artist);
        }

        var observedAt = clock.UtcNow;
        return await ingest.IngestAsync(
            actor,
            new MusicBrainzCatalogGraph(release, releaseGroup, artists),
            new MusicBrainzCatalogSource(
                client.ConfiguredSourceId,
                client.ConfiguredSourceRevision,
                observedAt,
                observedAt.Add(RefreshInterval)),
            cancellationToken);
    }

    private static IEnumerable<MusicBrainzArtistCredit> Credits(
        MusicBrainzRelease release,
        MusicBrainzReleaseGroup releaseGroup) =>
        (releaseGroup.ArtistCredit ?? [])
        .Concat(release.ArtistCredit ?? [])
        .Concat((release.Media ?? [])
            .SelectMany(medium => medium.Tracks ?? [])
            .SelectMany(track => track.ArtistCredit ?? track.Recording?.ArtistCredit ?? []));
}

public sealed record MusicBrainzCatalogRefreshJobPayload(string ReleaseMbid);

public sealed class MusicBrainzCatalogRefreshJobHandler(
    IMusicBrainzCatalogRefreshService refresh) : IDurableJobHandler
{
    public const string Type = "catalog.musicbrainz.refresh-release";
    public string JobType => Type;

    public async Task<DurableJobCompletion> ExecuteAsync(
        DurableJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        MusicBrainzCatalogRefreshJobPayload? payload;
        try { payload = context.Claim.Payload.Deserialize<MusicBrainzCatalogRefreshJobPayload>(); }
        catch (JsonException) { payload = null; }
        if (payload == null ||
            !Guid.TryParse(payload.ReleaseMbid, out var releaseId) ||
            releaseId == Guid.Empty ||
            !context.Claim.TenantId.HasValue ||
            !context.Claim.OwnerUserId.HasValue)
            return DurableJobCompletion.Failure(
                "catalog_refresh_payload_invalid",
                "The canonical catalog refresh payload is invalid.");

        var actor = new ProviderActorContext(
            context.Claim.TenantId.Value,
            ProviderActorKind.SystemJob,
            null,
            durableJobId: context.Claim.JobId,
            actingForUserId: context.Claim.OwnerUserId.Value);
        try
        {
            await refresh.RefreshReleaseAsync(actor, releaseId.ToString("D"), cancellationToken);
            await context.ReportProgressAsync(new(
                "catalog.refresh",
                "Canonical release metadata refreshed.",
                1,
                1), cancellationToken);
            return DurableJobCompletion.Success();
        }
        catch (MusicBrainzLookupException error) when (error.Retryable)
        {
            return DurableJobCompletion.Retry(error.Code, error.Message, error.RetryAfter);
        }
        catch (MusicBrainzLookupException error)
        {
            return DurableJobCompletion.Failure(error.Code, error.Message);
        }
        catch (KeyNotFoundException)
        {
            return DurableJobCompletion.Failure(
                "catalog_release_missing",
                "The canonical catalog release no longer exists upstream.");
        }
        catch (InvalidDataException)
        {
            return DurableJobCompletion.Failure(
                "catalog_release_invalid",
                "The canonical catalog release hierarchy is incomplete.");
        }
        catch (DbUpdateException)
        {
            return DurableJobCompletion.Retry(
                "catalog_storage_conflict",
                "Canonical catalog storage changed during refresh.");
        }
    }
}

using System.Globalization;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Jobs;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Enrichment;

public sealed partial class LibraryPromotionService
{
    public async Task<PromotionListPage> ListAsync(ProtocolExecutionContext context, string state = "active",
        int limit = 30, string? cursor = null, CancellationToken token = default)
    {
        if (state is not ("active" or "needs-look" or "added" or "all") || limit is < 1 or > 100)
            throw new ArgumentException("Choose a valid promotion state and a limit between 1 and 100.");
        var position = DecodeCursor(cursor);
        await using var db = await factory.CreateDbContextAsync(token);
        await AuthorizeAsync(db, context, token);
        var actor = context.Principal!.UserId;
        var administrator = await db.Users.AnyAsync(item => item.Id == actor && item.IsAdmin, token);
        var runs = await db.Set<PromotionRunRecord>().AsNoTracking()
            .Where(item => administrator || item.OwnerUserId == actor).ToArrayAsync(token);
        var projected = await ProjectReceiptsAsync(db, runs, token);
        // A release can remain active while one of its other songs needs attention.
        var today = DateTimeOffset.UtcNow.Date;
        var counts = new PromotionListCounts(projected.Count(item => item.Active || item.NeedsLook > 0), projected.Count(item => item.NeedsLook > 0),
            projected.Count(item => item.State == "added"), projected.Count,
            projected.Count(item => item.State == "added" && item.FinishedAt?.UtcDateTime.Date == today));
        var ordered = projected.Where(item => state switch
            {
                "active" => item.Active || item.NeedsLook > 0,
                "needs-look" => item.NeedsLook > 0,
                "added" => item.State == "added",
                _ => true
            }).Where(item => position == null || item.StartedAt < position.Value.Time ||
                item.StartedAt == position.Value.Time && item.Id.CompareTo(position.Value.Id) < 0)
            .OrderByDescending(item => item.StartedAt).ThenByDescending(item => item.Id).Take(limit + 1).ToArray();
        var page = ordered.Take(limit).ToArray();
        foreach (var item in page) await ProjectNextSourcesAsync(item, runs.Single(run => run.Id == item.Id), token);
        return new(page, ordered.Length > limit ? EncodeCursor(page[^1]) : null, counts);
    }

    public async Task<PromotionReceipt> GetReceiptAsync(ProtocolExecutionContext context, Guid id,
        CancellationToken token = default)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var run = await OwnedAsync(db, context, id, token);
        var receipt = (await ProjectReceiptsAsync(db, [run], token)).Single();
        await ProjectNextSourcesAsync(receipt, run, token);
        return receipt;
    }

    public async Task<PromotionKeptPage> KeptAsync(ProtocolExecutionContext context, CancellationToken token = default)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        await AuthorizeAsync(db, context, token);
        var access = await libraryAccess.ResolveAsync(context, token);
        if (!access.Succeeded) throw new UnauthorizedAccessException();
        var libraries = access.LibraryIds.Order(StringComparer.Ordinal).ToArray();
        var library = libraries.FirstOrDefault();
        var profile = (await (profiles ?? new PromotionProfiles(factory)).GetMineAsync(context, token)).Values;
        var routes = await (from route in db.Set<RecordingKeptRouteEntity>().AsNoTracking()
                            join file in db.ManagedFiles.AsNoTracking() on route.ManagedFileId equals file.Id
                            where file.RemovedAt == null && file.IsManaged && (file.OwnerUserId == null || file.OwnerUserId == context.Principal!.UserId)
                            select new { route.CanonicalRecordingId, file.Id, file.TargetRootPath, file.CanonicalPath }).ToArrayAsync(token);
        var imported = await (from adoption in db.Set<LegacyMediaAdoptionRecord>().AsNoTracking()
                              join file in db.ManagedFiles.AsNoTracking() on adoption.ManagedFileId equals file.Id
                              where file.RemovedAt == null && adoption.CanonicalRecordingId != null &&
                                  (file.OwnerUserId == null || file.OwnerUserId == context.Principal!.UserId)
                              select new { file.Id, RecordingId = adoption.CanonicalRecordingId!.Value }).ToArrayAsync(token);
        var recordingIds = routes.Select(item => item.CanonicalRecordingId).Concat(imported.Select(item => item.RecordingId)).Distinct().ToArray();
        var linked = await db.LibraryTracks.AsNoTracking().Where(item => item.OwnerUserId == context.Principal!.UserId &&
            item.BackendInstanceId == context.BackendInstanceId && item.BackendLibraryId == library &&
            item.CanonicalRecordingId != null && recordingIds.Contains(item.CanonicalRecordingId.Value))
            .Select(item => item.CanonicalRecordingId!.Value).ToArrayAsync(token);
        var known = linked.ToHashSet();
        var adoptedFiles = imported.Select(item => item.Id).ToHashSet();
        var items = routes.Where(item => !adoptedFiles.Contains(item.Id))
            .Select(item => new PromotionKeptItem(Path.GetRelativePath(item.TargetRootPath, item.CanonicalPath),
                item.CanonicalRecordingId, library, known.Contains(item.CanonicalRecordingId)))
            .Concat(imported.Select(item => new PromotionKeptItem($"imported/{item.Id:D}", item.RecordingId,
                library, known.Contains(item.RecordingId)))).DistinctBy(item => item.Path).ToArray();
        return new(items, library, libraries, profile.LibraryRootId, items.Count(item => item.InLibrary));
    }

    private async Task<List<PromotionReceipt>> ProjectReceiptsAsync(AllstarrDbContext db,
        IReadOnlyList<PromotionRunRecord> runs, CancellationToken token)
    {
        if (runs.Count == 0) return [];
        var owners = runs.Select(item => item.OwnerUserId).Distinct().ToArray();
        var jobIds = runs.Select(item => item.JobId).ToArray();
        var relevantJobs = await db.Jobs.AsNoTracking().Where(item => jobIds.Contains(item.Id) ||
            owners.Contains(item.OwnerUserId ?? Guid.Empty) && (item.Type == "library.refresh" || item.Type == LibraryRecordingLinkQueue.JobType))
            .ToArrayAsync(token);
        var jobsById = relevantJobs.ToDictionary(item => item.Id);
        var runIds = runs.Select(item => item.Id).ToArray();
        var legacySteps = await db.Set<PromotionStepRecord>().AsNoTracking().Where(item => runIds.Contains(item.RunId)).ToArrayAsync(token);
        var users = await db.Users.AsNoTracking().Where(item => owners.Contains(item.Id)).ToDictionaryAsync(item => item.Id, token);
        var result = new List<PromotionReceipt>(runs.Count);
        foreach (var run in runs)
        {
            var request = JsonSerializer.Deserialize<PromoteSongRequest>(run.RequestJson)!;
            var album = ReadAlbum(run);
            var job = jobsById.GetValueOrDefault(run.JobId);
            if (run.Outcome is "Queued" or "Running" && job?.State is DurableJobState.Failed or DurableJobState.Cancelled)
            {
                run.Outcome = job.State == DurableJobState.Failed ? "Failed" : "Cancelled";
                run.Reason = job.LastErrorMessage ?? "The promotion job stopped before completion.";
            }
            var tracks = album?.Tracks ?? LegacyTracks(run, request);
            if (album == null)
            {
                tracks[0].Steps = legacySteps.Where(item => item.RunId == run.Id).Select(item => new PromotionTrackStep(item.Name, "Complete", item.Reason)).ToList();
                tracks[0].QualityCheck = allstarr.Core.Downloads.AudioQualityCheckJobHandler.ReadCheck(
                    legacySteps.FirstOrDefault(item => item.RunId == run.Id && item.Name == "Quality")?.ResultJson);
            }
            run.Tracks = tracks;
            var refresh = album?.RefreshJobId is { } refreshId ? jobsById.GetValueOrDefault(refreshId) :
                relevantJobs.FirstOrDefault(item => item.Type == "library.refresh" && item.OwnerUserId == run.OwnerUserId && item.IdempotencyKey == $"promotion:{run.Id:N}");
            var legacyRefresh = !tracks.Any(item => item.RefreshJobId != null);
            DateTimeOffset? handoverFinishedAt = null;
            foreach (var track in tracks.Where(item => item.ManagedFileId != null || item.Steps.Any(step => step.Name == "Dedupe")))
            {
                var trackRefresh = track.RefreshJobId is { } trackRefreshId ? jobsById.GetValueOrDefault(trackRefreshId) : legacyRefresh ? refresh : null;
                var link = trackRefresh == null ? null : relevantJobs.FirstOrDefault(item => item.Type == LibraryRecordingLinkQueue.JobType &&
                    item.OwnerUserId == run.OwnerUserId && item.IdempotencyKey == $"promotion-refresh:{trackRefresh.Id:N}");
                if (link?.State == DurableJobState.Succeeded && link.CompletedAt is { } completed &&
                    (handoverFinishedAt == null || completed > handoverFinishedAt)) handoverFinishedAt = completed;
                track.Steps.RemoveAll(item => item.Name is "Refresh" or "Handover");
                track.Steps.Add(new("Refresh", trackRefresh?.State == DurableJobState.Succeeded ? "Complete" : trackRefresh?.State.ToString() ?? "Pending",
                    trackRefresh?.State == DurableJobState.Succeeded ? "The backend accepted the refresh request." : "Waiting for the backend refresh request."));
                track.Steps.Add(new("Handover", link?.State == DurableJobState.Succeeded ? "Complete" : link?.State.ToString() ?? "Pending",
                    link?.State == DurableJobState.Succeeded ? "The refreshed index confirmed the library links." : "Waiting for confirmed library links."));
            }
            var added = tracks.Count(IsAdded);
            var needsLook = run.Outcome == "Cancelled" ? 0 : tracks.Count(track => !IsAdded(track) && (track.Outcome is "Needs review" or "Failed" ||
                track.Steps.Any(item => item.Name is "Refresh" or "Handover" && item.Outcome is "Failed" or "Cancelled") ||
                run.Outcome == "Failed" && track.Outcome != "Skipped"));
            var active = run.Outcome != "Cancelled" && tracks.Any(track => !IsAdded(track) &&
                track.Outcome is not ("Skipped" or "Needs review" or "Failed" or "Cancelled") &&
                !track.Steps.Any(item => item.Name is "Refresh" or "Handover" && item.Outcome is "Failed" or "Cancelled")) && run.Outcome != "Failed";
            var state = run.Outcome == "Cancelled" ? "cancelled" : added > 0 && tracks.All(track => IsAdded(track) || track.Outcome == "Skipped") ? "added" :
                needsLook > 0 ? "needs-look" : active ? "active" : "skipped";
            var opened = tracks.FirstOrDefault(item => item.RecordingId == request.RecordingId)?.Song ?? tracks.FirstOrDefault()?.Song ?? new Song();
            var summary = AlbumSummary(opened, tracks.Count);
            var unit = request.Unit ?? album?.Profile.DefaultUnit ?? "song";
            var finished = state == "added" ? handoverFinishedAt ?? job?.CompletedAt ?? run.CreatedAt :
                state is "cancelled" or "skipped" || run.Outcome == "Failed" ? job?.CompletedAt ?? job?.UpdatedAt ?? run.CreatedAt : (DateTimeOffset?)null;
            result.Add(new(run.Id, run.JobId, run.Step, run.Outcome, run.Reason, run.ManagedFileId,
                run.ApprovedByUserId, run.ApprovalReason, tracks, unit == "album" ? summary.Title : opened.Title,
                unit == "album" ? summary.Artist : opened.Artist, summary.CoverReference, summary, unit,
                tracks.Count, added, needsLook, state, active, run.CreatedAt, finished,
                new(run.OwnerUserId, users.GetValueOrDefault(run.OwnerUserId)?.DisplayName ?? ""), request.RecordingId,
                request.BackendLibraryId, request.RootId == Guid.Empty ? album?.Profile.LibraryRootId : request.RootId)
            { ProfileSteps = album == null ? [] : ProfileSteps(album.Profile) });
        }
        return result;
    }

    private static bool IsAdded(PromotionTrack track) => track.Steps.Any(item => item.Name == "Dedupe") ||
        track.ManagedFileId != null && track.Steps.Any(item => item.Name == "Handover" && item.Outcome == "Complete");

    private static List<PromotionTrack> LegacyTracks(PromotionRunRecord run, PromoteSongRequest request)
    {
        var plan = JsonSerializer.Deserialize<PromotionPlan>(run.PlanJson)!;
        return [new() { RecordingId = request.RecordingId, Song = new Song { Title = plan.Track.Title,
            Artist = plan.Track.Artist, Album = plan.Track.Album ?? "", AlbumArtist = plan.Track.AlbumArtist, Year = plan.Track.Year },
            Step = run.Step, Outcome = run.Outcome, Reason = run.Reason, ManagedFileId = run.ManagedFileId,
            Provider = plan.Provider, ApprovedByUserId = run.ApprovedByUserId, ApprovalReason = run.ApprovalReason }];
    }

    private async Task ProjectNextSourcesAsync(PromotionReceipt receipt, PromotionRunRecord run, CancellationToken token)
    {
        if (identities == null || run.Outcome == "Cancelled" || ReadAlbum(run) is not { } album) return;
        var actor = new ProviderActorContext(ProviderActorKind.SystemJob, null, durableJobId: run.JobId, actingForUserId: run.OwnerUserId);
        foreach (var track in receipt.Tracks.Where(item => item.Outcome == "Needs review" && item.Provider != null))
        {
            var stored = await identities.GetStoredSongsAsync(actor, track.RecordingId.ToString("D"), token);
            var next = CandidateSources(album.Profile, stored.Append(track.Song)).FirstOrDefault(song =>
                song.ExternalProvider != track.Provider && !track.RejectedProviders.Contains(song.ExternalProvider!) &&
                PromotionQuality.Accepts(album.Profile, song.Codec, song.BitDepth, song.SampleRate));
            if (next != null) track.NextSource = new(next.ExternalProvider!, next.Codec, next.BitDepth, next.SampleRate);
        }
    }

    private static IEnumerable<Song> CandidateSources(PromotionProfile profile, IEnumerable<Song> songs) =>
        PromotionQuality.Order(profile, songs.Where(song => !song.IsLocal && !string.IsNullOrWhiteSpace(song.ExternalProvider) &&
            !string.IsNullOrWhiteSpace(song.ExternalId)).DistinctBy(song => (song.ExternalProvider, song.ExternalId)));

    private static PromotionAlbumSummary AlbumSummary(Song song, int count) => new(song.Album,
        string.IsNullOrWhiteSpace(song.AlbumArtist) ? song.Artist : song.AlbumArtist, song.Year, count,
        song.CoverArtUrl ?? song.CoverArtUrlLarge ?? (song.ExternalProvider != null && song.ExternalId != null
            ? $"ext-{song.ExternalProvider}-song-{song.ExternalId}" : null));

    private static string EncodeCursor(PromotionReceipt item) => Convert.ToBase64String(Encoding.UTF8.GetBytes(
        $"{item.StartedAt.UtcTicks.ToString(CultureInfo.InvariantCulture)}:{item.Id:N}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static (DateTimeOffset Time, Guid Id)? DecodeCursor(string? cursor)
    {
        if (cursor == null) return null;
        if (cursor.Length is 0 or > 128 || cursor.Any(value => !char.IsAsciiLetterOrDigit(value) && value is not ('-' or '_')))
            throw new ArgumentException("The promotion cursor is invalid.");
        try
        {
            var encoded = cursor.Replace('-', '+').Replace('_', '/');
            var value = Encoding.UTF8.GetString(Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '='))).Split(':');
            if (value.Length != 2 || !long.TryParse(value[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ||
                !Guid.TryParseExact(value[1], "N", out var id) || id == Guid.Empty || ticks < DateTimeOffset.MinValue.Ticks || ticks > DateTimeOffset.MaxValue.Ticks)
                throw new ArgumentException("The promotion cursor is invalid.");
            return (new DateTimeOffset(ticks, TimeSpan.Zero), id);
        }
        catch (FormatException) { throw new ArgumentException("The promotion cursor is invalid."); }
    }
}

using System.Security.Cryptography;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Jobs;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Enrichment;

public sealed record PromoteSongRequest(Guid RecordingId, Guid ArtifactId, Guid RootId, string BackendLibraryId,
    string RequestKey, Guid? CredentialReferenceId = null);
public sealed record PromotionPreview(string DestinationPath, string[] Steps, long Size);
internal sealed record PromotionPlan(MetadataEnrichmentPlan Metadata, ManagedTrackPathValues Track,
    string Provider, string? ExternalId, string? QualityJson);

public sealed class LibraryPromotionService(IDbContextFactory<AllstarrDbContext> factory, DurableJobQueue jobs,
    PromotionRoots roots, ProviderDownloadArtifactResolver artifacts, ProviderDownloadWorkspaceOptions workspaces,
    IServiceScopeFactory scopes, IManagedMetadataWriter writer, AudioQualityAnalyzer analyzer,
    IBackendLibraryAccessResolver libraryAccess, ITrackIdentityService? identities = null,
    IPlaybackMetadataResolver? artwork = null) : IDurableJobHandler
{
    public const string JobType = "library.promote";
    private const string Template = "{albumArtist}/{album} ({year})/{track:00} - {title}{extension}";
    private static readonly string[] Steps = ["Copy complete original", "MusicBrainz tags and cover", "Quality screen", "Place in library", "Request library refresh"];
    private readonly SemaphoreSlim gate = new(1, 1);
    string IDurableJobHandler.JobType => JobType;

    public async Task<PromotionPreview> PreviewAsync(ProtocolExecutionContext context, PromoteSongRequest request,
        CancellationToken token = default)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        await AuthorizeAsync(db, context, token);
        var root = await roots.RequireAsync(request.RootId, token);
        if (!(await libraryAccess.ResolveAsync(context, token)).Allows(request.BackendLibraryId))
            throw new UnauthorizedAccessException("This backend library is unavailable to you.");
        var source = await SourceAsync(db, context.Principal!.UserId, request, token);
        var plan = await PlanAsync(db, context, request, source, token);
        var destination = Destination(root.Path, plan.Track);
        return new(destination, Steps, source.Length);
    }

    public async Task<PromotionRunRecord> StartAsync(ProtocolExecutionContext context, PromoteSongRequest request,
        CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(request.RequestKey) || request.RequestKey.Length > 100 ||
            request.RecordingId == Guid.Empty || request.ArtifactId == Guid.Empty || string.IsNullOrWhiteSpace(request.BackendLibraryId))
            throw new ArgumentException("A song, complete original, library and request key are required.");
        await gate.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            await AuthorizeAsync(db, context, token);
            var owner = context.Principal!.UserId;
            var json = JsonSerializer.Serialize(request);
            var existing = await db.Set<PromotionRunRecord>().SingleOrDefaultAsync(item =>
                item.OwnerUserId == owner && item.RequestKey == request.RequestKey, token);
            if (existing != null)
            {
                if (existing.RequestJson != json) throw new InvalidOperationException("This request key already belongs to a different promotion.");
                return await GetAsync(context, existing.Id, token);
            }
            if (!(await libraryAccess.ResolveAsync(context, token)).Allows(request.BackendLibraryId))
                throw new UnauthorizedAccessException("This backend library is unavailable to you.");
            var source = await SourceAsync(db, owner, request, token);
            var root = await roots.RequireAsync(request.RootId, token);
            var plan = await PlanAsync(db, context, request, source, token);
            Destination(root.Path, plan.Track);
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            var id = Guid.CreateVersion7();
            var queued = await jobs.EnqueueInExistingTransactionAsync(db, new DurableJobEnqueueRequest<Guid>(
                JobType, $"promotion:{id:N}", id, owner, MaxAttempts: 3), token);
            var run = new PromotionRunRecord
            {
                Id = id,
                OwnerUserId = owner,
                JobId = queued.JobId,
                RequestKey = request.RequestKey,
                RequestJson = json,
                RootPath = root.Path,
                PlanJson = JsonSerializer.Serialize(plan),
                SourceSha256 = source.ContentSha256,
                SourceLength = source.Length,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.Set<PromotionRunRecord>().Add(run);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return run;
        }
        finally { gate.Release(); }
    }

    public async Task<PromotionRunRecord> GetAsync(ProtocolExecutionContext context, Guid id, CancellationToken token = default)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var run = await OwnedAsync(db, context, id, token);
        var job = await db.Jobs.AsNoTracking().SingleAsync(item => item.Id == run.JobId, token);
        if (run.Outcome is "Queued" or "Running" && job.State is DurableJobState.Failed or DurableJobState.Cancelled)
        {
            run.Outcome = job.State == DurableJobState.Failed ? "Failed" : "Cancelled";
            run.Reason = job.LastErrorMessage ?? "The durable job stopped before placement completed.";
        }
        return run;
    }

    public async Task<PromotionRunRecord> ApproveAsync(ProtocolExecutionContext context, Guid id, string reason,
        CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 500)
            throw new ArgumentException("Explain why this exact quality result is acceptable.");
        await gate.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var run = await OwnedAsync(db, context, id, token);
            if (run.Outcome != "Needs review") throw new InvalidOperationException("Only a run needing quality review can be approved.");
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            var queued = await jobs.EnqueueInExistingTransactionAsync(db, new DurableJobEnqueueRequest<Guid>(
                JobType, $"promotion:{id:N}:approved", id, run.OwnerUserId, MaxAttempts: 3), token);
            run.JobId = queued.JobId;
            run.ApprovedByUserId = context.Principal!.UserId;
            run.ApprovalReason = reason.Trim();
            run.Outcome = "Queued";
            run.Reason = "Quality review approved; waiting to place the checked file.";
            run.Revision++;
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return run;
        }
        finally { gate.Release(); }
    }

    public async Task<PromotionRunRecord> CancelAsync(ProtocolExecutionContext context, Guid id, CancellationToken token = default)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var run = await OwnedAsync(db, context, id, token);
        await jobs.RequestCancellationAsync(run.JobId, run.OwnerUserId, token);
        await gate.WaitAsync(token);
        try
        {
            await db.Entry(run).ReloadAsync(token);
            Cleanup(run.Id);
            if (run.Outcome == "Placed") return run;
            run.Outcome = "Cancelled";
            run.Reason = run.ManagedFileId == null ? "Cancelled; private working files removed." : "Cancelled; the placed library file is retained.";
            run.Revision++;
            await db.SaveChangesAsync(token);
            return run;
        }
        finally { gate.Release(); }
    }

    public async Task<DurableJobCompletion> ExecuteAsync(DurableJobExecutionContext execution, CancellationToken token)
    {
        var id = execution.Claim.Payload.Deserialize<Guid>();
        await gate.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var run = await db.Set<PromotionRunRecord>().SingleOrDefaultAsync(item => item.Id == id &&
                item.OwnerUserId == execution.Claim.OwnerUserId && item.JobId == execution.Claim.JobId, token);
            if (run == null || run.Outcome == "Cancelled") return DurableJobCompletion.Cancelled();
            if (run.Outcome == "Placed") { Cleanup(id); return DurableJobCompletion.Success(); }
            var request = JsonSerializer.Deserialize<PromoteSongRequest>(run.RequestJson)!;
            var plan = JsonSerializer.Deserialize<PromotionPlan>(run.PlanJson)!;
            var completed = await db.Set<PromotionStepRecord>().Where(item => item.RunId == id).ToDictionaryAsync(item => item.Name, token);
            var access = await libraryAccess.ResolveUserAsync(run.OwnerUserId, token);
            if (access.Context == null || !access.Access.Allows(request.BackendLibraryId))
                throw new UnauthorizedAccessException("The owner's backend library access is unavailable.");
            var root = await roots.RequireAsync(request.RootId, token);
            if (root.Path != run.RootPath) throw new InvalidOperationException("The approved root changed; start a new promotion.");
            var workspace = Workspace(id);
            var path = Path.Combine(workspace, "song" + plan.Track.Extension);

            async Task Begin(string step)
            {
                if (!await jobs.IsExecutionCurrentAsync(execution.Claim, token)) throw new OperationCanceledException();
                run.Step = step;
                run.Outcome = "Running";
                run.Reason = step switch
                {
                    "Copy" => "Copying the verified complete original.",
                    "Tags" => "Writing supported tags and existing cover artwork.",
                    "Quality" => "Screening for suspicious audio evidence.",
                    "Placement" => "Placing the checked file in the approved root.",
                    _ => "Requesting a backend library scan."
                };
                run.Revision++;
                await db.SaveChangesAsync(token);
                await execution.ReportProgressAsync(new(step, run.Reason), token);
            }
            async Task Save(string name, string hash, long length, string reason, string? result = null)
            {
                if (name != "Placement" && !await jobs.IsExecutionCurrentAsync(execution.Claim, token)) throw new OperationCanceledException();
                var step = new PromotionStepRecord { RunId = id, Name = name, ContentSha256 = hash, Length = length, Reason = reason, ResultJson = result };
                db.Set<PromotionStepRecord>().Add(step);
                await db.SaveChangesAsync(token);
                completed[name] = step;
            }

            if (!completed.ContainsKey("Copy"))
            {
                await Begin("Copy");
                var source = await SourceAsync(db, run.OwnerUserId, request, token);
                if (source.ContentSha256 != run.SourceSha256 || source.Length != run.SourceLength)
                    throw new IOException("The selected original changed.");
                Directory.CreateDirectory(workspace);
                PromotionRoots.RejectLinks(workspace);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(workspace, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                var partial = path + ".partial";
                if (File.Exists(partial)) File.Delete(partial);
                if (!File.Exists(path))
                {
                    await using (var input = File.OpenRead(source.SourcePath))
                    await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        await input.CopyToAsync(output, token);
                    await VerifyAsync(partial, run.SourceSha256, run.SourceLength, token);
                    File.Move(partial, path, false);
                }
                await VerifyAsync(path, run.SourceSha256, run.SourceLength, token);
                await Save("Copy", run.SourceSha256, run.SourceLength, "Copied and verified the complete original.");
            }
            if (!completed.ContainsKey("Tags"))
            {
                await Begin("Tags");
                var tags = new Dictionary<string, string>(plan.Metadata.Tags);
                foreach (var key in new[] { "musicbrainz_recordingid", "musicbrainz_releaseid", "musicbrainz_releasegroupid", "musicbrainz_artistid" })
                    tags.TryAdd(key, "");
                var coverPath = Path.Combine(workspace, "cover.json");
                PromotionRoots.RejectLinks(coverPath);
                if (!File.Exists(coverPath))
                {
                    var cover = artwork != null && plan.ExternalId != null
                        ? await artwork.ResolveArtworkAsync($"ext-{plan.Provider}-song-{plan.ExternalId}", access.Context, token) : null;
                    var valid = cover?.Content is { Length: > 8 and <= 5242880 } bytes &&
                        (bytes.AsSpan().StartsWith(new byte[] { 255, 216, 255 }) || bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
                    var temporaryCover = coverPath + ".partial";
                    PromotionRoots.RejectLinks(temporaryCover);
                    await File.WriteAllTextAsync(temporaryCover, JsonSerializer.Serialize(valid ? Convert.ToBase64String(cover!.Content) : ""), token);
                    File.Move(temporaryCover, coverPath, false);
                }
                var coverData = JsonSerializer.Deserialize<string>(await File.ReadAllTextAsync(coverPath, token))!;
                if (coverData.Length > 0) tags["cover"] = coverData;
                var operation = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(tags))));
                var written = await writer.WriteAsync(new(path, run.SourceSha256, true, false)
                { TargetRootPath = workspace, OperationFingerprint = operation }, tags, token);
                await using var lease = written.Lease;
                await Save("Tags", written.ContentSha256, written.Length,
                    "Supported MusicBrainz identities only; unsupported copied identities cleared. " +
                    (coverData.Length > 0 ? "Existing provider cover embedded as a fallback; release artwork agreement is unverified." : "Existing embedded cover preserved; no additional cover was available."));
                if (lease != null) await lease.CommitAsync(token);
            }
            var tagged = completed["Tags"];
            await VerifyAsync(path, tagged.ContentSha256, tagged.Length, token);
            if (!completed.ContainsKey("Quality"))
            {
                await Begin("Quality");
                var check = AudioQualityCheckJobHandler.ReadCheck(plan.QualityJson) ?? await analyzer.AnalyzeAsync(path, token);
                var reason = check.Verdict switch
                {
                    "standard" or "true-hi-res" => "No suspicious evidence found.",
                    "lossy-transcode-suspected" => "Suspected lossy transcode.",
                    "upsampled" => "Suspected upsample.",
                    "padded-bit-depth" => "Evidence of padded bit depth.",
                    _ => "Inconclusive audio quality screen."
                };
                await Save("Quality", tagged.ContentSha256, tagged.Length, reason, JsonSerializer.Serialize(check));
            }
            var quality = completed["Quality"];
            var verdict = AudioQualityCheckJobHandler.ReadCheck(quality.ResultJson)?.Verdict;
            if (verdict is not ("standard" or "true-hi-res") && run.ApprovedByUserId == null)
            {
                run.Outcome = "Needs review";
                run.Step = "Quality";
                run.Reason = quality.Reason + " An owner or administrator must review and give a reason to proceed.";
                run.Revision++;
                await db.SaveChangesAsync(token);
                return DurableJobCompletion.Success();
            }
            if (run.ManagedFileId == null)
            {
                await Begin("Placement");
                var current = await libraryAccess.ResolveUserAsync(run.OwnerUserId, token);
                if (!current.Access.Allows(request.BackendLibraryId)) throw new UnauthorizedAccessException("Library access changed before placement.");
                Destination(root.Path, plan.Track);
                using var scope = scopes.CreateScope();
                var placed = await scope.ServiceProvider.GetRequiredService<FilePlacementService>().PlacePromotionAsync(new(
                    new(root.Id, root.Path, run.OwnerUserId), path, Template, plan.Track, execution.Claim.JobId,
                    ManagedFileScopeKey.Create(run.OwnerUserId, root.Id), true, false, tagged.ContentSha256, tagged.Length)
                { ReferenceKey = $"promotion:{id:N}" }, token);
                run.ManagedFileId = placed.File.Id;
                await Save("Placement", tagged.ContentSha256, tagged.Length, "Placed with a durable library reference independent of hearts.");
            }
            await Begin("Refresh");
            await using (var transaction = await db.Database.BeginTransactionAsync(token))
            {
                await jobs.EnqueueInExistingTransactionAsync(db, new DurableJobEnqueueRequest<BackendLibraryRefreshJobPayload>(
                    "library.refresh", $"promotion:{id:N}", new(request.BackendLibraryId, access.Context.BackendInstanceId,
                        access.Context.VerifiedBackendPrincipalId, request.CredentialReferenceId), run.OwnerUserId), token);
                run.Outcome = "Placed";
                run.Reason = "Placed in the library; backend refresh queued. Scan completion is not yet verified.";
                run.Revision++;
                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
            }
            Cleanup(id);
            return DurableJobCompletion.Success();
        }
        catch (LocalCapacityException)
        {
            return DurableJobCompletion.Retry("local-capacity-unavailable", "Audio checking capacity is full; this promotion will retry.", TimeSpan.FromSeconds(5));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var run = await db.Set<PromotionRunRecord>().SingleOrDefaultAsync(item => item.Id == id, token);
            if (run != null)
            {
                var placed = run.Outcome == "Placed";
                if (!placed) run.Outcome = "Failed";
                run.Reason = placed ? "Placed and refresh queued; private workspace cleanup will retry." :
                    SafeOperationalText.Sanitize(error.Message, 500) ?? "Promotion stopped; review the source and destination.";
                run.Revision++;
                await db.SaveChangesAsync(token);
                if (placed) return DurableJobCompletion.Retry("promotion_cleanup_pending", run.Reason);
            }
            return DurableJobCompletion.Failure("promotion_stopped", run?.Reason);
        }
        finally { gate.Release(); }
    }

    private async Task<VerifiedProviderDownloadArtifact> SourceAsync(AllstarrDbContext db, Guid owner,
        PromoteSongRequest request, CancellationToken token)
    {
        var item = await db.Set<ProviderDownloadArtifactEntity>().AsNoTracking().SingleOrDefaultAsync(value => value.Id == request.ArtifactId, token)
            ?? throw new InvalidOperationException("Choose a verified complete kept or cached original; streaming fragments cannot be promoted.");
        if (item.State is not (ProviderDownloadArtifactState.Verified or ProviderDownloadArtifactState.Placed) ||
            item.VerifiedAt == default || item.SupersededAt != null || item.Length <= 0)
            throw new InvalidOperationException("Only verified complete originals can be promoted; streaming fragments are excluded.");
        var kept = await (from route in db.Set<RecordingKeptRouteEntity>().AsNoTracking()
                          join file in db.ManagedFiles.AsNoTracking() on route.ManagedFileId equals file.Id
                          where route.SourceArtifactId == item.Id && route.CanonicalRecordingId == request.RecordingId && file.RemovedAt == null &&
                                file.ContentSha256 == item.ContentSha256 && file.Length == item.Length
                          select file).FirstOrDefaultAsync(token);
        if (kept == null && item.OwnerUserId != owner) throw new UnauthorizedAccessException("The selected original is unavailable to this listener.");
        if (kept == null && !await db.ProviderTrackIdentities.AnyAsync(value => value.CanonicalRecordingId == request.RecordingId &&
            value.ProviderId == item.ProviderId && value.ExternalId == item.SourceExternalTrackId &&
            value.ResourceKind == ProviderResourceKind.Track && value.Verification == ProviderIdentityVerification.Verified, token))
            throw new InvalidOperationException("The complete original is not verified for this recording.");
        if (kept != null)
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<FilePlacementService>().VerifyOwnedFileAsync(EfManagedFileOwnershipStore.Map(kept)!, token);
        }
        var resolved = kept == null ? await artifacts.FindByJobAsync(item.DurableJobId, item.ProviderId, token) : null;
        if (kept == null && (resolved?.Id != item.Id || item.State != ProviderDownloadArtifactState.Verified))
            throw new InvalidOperationException("The verified complete original is unavailable.");
        var path = kept?.CanonicalPath ?? resolved!.SourcePath;
        PromotionRoots.RejectLinks(path);
        await VerifyAsync(path, item.ContentSha256, item.Length, token);
        return new(item.Id, item.WorkspaceRecordId, path, item.ContentSha256, item.Length, item.OwnerUserId,
            item.DurableJobId, item.ProviderId, item.ProviderAccountId, item.State, kept?.Id ?? item.ManagedFileId)
        { SourceExternalTrackId = item.SourceExternalTrackId };
    }

    private async Task<PromotionPlan> PlanAsync(AllstarrDbContext db, ProtocolExecutionContext context, PromoteSongRequest request,
        VerifiedProviderDownloadArtifact source, CancellationToken token)
    {
        var recording = await db.CanonicalRecordings.Include(item => item.Identifiers).SingleAsync(item => item.Id == request.RecordingId, token);
        var songs = identities == null ? [] : await identities.GetStoredSongsAsync(context.RequireActor(), request.RecordingId.ToString("D"), token);
        var song = songs.FirstOrDefault(item => item.ExternalProvider == source.ProviderId && item.ExternalId == source.SourceExternalTrackId);
        var track = new ManagedTrackPathValues(song?.Title ?? recording.Title, song?.Artist ?? "Unknown", song?.Album ?? "Unknown",
            song?.AlbumArtist, Year: song?.Year, Track: song?.Track ?? 0, Extension: Path.GetExtension(source.SourcePath));
        try
        {
            using var file = TagLib.File.Create(source.SourcePath);
            track = track with
            {
                Title = song?.Title ?? file.Tag.Title ?? track.Title,
                Artist = song?.Artist ?? file.Tag.FirstPerformer ?? track.Artist,
                Album = song?.Album ?? file.Tag.Album ?? track.Album,
                AlbumArtist = song?.AlbumArtist ?? file.Tag.FirstAlbumArtist,
                Year = song?.Year ?? (file.Tag.Year > 0 ? (int)file.Tag.Year : null),
                Track = song?.Track ?? (int)file.Tag.Track
            };
        }
        catch (Exception error) when (error is TagLib.UnsupportedFormatException or TagLib.CorruptFileException) { }
        var mbids = recording.Identifiers.Where(item => item.Kind == RecordingIdentifierKinds.MusicBrainz)
            .Select(item => item.Value).Distinct().ToArray();
        string? recordingMbid = mbids.Length == 1 && Guid.TryParse(mbids[0], out var parsed) ? parsed.ToString("D") : null;
        var applied = source.ManagedFileId == null ? null : await (from application in db.MetadataEnrichmentApplications
                                                                   join plan in db.MetadataEnrichmentPlans on application.PlanId equals plan.Id
                                                                   where application.ManagedArtifactId == source.ManagedFileId && application.State == MetadataEnrichmentApplicationState.Applied
                                                                   select plan).OrderByDescending(item => item.CreatedAt).FirstOrDefaultAsync(token);
        var supported = applied == null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(applied.TagsJson);
        var release = supported != null && supported.GetValueOrDefault("musicbrainz_recordingid") == recordingMbid &&
            Guid.TryParse(supported.GetValueOrDefault("musicbrainz_releaseid"), out var releaseMbid) ? releaseMbid.ToString("D") : null;
        var metadata = new MetadataEnrichmentPlanner().CreatePlan(new(new(track.Title), new(track.Artist), new(track.Album),
            new(track.AlbumArtist), Year: new(track.Year?.ToString()), Track: new(track.Track?.ToString())),
            new(recordingMbid, release, null, null, null, null, null, null, null, null, null));
        var quality = await db.Set<ProviderDownloadArtifactEntity>().Where(item => item.Id == source.Id).Select(item => item.QualityCheckJson).SingleAsync(token);
        return new(metadata, track, source.ProviderId, source.SourceExternalTrackId, quality);
    }

    private static string Destination(string root, ManagedTrackPathValues track)
    {
        var path = PromotionRoots.Contained(root, Path.Combine(root, ManagedPathTemplate.Render(Template, track)));
        PromotionRoots.RejectLinks(path);
        return path;
    }

    private string Workspace(Guid id)
    {
        var path = Path.Combine(Path.GetFullPath(workspaces.RootPath), "promotions", id.ToString("N"));
        PromotionRoots.RejectLinks(path);
        return path;
    }

    private void Cleanup(Guid id)
    {
        var path = Workspace(id);
        if (!Directory.Exists(path)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            PromotionRoots.RejectLinks(entry);
            if (Directory.Exists(entry)) throw new IOException("Unexpected directory in the private workspace.");
        }
        Directory.Delete(path, true);
    }

    private static async Task VerifyAsync(string path, string hash, long length, CancellationToken token)
    {
        PromotionRoots.RejectLinks(path);
        await using var stream = File.OpenRead(path);
        if (stream.Length != length || Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token)) != hash)
            throw new IOException("The saved working file changed; promotion stopped without publishing it.");
    }

    private static async Task AuthorizeAsync(AllstarrDbContext db, ProtocolExecutionContext context, CancellationToken token)
    {
        if (context.Principal == null || !await db.Users.AnyAsync(item => item.Id == context.Principal.UserId && item.Enabled &&
            item.BackendInstanceId == context.BackendInstanceId && item.BackendPrincipalId == context.VerifiedBackendPrincipalId, token))
            throw new UnauthorizedAccessException("An active linked backend user is required.");
    }

    private static async Task<PromotionRunRecord> OwnedAsync(AllstarrDbContext db, ProtocolExecutionContext context, Guid id, CancellationToken token)
    {
        await AuthorizeAsync(db, context, token);
        var run = await db.Set<PromotionRunRecord>().SingleOrDefaultAsync(item => item.Id == id, token) ?? throw new KeyNotFoundException();
        var actor = context.Principal!.UserId;
        if (run.OwnerUserId != actor && !await db.Users.AnyAsync(item => item.Id == actor && item.IsAdmin, token))
            throw new UnauthorizedAccessException("Only the owner or an administrator can review this promotion.");
        return run;
    }
}

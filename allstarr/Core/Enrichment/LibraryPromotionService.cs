using allstarr.Core.Playback;
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
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Enrichment;

public sealed record PromoteSongRequest(Guid RecordingId, Guid ArtifactId, Guid RootId, string BackendLibraryId,
    string RequestKey, Guid? CredentialReferenceId = null)
{
    public string? Unit { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? RequestedItemId { get; init; }
}
public sealed record PromotionPreview(string DestinationPath, string[] Steps, long Size)
{
    public bool CanAddDirectly { get; init; }
    public PromotionAlbumSummary? Album { get; init; }
    public int AlreadyInLibrary { get; init; }
    public IReadOnlyList<PromotionCopyPreview> BestCopies { get; init; } = [];
    public IReadOnlyList<PromotionProfileStep> ProfileSteps { get; init; } = [];
    public string? BackendLibraryId { get; init; }
    public Guid? RootId { get; init; }
}
internal sealed record PromotionPlan(MetadataEnrichmentPlan Metadata, ManagedTrackPathValues Track,
    string Provider, string? ExternalId, string? QualityJson)
{
    public string NamingScript { get; init; } = PromotionProfile.DefaultNamingScript;
    public Dictionary<string, string?>? NamingFields { get; init; }
}

public sealed partial class LibraryPromotionService(IDbContextFactory<AllstarrDbContext> factory, DurableJobQueue jobs,
    PromotionRoots roots, ProviderDownloadArtifactResolver artifacts, ProviderDownloadWorkspaceOptions workspaces,
    IServiceScopeFactory scopes, IManagedMetadataWriter writer, AudioQualityAnalyzer analyzer,
    IBackendLibraryAccessResolver libraryAccess, ITrackIdentityService? identities = null,
    IPlaybackMetadataResolver? artwork = null, PromotionProfiles? profiles = null,
    IPromotionCatalog? promotionCatalog = null, IPromotionAcquisition? promotionAcquisition = null,
    IProtocolLyricsResolver? lyrics = null, PromotionReplayGain? replayGain = null, MediaConversionGate? conversions = null,
    allstarr.Core.ClientEvents.IClientEventPublisher? events = null) : IDurableJobHandler
{
    public const string JobType = "library.promote";
    private static readonly string[] Steps = ["Copy complete original", "MusicBrainz tags and cover", "Quality screen", "Place in library", "Request library refresh"];
    private readonly SemaphoreSlim gate = new(1, 1);
    string IDurableJobHandler.JobType => JobType;

    public async Task<PromotionPreview> PreviewAsync(ProtocolExecutionContext context, PromoteSongRequest request,
        CancellationToken token = default)
    {
        var canAddDirectly = await CanAddDirectlyAsync(context, token);
        if (promotionCatalog != null) return (await PreviewAlbumAsync(context, request, token)) with { CanAddDirectly = canAddDirectly };
        await using var db = await factory.CreateDbContextAsync(token);
        await AuthorizeAsync(db, context, token);
        var root = await roots.RequireAsync(request.RootId, token, backendLibraryId: request.BackendLibraryId);
        if (!(await libraryAccess.ResolveAsync(context, token)).Allows(request.BackendLibraryId))
            throw new UnauthorizedAccessException("This backend library is unavailable to you.");
        var source = await SourceAsync(db, context.Principal!.UserId, request, token);
        var plan = await PlanAsync(db, context, request, source, token);
        var destination = Destination(root.Path, plan, requireAvailable: true);
        var profile = (await (profiles ?? new PromotionProfiles(factory)).GetMineAsync(context, token)).Values;
        var song = new Song
        {
            CanonicalRecordingId = request.RecordingId,
            Title = plan.Track.Title,
            Artist = plan.Track.Artist,
            Album = plan.Track.Album ?? "",
            AlbumArtist = plan.Track.AlbumArtist,
            Year = plan.Track.Year
        };
        var artifact = await db.Set<ProviderDownloadArtifactEntity>().AsNoTracking().SingleAsync(item => item.Id == request.ArtifactId, token);
        var meets = PromotionQuality.Accepts(profile, artifact.Codec, artifact.BitDepth, artifact.SampleRate);
        return new(destination, Steps, source.Length)
        {
            CanAddDirectly = canAddDirectly,
            Album = AlbumSummary(song, 1),
            BackendLibraryId = request.BackendLibraryId,
            RootId = root.Id,
            AlreadyInLibrary = await AlreadyInLibraryAsync(db, context, request.BackendLibraryId,
                new PromotionTrack { RecordingId = request.RecordingId, Song = song }, token) ? 1 : 0,
            BestCopies = [new(source.ProviderId, artifact.Codec, artifact.BitDepth, artifact.SampleRate,
                meets ? "meets-your-floor" : "fallback", meets ? "The verified source meets your saved quality floor." : "Source format information is incomplete or below your saved floor.")],
            ProfileSteps = ProfileSteps(profile)
        };
    }

    private async Task<PromotionRunRecord> StartRunAsync(ProtocolExecutionContext context, PromoteSongRequest request,
        CancellationToken token, IReadOnlyList<Song>? savedSongs = null, Guid? requestId = null, Guid? approver = null,
        AuditEventRecord? clientReceipt = null)
    {
        if (promotionCatalog != null) return await StartAlbumAsync(context, request, token, savedSongs, requestId, approver, clientReceipt);
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
                if (existing.RequestJson != json) throw new PromotionException(PromotionFailure.PromotionChanged);
                return await GetAsync(context, existing.Id, token);
            }
            if (!(await libraryAccess.ResolveAsync(context, token)).Allows(request.BackendLibraryId))
                throw new UnauthorizedAccessException("This backend library is unavailable to you.");
            var source = await SourceAsync(db, owner, request, token);
            var root = await roots.RequireAsync(request.RootId, token, backendLibraryId: request.BackendLibraryId);
            var plan = await PlanAsync(db, context, request, source, token);
            Destination(root.Path, plan, requireAvailable: true);
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
            AttachClientReceipt(db, clientReceipt, run.Id);
            if (requestId is { } approvalId) await LinkApprovedRequestAsync(db, approvalId, run, approver!.Value, token);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            await PublishRunAsync(run, token);
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
        HydrateTracks(run);
        await ProjectHandoverAsync(db, run, token);
        return run;
    }

    public async Task<PromotionRunRecord> ApproveAsync(ProtocolExecutionContext context, Guid id, string reason,
        CancellationToken token = default)
    {
        if (promotionCatalog != null)
        {
            var current = await GetAsync(context, id, token);
            if (current.Tracks.Count > 0)
            {
                if (current.Tracks.Count != 1) throw new PromotionException(PromotionFailure.InvalidRequest);
                return await ReviewAsync(context, id, current.Tracks[0].RecordingId, "add-anyway", reason, token);
            }
        }
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 500)
            throw new ArgumentException("Explain why this exact quality result is acceptable.");
        await gate.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var run = await OwnedAsync(db, context, id, token);
            if (run.Outcome != "Needs review") throw new PromotionException(PromotionFailure.PromotionChanged);
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
            await PublishRunAsync(run, token);
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
            if (ReadAlbum(run) is { } album)
            {
                foreach (var track in album.Tracks)
                {
                    if (track.AcquisitionJobId is { } downloadJob) await jobs.RequestCancellationAsync(downloadJob, run.OwnerUserId, token);
                    if (track.Outcome is not ("Placed" or "Skipped")) track.Outcome = "Cancelled";
                }
                run.PlanJson = JsonSerializer.Serialize(album);
                run.Tracks = album.Tracks;
            }
            Cleanup(run.Id);
            if (run.Outcome == "Placed") return run;
            run.Outcome = "Cancelled";
            run.Reason = run.ManagedFileId == null ? "Cancelled; private working files removed." : "Cancelled; the placed library file is retained.";
            run.Revision++;
            await db.SaveChangesAsync(token);
            await SynchronizeRequestsForRunAsync(run.Id, token);
            await PublishRunAsync(run, token);
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
            if (ReadAlbum(run) is { } album) return await ExecuteAlbumAsync(db, run, album, execution, token);
            var request = JsonSerializer.Deserialize<PromoteSongRequest>(run.RequestJson)!;
            var plan = JsonSerializer.Deserialize<PromotionPlan>(run.PlanJson)!;
            var completed = await db.Set<PromotionStepRecord>().Where(item => item.RunId == id).ToDictionaryAsync(item => item.Name, token);
            var access = await libraryAccess.ResolveUserAsync(run.OwnerUserId, token);
            if (access.Context == null || !access.Access.Allows(request.BackendLibraryId))
                throw new UnauthorizedAccessException("The owner's backend library access is unavailable.");
            var root = await roots.RequireAsync(request.RootId, token, checkFreeSpace: false, backendLibraryId: request.BackendLibraryId);
            if (root.Path != run.RootPath) throw new PromotionException(PromotionFailure.PromotionChanged);
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
                await PublishRunAsync(run, token);
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
                    throw new PromotionException(PromotionFailure.SourceChanged);
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
                await PublishRunAsync(run, token);
                return DurableJobCompletion.Success();
            }
            if (run.ManagedFileId == null)
            {
                await Begin("Placement");
                var current = await libraryAccess.ResolveUserAsync(run.OwnerUserId, token);
                if (!current.Access.Allows(request.BackendLibraryId)) throw new UnauthorizedAccessException("Library access changed before placement.");
                Destination(root.Path, plan);
                roots.RequireFreeSpace(root.Path, tagged.Length);
                using var scope = scopes.CreateScope();
                var placed = await scope.ServiceProvider.GetRequiredService<FilePlacementService>().PlacePromotionAsync(new(
                    new(root.Id, root.Path, run.OwnerUserId), path, plan.NamingScript, plan.Track, execution.Claim.JobId,
                    ManagedFileScopeKey.Create(run.OwnerUserId, root.Id), true, false, tagged.ContentSha256, tagged.Length)
                { ReferenceKey = $"promotion:{id:N}" }, token, RelativePath(plan));
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
            await PublishRunAsync(run, token);
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
                await PublishRunAsync(run, token);
                if (placed) return DurableJobCompletion.Retry("promotion_cleanup_pending", run.Reason);
            }
            return DurableJobCompletion.Failure("promotion_stopped", run?.Reason);
        }
        finally
        {
            try { if (!token.IsCancellationRequested) await SynchronizeRequestsForRunAsync(id, token); }
            finally { gate.Release(); }
        }
    }

    private async Task<VerifiedProviderDownloadArtifact> SourceAsync(AllstarrDbContext db, Guid owner,
        PromoteSongRequest request, CancellationToken token)
    {
        var item = await db.Set<ProviderDownloadArtifactEntity>().AsNoTracking().SingleOrDefaultAsync(value => value.Id == request.ArtifactId, token)
            ?? throw new PromotionException(PromotionFailure.SourceChanged);
        if (item.State is not (ProviderDownloadArtifactState.Verified or ProviderDownloadArtifactState.Placed) ||
            item.VerifiedAt == default || item.SupersededAt != null || item.Length <= 0)
            throw new PromotionException(PromotionFailure.SourceChanged);
        var kept = await (from route in db.Set<RecordingKeptRouteEntity>().AsNoTracking()
                          join file in db.ManagedFiles.AsNoTracking() on route.ManagedFileId equals file.Id
                          where route.SourceArtifactId == item.Id && route.CanonicalRecordingId == request.RecordingId && file.RemovedAt == null &&
                                file.ContentSha256 == item.ContentSha256 && file.Length == item.Length
                          select file).FirstOrDefaultAsync(token);
        if (kept == null && item.OwnerUserId != owner) throw new UnauthorizedAccessException("The selected original is unavailable to this listener.");
        if (kept == null && !await db.ProviderTrackIdentities.AnyAsync(value => value.CanonicalRecordingId == request.RecordingId &&
            value.ProviderId == item.ProviderId && value.ExternalId == item.SourceExternalTrackId &&
            value.ResourceKind == ProviderResourceKind.Track && value.Verification == ProviderIdentityVerification.Verified, token))
            throw new PromotionException(PromotionFailure.SourceChanged);
        if (kept != null)
        {
            await VerifyAsync(kept.CanonicalPath, item.ContentSha256, item.Length, token);
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<FilePlacementService>().VerifyOwnedFileAsync(EfManagedFileOwnershipStore.Map(kept)!, token);
        }
        VerifiedProviderDownloadArtifact? resolved;
        try { resolved = kept == null ? await artifacts.FindByJobAsync(item.DurableJobId, item.ProviderId, token) : null; }
        catch (ProviderDownloadArtifactChangedException) { throw new PromotionException(PromotionFailure.SourceChanged); }
        if (kept == null && (resolved?.Id != item.Id || item.State != ProviderDownloadArtifactState.Verified))
            throw new PromotionException(PromotionFailure.SourceChanged);
        var path = kept?.CanonicalPath ?? resolved!.SourcePath;
        PromotionRoots.RejectLinks(path);
        if (kept == null) await VerifyAsync(path, item.ContentSha256, item.Length, token);
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
        var fields = PromotionSongFields.FromSong(song ?? new Song(), track.Extension);
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
            fields["discnumber"] = (song?.DiscNumber ?? (file.Tag.Disc > 0 ? (int)file.Tag.Disc : 1)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            fields["totaldiscs"] = (song?.TotalDiscs ?? (file.Tag.DiscCount > 0 ? (int)file.Tag.DiscCount : 1)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            fields["totaltracks"] = (song?.TotalTracks ?? (int)file.Tag.TrackCount).ToString(System.Globalization.CultureInfo.InvariantCulture);
            fields["genre"] ??= file.Tag.FirstGenre;
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
        var artifact = await db.Set<ProviderDownloadArtifactEntity>().AsNoTracking().SingleAsync(item => item.Id == source.Id, token);
        fields["title"] = track.Title;
        fields["artist"] = track.Artist;
        fields["album"] = track.Album;
        fields["albumartist"] = track.AlbumArtist ?? track.Artist;
        fields["year"] = track.Year?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        fields["date"] ??= fields["year"];
        fields["tracknumber"] = track.Track?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var tag in metadata.Tags.Where(item => item.Key.StartsWith("musicbrainz_", StringComparison.Ordinal))) fields[tag.Key] = tag.Value;
        fields["musicbrainz_albumid"] = release;
        if (string.IsNullOrWhiteSpace(fields.GetValueOrDefault("_quality")))
            fields["_quality"] = PromotionSongFields.FromSong(new Song
            { Codec = artifact.Codec, BitDepth = artifact.BitDepth, SampleRate = artifact.SampleRate })["_quality"];
        var profile = await (profiles ?? new PromotionProfiles(factory)).GetMineAsync(context, token);
        if (!profile.Values.NamingConfirmed) throw new PromotionException(PromotionFailure.NamingPatternNotSelected);
        return new(metadata, track, source.ProviderId, source.SourceExternalTrackId, artifact.QualityCheckJson)
        { NamingScript = profile.Values.NamingScript, NamingFields = fields };
    }

    private static string RelativePath(PromotionPlan plan)
    {
        var fields = plan.NamingFields ?? PromotionSongFields.FromSong(new Song
        {
            Title = plan.Track.Title,
            Artist = plan.Track.Artist,
            Album = plan.Track.Album ?? "Unknown",
            AlbumArtist = plan.Track.AlbumArtist,
            Year = plan.Track.Year,
            Track = plan.Track.Track
        }, plan.Track.Extension);
        var result = PromotionNaming.RenderForPromotion(plan.NamingScript, fields);
        return result.RelativePath ?? throw new ArgumentException("The promotion naming script does not produce a safe relative path.");
    }

    private static string Destination(string root, PromotionPlan plan, bool requireAvailable = false)
    {
        var path = PromotionNaming.ContainedPath(root, RelativePath(plan));
        PromotionRoots.RejectLinks(path);
        if (requireAvailable && File.Exists(path)) throw new PromotionException(PromotionFailure.DestinationExists);
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
            if (Directory.Exists(entry))
            {
                foreach (var child in Directory.EnumerateFileSystemEntries(entry))
                {
                    PromotionRoots.RejectLinks(child);
                    if (Directory.Exists(child)) throw new IOException("Unexpected directory in the private workspace.");
                }
            }
        }
        Directory.Delete(path, true);
    }

    private static async Task VerifyAsync(string path, string hash, long length, CancellationToken token)
    {
        PromotionRoots.RejectLinks(path);
        try
        {
            await using var stream = File.OpenRead(path);
            if (stream.Length != length || Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token)) != hash)
                throw new PromotionException(PromotionFailure.SourceChanged);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        { throw new PromotionException(PromotionFailure.SourceChanged); }
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

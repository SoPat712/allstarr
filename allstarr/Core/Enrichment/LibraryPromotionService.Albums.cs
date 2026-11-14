using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using allstarr.Core.Downloads;
using allstarr.Core.Jobs;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Matching;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Enrichment;

public sealed partial class LibraryPromotionService
{
    internal static PromotionAlbumPlan? ReadAlbum(PromotionRunRecord run)
    {
        using var json = JsonDocument.Parse(run.PlanJson);
        return json.RootElement.TryGetProperty("Version", out var version) && version.GetInt32() == 2
            ? JsonSerializer.Deserialize<PromotionAlbumPlan>(run.PlanJson) : null;
    }

    private static void HydrateTracks(PromotionRunRecord run) => run.Tracks = ReadAlbum(run)?.Tracks ?? [];

    private static async Task ProjectHandoverAsync(AllstarrDbContext db, PromotionRunRecord run, CancellationToken token)
    {
        var album = ReadAlbum(run);
        if (album == null) return;
        var legacyRefresh = !run.Tracks.Any(item => item.RefreshJobId != null);
        var refreshIds = run.Tracks.Select(item => item.RefreshJobId ?? (legacyRefresh ? album.RefreshJobId : null))
            .OfType<Guid>().Distinct().ToArray();
        var refreshes = await db.Jobs.AsNoTracking().Where(item => refreshIds.Contains(item.Id) && item.OwnerUserId == run.OwnerUserId)
            .ToDictionaryAsync(item => item.Id, token);
        var keys = refreshIds.Select(id => $"promotion-refresh:{id:N}").ToArray();
        var links = await db.Jobs.AsNoTracking().Where(item => item.Type == allstarr.Core.Matching.LibraryRecordingLinkQueue.JobType &&
            item.OwnerUserId == run.OwnerUserId && keys.Contains(item.IdempotencyKey)).ToDictionaryAsync(item => item.IdempotencyKey, token);
        var correlations = links.Values.Select(item => item.CorrelationId).ToArray();
        var confirmations = await ReadHandoverConfirmationsAsync(db, correlations, token);
        foreach (var track in run.Tracks.Where(item => item.ManagedFileId != null || item.Steps.Any(step => step.Name == "Dedupe")))
        {
            var id = track.RefreshJobId ?? (legacyRefresh ? album.RefreshJobId : null);
            if (id == null || !refreshes.TryGetValue(id.Value, out var refresh)) continue;
            var link = links.GetValueOrDefault($"promotion-refresh:{id.Value:N}");
            var confirmed = link?.State == DurableJobState.Succeeded || link != null && confirmations.Contains((link.CorrelationId, track.RecordingId));
            track.Steps.RemoveAll(item => item.Name is "Refresh" or "Handover");
            track.Steps.Add(new("Refresh", refresh.State == DurableJobState.Succeeded ? "Complete" : refresh.State.ToString(),
                refresh.State == DurableJobState.Succeeded ? "The backend accepted the refresh request." : "Waiting for the backend refresh request."));
            track.Steps.Add(new("Handover", confirmed ? "Complete" : link?.State.ToString() ?? "Pending",
                confirmed ? "The library links and listener handover are confirmed." : link?.LastErrorMessage ?? "Waiting for confirmed library links."));
        }
    }

    private static async Task<HashSet<(string, Guid)>> ReadHandoverConfirmationsAsync(AllstarrDbContext db, string[] correlations, CancellationToken token)
    {
        var audits = await db.AuditEvents.AsNoTracking().Where(item => item.Action == "promotion.handover-confirmed" &&
            correlations.Contains(item.CorrelationId)).Select(item => new { item.CorrelationId, item.DetailsJson }).ToArrayAsync(token);
        return audits.Select(item => (item.CorrelationId, JsonSerializer.Deserialize<Dictionary<string, Guid>>(item.DetailsJson)!["RecordingId"])).ToHashSet();
    }

    private async Task<PromotionPreview> PreviewAlbumAsync(ProtocolExecutionContext context, PromoteSongRequest request, CancellationToken token, IReadOnlyList<Song>? savedSongs = null)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        await AuthorizeAsync(db, context, token);
        var access = await libraryAccess.ResolveAsync(context, token);
        var profile = (await (profiles ?? new PromotionProfiles(factory)).GetMineAsync(context, token)).Values;
        var root = await roots.RequireAsync(request.RootId == Guid.Empty ? profile.LibraryRootId ?? Guid.Empty : request.RootId, token);
        var library = string.IsNullOrWhiteSpace(request.BackendLibraryId) ? root.BackendLibraryId : request.BackendLibraryId;
        if (library == null || !access.Allows(library)) throw new UnauthorizedAccessException();
        if (root.BackendLibraryId != library) throw new PromotionException(PromotionFailure.LibraryFolderMismatch);
        if (!profile.NamingConfirmed) throw new PromotionException(PromotionFailure.NamingPatternNotSelected);
        var songs = savedSongs ?? await promotionCatalog!.ResolveAsync(context, request.RecordingId, Unit(request, profile), token);
        if (songs.Count is 0 or > 256) throw new PromotionException(PromotionFailure.InvalidRequest);
        var opened = songs.FirstOrDefault(song => song.CanonicalRecordingId == request.RecordingId)
            ?? throw new PromotionException(PromotionFailure.InvalidRequest);
        var already = 0;
        foreach (var song in songs.DistinctBy(song => song.CanonicalRecordingId))
            if (song.CanonicalRecordingId is { } id && await AlreadyInLibraryAsync(db, context, library,
                    new PromotionTrack { RecordingId = id, Song = song }, token)) already++;
        var stored = identities == null ? [] : await identities.GetStoredSongsAsync(context.RequireActor(), request.RecordingId.ToString("D"), token);
        var candidates = CandidateSources(profile, stored.Append(opened)).OrderByDescending(song =>
            PromotionQuality.Accepts(profile, song.Codec, song.BitDepth, song.SampleRate) ? 2 :
            string.IsNullOrWhiteSpace(song.Codec) || song.SampleRate == null ||
                PromotionQuality.IsLossless(song.Codec) && song.BitDepth == null ? 1 : 0).ToArray();
        var best = candidates.FirstOrDefault(song => PromotionQuality.Accepts(profile, song.Codec, song.BitDepth, song.SampleRate));
        var destination = await PromotionDestinationNaming.ResolveAsync(db, context, access, root, profile, opened, request.NamingYear, token);
        var fields = PromotionSongFields.FromSong(opened, namingYear: request.NamingYear ??
            (destination.RequiresChoice ? destination.Choices.FirstOrDefault()?.Year : null));
        var pathKnown = false;
        if (best?.BitDepth is { } bits && best.SampleRate is { } rate)
        {
            var converts = PromotionQuality.IsLossless(best.Codec) &&
                (bits > profile.HiResCeiling.MaxBitDepth || rate > profile.HiResCeiling.MaxSampleRate);
            var format = new allstarr.Models.Domain.Song
            {
                Codec = converts ? "flac" : best.Codec,
                Container = converts ? "flac" : best.Container,
                BitDepth = converts ? Math.Min(bits, profile.HiResCeiling.MaxBitDepth) : bits,
                SampleRate = converts ? Math.Min(rate, profile.HiResCeiling.MaxSampleRate) : rate
            };
            var quality = PromotionSongFields.FromSong(format);
            fields["_extension"] = quality["_extension"];
            fields["_quality"] = quality["_quality"];
            pathKnown = !string.IsNullOrWhiteSpace(format.Container);
        }
        var rendered = PromotionNaming.RenderForPromotion(profile.NamingScript, fields);
        if (rendered.RelativePath == null) throw new ArgumentException("The naming script does not produce a safe path.");
        var path = PromotionNaming.ContainedPath(root.Path, rendered.RelativePath);
        PromotionRoots.RejectLinks(path);
        static string Rate(int value) => (value == 44100 ? 44 : value / 1000.0).ToString("0.#", CultureInfo.InvariantCulture);
        var floor = $"{profile.QualityFloor.CodecFamily} {profile.QualityFloor.MinBitDepth}/{Rate(profile.QualityFloor.MinSampleRate)}";
        var ceiling = $"{profile.HiResCeiling.MaxBitDepth}/{Rate(profile.HiResCeiling.MaxSampleRate)}";
        var copies = candidates.Select(song =>
        {
            var meets = PromotionQuality.Accepts(profile, song.Codec, song.BitDepth, song.SampleRate);
            var unknown = string.IsNullOrWhiteSpace(song.Codec) || song.SampleRate == null ||
                PromotionQuality.IsLossless(song.Codec) && song.BitDepth == null;
            return new PromotionCopyPreview(song.ExternalProvider!, song.Codec, song.BitDepth, song.SampleRate,
                song == best ? "meets-your-floor" : meets || unknown ? "fallback" : "skipped-below-floor",
                song == best ? $"Your floor is {floor} · keeps hi-res up to {ceiling}" :
                meets ? $"Next choice if {best!.ExternalProvider} fails" :
                unknown ? $"Source format unknown · the downloaded file must meet {floor}" :
                "Below your floor · never used for the library");
        }).ToArray();
        var steps = ProfileSteps(profile);
        return new(pathKnown && !destination.RequiresChoice ? path : "", steps.Where(step => step.Enabled).Select(step => step.Title).ToArray(), 0)
        {
            Album = AlbumSummary(opened, songs.Count),
            RequiresDestinationChoice = destination.RequiresChoice,
            DestinationChoices = destination.Choices,
            NamingYear = request.NamingYear,
            AlreadyInLibrary = already,
            BestCopies = copies,
            ProfileSteps = steps,
            BackendLibraryId = library,
            RootId = root.Id
        };
    }

    private static string Unit(PromoteSongRequest request, PromotionProfile profile) => (request.Unit ?? profile.DefaultUnit) switch
    {
        "song" => "song",
        "album" => "album",
        _ => throw new ArgumentException("Choose album or song.")
    };

    private async Task<PromotionRunRecord> StartAlbumAsync(ProtocolExecutionContext context, PromoteSongRequest request, CancellationToken token,
        IReadOnlyList<allstarr.Models.Domain.Song>? savedSongs = null, Guid? requestId = null, Guid? approver = null,
        AuditEventRecord? clientReceipt = null)
    {
        if (request.RecordingId == Guid.Empty || string.IsNullOrWhiteSpace(request.BackendLibraryId) ||
            string.IsNullOrWhiteSpace(request.RequestKey) || request.RequestKey.Length > 100)
            throw new ArgumentException("A recording, library and request key are required.");
        await gate.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            await AuthorizeAsync(db, context, token);
            var owner = context.Principal!.UserId;
            var json = JsonSerializer.Serialize(request);
            var existing = await db.Set<PromotionRunRecord>().SingleOrDefaultAsync(item => item.OwnerUserId == owner && item.RequestKey == request.RequestKey, token);
            if (existing != null)
            {
                if (existing.RequestJson != json) throw new PromotionException(PromotionFailure.PromotionChanged);
                HydrateTracks(existing);
                return existing;
            }
            var access = await libraryAccess.ResolveAsync(context, token);
            if (!access.Allows(request.BackendLibraryId)) throw new UnauthorizedAccessException();
            var profile = (await (profiles ?? new PromotionProfiles(factory)).GetMineAsync(context, token)).Values;
            var root = await roots.RequireAsync(request.RootId == Guid.Empty ? profile.LibraryRootId ?? Guid.Empty : request.RootId, token, backendLibraryId: request.BackendLibraryId);
            if (!profile.NamingConfirmed) throw new PromotionException(PromotionFailure.NamingPatternNotSelected);
            var songs = savedSongs ?? await promotionCatalog!.ResolveAsync(context, request.RecordingId, Unit(request, profile), token);
            if (songs.Count is 0 or > 256 || songs.Any(song => song.CanonicalRecordingId is null || song.CanonicalRecordingId == Guid.Empty))
                throw new PromotionException(PromotionFailure.InvalidRequest);
            var tracks = new List<PromotionTrack>();
            foreach (var song in songs.DistinctBy(song => song.CanonicalRecordingId))
            {
                var track = new PromotionTrack { RecordingId = song.CanonicalRecordingId!.Value, Song = song };
                if (await AlreadyInLibraryAsync(db, context, request.BackendLibraryId, track, token))
                {
                    track.Outcome = "Skipped";
                    track.Reason = "This recording or ISRC is already in the backend library.";
                    track.Steps.Add(new("Dedupe", "Skipped", track.Reason));
                }
                var destination = await PromotionDestinationNaming.ResolveAsync(db, context, access, root, profile, song, request.NamingYear, token);
                if (destination.RequiresChoice) throw new PromotionException(PromotionFailure.DestinationChoiceRequired);
                var rendered = PromotionNaming.RenderForPromotion(profile.NamingScript, PromotionSongFields.FromSong(song, namingYear: request.NamingYear));
                if (rendered.RelativePath == null) throw new ArgumentException("The naming script does not produce a safe path.");
                PromotionRoots.RejectLinks(PromotionNaming.ContainedPath(root.Path, rendered.RelativePath));
                tracks.Add(track);
            }
            var id = Guid.CreateVersion7();
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            var queued = await jobs.EnqueueInExistingTransactionAsync(db, new DurableJobEnqueueRequest<Guid>(JobType,
                $"promotion:{id:N}", id, owner, MaxAttempts: 3, MaxDeferrals: 10000), token);
            var run = new PromotionRunRecord
            {
                Id = id,
                OwnerUserId = owner,
                JobId = queued.JobId,
                RequestKey = request.RequestKey,
                RequestJson = json,
                RootPath = root.Path,
                PlanJson = JsonSerializer.Serialize(new PromotionAlbumPlan(profile, tracks)),
                CreatedAt = DateTimeOffset.UtcNow,
                Step = "Acquire",
                Reason = "Waiting to acquire the release tracks.",
                Tracks = tracks
            };
            db.Add(run);
            AttachClientReceipt(db, clientReceipt, run.Id);
            if (requestId is { } approvalId) await LinkApprovedRequestAsync(db, approvalId, run, approver!.Value, token);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            await PublishRunAsync(run, token);
            return run;
        }
        finally { gate.Release(); }
    }

    private async Task<bool> AlreadyInLibraryAsync(AllstarrDbContext db, ProtocolExecutionContext context, string library,
        PromotionTrack track, CancellationToken token)
    {
        var isrc = track.Song.Isrc?.Replace("-", "", StringComparison.Ordinal).Trim().ToUpperInvariant();
        var mbid = track.Song.ProviderIds.GetValueOrDefault("musicbrainz_recordingid");
        return await LibraryTrackAccess.Query(db, context, await libraryAccess.ResolveAsync(context, token)).AnyAsync(item =>
            item.BackendInstanceId == context.BackendInstanceId && item.BackendLibraryId == library &&
            (item.CanonicalRecordingId == track.RecordingId || isrc != null && isrc != "" && item.Isrc != null &&
                item.Isrc.Replace("-", "").ToUpper() == isrc || mbid != null && item.MusicBrainzRecordingId == mbid), token);
    }

    public async Task<PromotionRunRecord> ReviewAsync(ProtocolExecutionContext context, Guid runId, Guid recordingId,
        string action, string? reason, CancellationToken token = default)
    {
        if (action is not ("try-next-source" or "add-anyway" or "skip") ||
            action == "add-anyway" && (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 500))
            throw new ArgumentException("Choose a review action and explain any quality override.");
        await gate.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var run = await OwnedAsync(db, context, runId, token);
            var album = ReadAlbum(run) ?? throw new PromotionException(PromotionFailure.InvalidRequest);
            var track = album.Tracks.SingleOrDefault(item => item.RecordingId == recordingId) ?? throw new KeyNotFoundException();
            if (run.Outcome == "Cancelled" || track.Outcome != "Needs review") throw new PromotionException(PromotionFailure.PromotionChanged);
            if (action == "add-anyway" && (track.ArtifactId == null || track.Step != "Quality" || track.QualityCheck == null ||
                track.QualityCheck.Verdict is "standard" or "true-hi-res"))
                throw new PromotionException(PromotionFailure.InvalidRequest);
            if (action == "skip") { track.Outcome = "Skipped"; track.Reason = "Skipped by the reviewer."; }
            else if (action == "add-anyway")
            {
                track.ApprovedByUserId = context.Principal!.UserId;
                track.ApprovalReason = reason!.Trim();
                track.Lossy = true;
                track.Outcome = "Queued";
            }
            else
            {
                NextSource(track);
            }
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            var queued = await jobs.EnqueueInExistingTransactionAsync(db, new DurableJobEnqueueRequest<Guid>(JobType,
                $"promotion:{runId:N}:review:{run.Revision}", runId, run.OwnerUserId, MaxAttempts: 3, MaxDeferrals: 10000), token);
            run.JobId = queued.JobId;
            run.Outcome = "Queued";
            run.PlanJson = JsonSerializer.Serialize(album);
            run.Revision++;
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            await PublishRunAsync(run, token);
            HydrateTracks(run);
            return run;
        }
        finally { gate.Release(); }
    }

    private async Task<DurableJobCompletion> ExecuteAlbumAsync(AllstarrDbContext db, PromotionRunRecord run,
        PromotionAlbumPlan album, DurableJobExecutionContext execution, CancellationToken token)
    {
        var request = JsonSerializer.Deserialize<PromoteSongRequest>(run.RequestJson)!;
        if (album.RefreshJobId != null && !album.Tracks.Any(item => item.RefreshJobId != null))
            foreach (var track in album.Tracks.Where(item => item.Outcome == "Placed" || item.Steps.Any(step => step.Name == "Dedupe")))
                track.RefreshJobId = album.RefreshJobId;
        var access = await libraryAccess.ResolveUserAsync(run.OwnerUserId, token);
        if (access.Context == null || !access.Access.Allows(request.BackendLibraryId)) throw new UnauthorizedAccessException();
        var root = await roots.RequireAsync(request.RootId == Guid.Empty ? album.Profile.LibraryRootId ?? Guid.Empty : request.RootId,
            token, checkFreeSpace: false, backendLibraryId: request.BackendLibraryId);
        if (root.Path != run.RootPath) throw new PromotionException(PromotionFailure.PromotionChanged);
        async Task Persist()
        {
            if (!await jobs.IsExecutionCurrentAsync(execution.Claim, token)) throw new OperationCanceledException();
            run.PlanJson = JsonSerializer.Serialize(album);
            run.Revision++;
            await db.SaveChangesAsync(token);
            await PublishRunAsync(run, token);
        }
        async Task Step(PromotionTrack track, string name, string outcome, string reason)
        {
            track.Step = name;
            track.Reason = reason;
            track.Steps.RemoveAll(item => item.Name == name);
            track.Steps.Add(new(name, outcome, reason));
            run.Step = name;
            run.Reason = reason;
            await Persist();
            await execution.ReportProgressAsync(new(name, reason, album.Tracks.Count(item => item.Outcome is "Ready" or "Placed" or "Skipped"),
                album.Tracks.Count, Track: track.Song.Title), token);
        }
        run.Outcome = "Running";
        await Persist();
        foreach (var track in album.Tracks.Where(item => item.Outcome is not ("Skipped" or "Placed" or "Needs review")))
        {
            try
            {
                if (track.PendingTags != null && track.PendingMetadataStep != null)
                    await WriteTrackTagsAsync(run, track, track.PendingTags, Persist, token, track.PendingMetadataStep);
                if (track.Outcome == "Ready") continue;
                if (track.ArtifactId == null && await AlreadyInLibraryAsync(db, access.Context, request.BackendLibraryId, track, token))
                {
                    track.Outcome = "Skipped";
                    await Step(track, "Dedupe", "Skipped", "The recording is already in the backend library.");
                    continue;
                }
                var workspace = TrackWorkspace(run.Id, track);
                Directory.CreateDirectory(workspace);
                PromotionRoots.RejectLinks(workspace);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(workspace, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                if (track.ArtifactId == null)
                {
                    var result = await promotionAcquisition!.AcquireAsync(run.OwnerUserId, run.Id, track, album.Profile, token);
                    if (result.Pending) { await Step(track, "Acquire", "Running", result.Reason); continue; }
                    if (result.Artifact is not { } source)
                    {
                        track.Outcome = "Needs review";
                        await Step(track, "Acquire", "Needs review", result.Reason);
                        continue;
                    }
                    if (!PromotionQuality.Accepts(album.Profile, source.Codec, source.BitDepth, source.SampleRate))
                    {
                        track.Outcome = "Needs review";
                        await Step(track, "Acquire", "Needs review", "The acquired file is below the quality floor and cannot be approved.");
                        continue;
                    }
                    track.Extension = Path.GetExtension(source.SourcePath).ToLowerInvariant();
                    track.WorkingFileName = "song" + track.Extension;
                    track.Provider = source.ProviderId;
                    track.Song.Codec = source.Codec;
                    track.Song.BitDepth = source.BitDepth;
                    track.Song.SampleRate = source.SampleRate;
                    track.Lossy = !PromotionQuality.IsLossless(source.Codec);
                    var destination = TrackPath(run.Id, track);
                    await VerifyAsync(source.SourcePath, source.ContentSha256, source.Length, token);
                    var partial = destination + ".partial";
                    PromotionRoots.RejectLinks(partial);
                    if (File.Exists(partial)) File.Delete(partial);
                    await using (var input = File.OpenRead(source.SourcePath))
                    await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        await input.CopyToAsync(output, token);
                    await VerifyAsync(partial, source.ContentSha256, source.Length, token);
                    PromotionRoots.RejectLinks(destination);
                    File.Move(partial, destination, true);
                    track.ArtifactId = source.Id;
                    track.ContentSha256 = source.ContentSha256;
                    track.Length = source.Length;
                    await Step(track, "Acquire", "Complete", result.Reason);
                }
                var path = TrackPath(run.Id, track);
                if (!track.Steps.Any(item => item.Name == "Quality"))
                {
                    await VerifyAsync(path, track.ContentSha256!, track.Length, token);
                    var quality = album.Profile.FakeLosslessCheck ? await analyzer.AnalyzeAsync(path, token) : null;
                    track.QualityCheck = quality;
                    var clean = quality == null || quality.Verdict is "standard" or "true-hi-res";
                    if (!clean && track.ApprovedByUserId == null)
                    {
                        track.Outcome = "Needs review";
                        await Step(track, "Quality", "Needs review", $"Audio quality screen: {quality!.Verdict}.");
                        continue;
                    }
                    await Step(track, "Quality", "Complete", quality == null ? "Quality screen disabled by profile." : $"Audio quality screen: {quality.Verdict}.");
                }
                else if (track.Steps.Any(item => item.Name == "Quality" && item.Outcome == "Needs review"))
                {
                    await VerifyAsync(path, track.ContentSha256!, track.Length, token);
                    if (track.ApprovedByUserId == null) { track.Outcome = "Needs review"; continue; }
                    await Step(track, "Quality", "Complete", "Quality result accepted: " + track.ApprovalReason);
                }
                if (!track.Steps.Any(item => item.Name == "Ceiling"))
                {
                    track.Step = "Ceiling";
                    await ConvertCeilingAsync(run, track, album.Profile, token);
                    path = TrackPath(run.Id, track);
                    await Step(track, "Ceiling", "Complete", "Audio is within the profile ceiling.");
                }
                if (!track.Steps.Any(item => item.Name == "Lyrics"))
                {
                    track.Step = "Lyrics";
                    await VerifyAsync(path, track.ContentSha256!, track.Length, token);
                    using (var file = TagLib.File.Create(path)) track.DurationMilliseconds = (long)file.Properties.Duration.TotalMilliseconds;
                    var found = await PromotionLyricsAsync(access.Context, track, album.Profile, token);
                    var lyricsStep = new PromotionTrackStep("Lyrics", found == null ? "None fits" : "Complete",
                        found == null ? "No rank-1 lyrics version fits the measured file duration." : "Rank-1 lyrics fit the measured file duration.");
                    if (found != null)
                    {
                        if (album.Profile.LyricsMode is "both" or ".lrc") await WritePrivateAsync(Path.ChangeExtension(path, ".lrc"), found, token);
                    }
                    await WriteTrackTagsAsync(run, track, new() { ["lyrics"] = found != null && album.Profile.LyricsMode is "both" or "embedded" ? found : "" }, Persist, token, lyricsStep);
                    await Step(track, lyricsStep.Name, lyricsStep.Outcome, lyricsStep.Reason);
                }
                if (!track.Steps.Any(item => item.Name == "Tags"))
                {
                    track.Step = "Tags";
                    var tags = TrackTags(track, album.Profile);
                    byte[]? coverBytes = null;
                    if (artwork != null && album.Profile.CoverMode != "none" && track.Song.ExternalProvider != null && track.Song.ExternalId != null)
                    {
                        try { coverBytes = (await artwork.ResolveArtworkAsync($"ext-{track.Song.ExternalProvider}-song-{track.Song.ExternalId}", access.Context, token))?.Content; }
                        catch (HttpRequestException) { }
                    }
                    if (album.Profile.CoverMode != "none" && (coverBytes == null || !PromotionCover.Supported(coverBytes)))
                    {
                        using var file = TagLib.File.Create(path);
                        coverBytes = file.Tag.Pictures.FirstOrDefault()?.Data.Data;
                    }
                    if (coverBytes != null && PromotionCover.Supported(coverBytes))
                    {
                        if (album.Profile.CoverMode is "both" or "embedded") tags["cover"] = Convert.ToBase64String(coverBytes);
                        if (album.Profile.CoverMode is "both" or "cover.jpg" && PromotionCover.Jpeg(coverBytes) is { } jpeg)
                            await WritePrivateBytesAsync(Path.Combine(workspace, "cover.jpg"), jpeg, token);
                    }
                    if (album.Profile.CoverMode is "none" or "cover.jpg") tags["cover"] = "";
                    await WriteTrackTagsAsync(run, track, tags, Persist, token, new("Tags", "Complete", "Release metadata and profile artwork written."));
                    await Step(track, "Tags", "Complete", "Release metadata and profile artwork written.");
                }
                track.Outcome = "Ready";
                await Persist();
            }
            catch (Exception error) when (error is IOException or HttpRequestException or TagLib.CorruptFileException or TagLib.UnsupportedFormatException)
            {
                if (track.Provider != null)
                {
                    track.Reason = "The source could not be prepared; trying the next source.";
                    NextSource(track);
                    await Step(track, "Acquire", "Running", track.Reason);
                }
                else
                {
                    track.Outcome = "Needs review";
                    await Step(track, track.Step, "Needs review", "This source could not be prepared; try another source or skip this song.");
                }
            }
        }
        var ready = album.Tracks.Where(item => item.Outcome == "Ready").ToArray();
        var partialAlbum = album.Tracks.Any(item => item.Outcome is "Queued" or "Needs review" or "Placed");
        if (album.Profile.ReplayGain && ready.Any(item => !item.Steps.Any(step => step.Name == "ReplayGain")))
        {
            if (album.ReplayGainTags != null && ready.Any(item => !album.ReplayGainTags.ContainsKey(item.RecordingId)))
                album.ReplayGainTags = null;
            if (album.ReplayGainTags == null)
            {
                run.Step = "ReplayGain";
                run.Reason = partialAlbum ? "Measuring track loudness and gain for the ready batch; the complete album is not available." : "Measuring track and album loudness and true peaks.";
                foreach (var track in ready) track.Step = "ReplayGain";
                await Persist();
                foreach (var track in ready) await VerifyAsync(TrackPath(run.Id, track), track.ContentSha256!, track.Length, token);
                var gain = await (replayGain ?? new PromotionReplayGain(conversions)).AnalyzeAsync(ready.Select(track => TrackPath(run.Id, track)).ToArray(), token);
                album.ReplayGainTags = ready.Select((track, index) => (track.RecordingId, Tags: gain.Tracks[index].Tags.Concat(gain.AlbumTags).ToDictionary(item => item.Key, item => item.Value)))
                    .ToDictionary(item => item.RecordingId, item => item.Tags);
                await Persist();
            }
            foreach (var track in ready.Where(item => !item.Steps.Any(step => step.Name == "ReplayGain")))
            {
                var gainReason = partialAlbum ? "Track gain and ready-batch group gain measured at the -18 LUFS reference with true peak; this is not complete album gain." :
                    "Track and album gain measured at the -18 LUFS reference with true peak.";
                await WriteTrackTagsAsync(run, track, album.ReplayGainTags[track.RecordingId], Persist, token,
                    new("ReplayGain", "Complete", gainReason));
                await Step(track, "ReplayGain", "Complete", gainReason);
            }
            album.ReplayGainComplete = true;
            await Persist();
        }
        var unplaced = ready.Where(item => item.ManagedFileId == null).ToArray();
        if (unplaced.Length > 0) roots.RequireFreeSpace(root.Path, unplaced.Sum(item => item.Length));
        foreach (var track in ready)
        {
            if (track.ManagedFileId == null)
            {
                var current = await libraryAccess.ResolveUserAsync(run.OwnerUserId, token);
                if (current.Context == null || !current.Access.Allows(request.BackendLibraryId)) throw new UnauthorizedAccessException();
                await VerifyAsync(TrackPath(run.Id, track), track.ContentSha256!, track.Length, token);
                var destination = await PromotionDestinationNaming.ResolveAsync(db, current.Context, current.Access, root, album.Profile, track.Song, request.NamingYear, token);
                if (destination.RequiresChoice) throw new PromotionException(PromotionFailure.DestinationChoiceRequired);
                var relative = PromotionNaming.RenderForPromotion(album.Profile.NamingScript, PromotionSongFields.FromSong(track.Song, track.Extension, namingYear: request.NamingYear)).RelativePath
                    ?? throw new ArgumentException("The naming script does not produce a safe path.");
                using var scope = scopes.CreateScope();
                var placed = await scope.ServiceProvider.GetRequiredService<FilePlacementService>().PlacePromotionAsync(new(
                    new(root.Id, root.Path, run.OwnerUserId), TrackPath(run.Id, track), album.Profile.NamingScript,
                    new(track.Song.Title, track.Song.Artist, track.Song.Album, track.Song.AlbumArtist, Year: track.Song.Year, Track: track.Song.Track, Extension: track.Extension!),
                    execution.Claim.JobId, ManagedFileScopeKey.Create(run.OwnerUserId, root.Id), true, false, track.ContentSha256!, track.Length)
                { ReferenceKey = $"promotion:{run.Id:N}:{track.RecordingId:N}" }, token, relative, collisionSafe: true);
                track.ManagedFileId = placed.File.Id;
                track.DestinationPath = placed.File.CanonicalPath;
                await Persist();
            }
            await PlaceSidecarsAsync(run.Id, track, token);
            track.Outcome = "Placed";
            await Step(track, "Placement", "Complete", "Placed with a durable library reference.");
        }
        var handoverTracks = album.Tracks.Where(item => item.RefreshJobId == null &&
            (item.Outcome == "Placed" || item.Steps.Any(step => step.Name == "Dedupe"))).ToArray();
        var handoverRecordings = handoverTracks.Select(item => item.RecordingId).Distinct().ToArray();
        if (handoverRecordings.Length > 0)
        {
            var batchHash = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                string.Join('-', handoverRecordings.Order().Select(id => id.ToString("N"))))));
            var batchKey = album.RefreshJobId == null ? $"promotion:{run.Id:N}" : $"promotion:{run.Id:N}:batch:{batchHash}";
            var queued = await jobs.EnqueueAsync(new DurableJobEnqueueRequest<BackendLibraryRefreshJobPayload>("library.refresh",
                batchKey, new(request.BackendLibraryId, access.Context.BackendInstanceId,
                    access.Context.VerifiedBackendPrincipalId, request.CredentialReferenceId)
                { RecordingIds = handoverRecordings, RemoveKeptCopy = album.Profile.RemoveKeptCopy }, run.OwnerUserId), token);
            album.RefreshJobId = queued.JobId;
            foreach (var track in handoverTracks) track.RefreshJobId = queued.JobId;
        }
        run.ManagedFileId = album.Tracks.FirstOrDefault(item => item.ManagedFileId != null)?.ManagedFileId;
        if (album.Tracks.Any(item => item.Outcome == "Queued"))
        {
            run.Outcome = "Running";
            run.Reason = "Ready songs were placed; waiting for the remaining managed downloads.";
            await Persist();
            return DurableJobCompletion.Defer("promotion_download_pending", run.Reason, TimeSpan.FromSeconds(10));
        }
        if (album.Tracks.Any(item => item.Outcome == "Needs review"))
        {
            run.Outcome = "Needs review";
            run.Reason = "Ready songs were placed; review the held songs individually.";
            await Persist();
            return DurableJobCompletion.Success();
        }
        run.Outcome = "Placed";
        run.Step = "Refresh";
        run.Reason = album.Tracks.All(item => item.Outcome == "Skipped") ? "All tracks were skipped." : "Placement complete; refresh and recording handover queued.";
        await Persist();
        Cleanup(run.Id);
        return DurableJobCompletion.Success();
    }

    private string TrackWorkspace(Guid runId, PromotionTrack track) => Path.Combine(Workspace(runId), $"{track.RecordingId:N}-{track.SourceAttempt}");
    private string TrackPath(Guid runId, PromotionTrack track) => Path.Combine(TrackWorkspace(runId, track), track.WorkingFileName ?? "song" + track.Extension);

    private static void NextSource(PromotionTrack track)
    {
        track.SourceAttempts.Add(new(track.Provider, track.ArtifactId, track.Outcome, track.Reason));
        if (track.Provider != null) track.RejectedProviders.Add(track.Provider);
        track.SourceAttempt++;
        track.AcquisitionJobId = null;
        track.ArtifactId = null;
        track.ContentSha256 = null;
        track.Extension = null;
        track.WorkingFileName = null;
        track.Provider = null;
        track.ApprovedByUserId = null;
        track.ApprovalReason = null;
        track.QualityCheck = null;
        track.PendingTags = null;
        track.PendingMetadataStep = null;
        track.Steps.Clear();
        track.Outcome = "Queued";
        track.Step = "Acquire";
    }

    private async Task WriteTrackTagsAsync(PromotionRunRecord run, PromotionTrack track, Dictionary<string, string> tags,
        Func<Task> persist, CancellationToken token, PromotionTrackStep step)
    {
        track.PendingTags ??= tags;
        track.PendingMetadataStep ??= step;
        tags = track.PendingTags;
        step = track.PendingMetadataStep;
        track.Step = step.Name;
        run.Step = step.Name;
        run.Reason = step.Reason;
        await persist();
        var operation = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(tags)));
        var result = await writer.WriteAsync(new(TrackPath(run.Id, track), track.ContentSha256!, true, false)
        { TargetRootPath = TrackWorkspace(run.Id, track), OperationFingerprint = operation }, tags, token);
        await using var lease = result.Lease;
        track.ContentSha256 = result.ContentSha256;
        track.Length = result.Length;
        track.Steps.RemoveAll(item => item.Name == step.Name);
        track.Steps.Add(step);
        track.PendingTags = null;
        track.PendingMetadataStep = null;
        await persist();
        if (lease != null) await lease.CommitAsync(token);
    }

    private static Dictionary<string, string> TrackTags(PromotionTrack track, PromotionProfile profile)
    {
        var song = track.Song;
        var tags = new Dictionary<string, string>
        {
            ["title"] = song.Title,
            ["artist"] = song.Artist,
            ["album"] = song.Album,
            ["albumArtist"] = song.AlbumArtist ?? song.Artist,
            ["track"] = (song.Track ?? 0).ToString(CultureInfo.InvariantCulture),
            ["disc"] = (song.DiscNumber ?? 1).ToString(CultureInfo.InvariantCulture),
            ["totaltracks"] = (song.TotalTracks ?? 0).ToString(CultureInfo.InvariantCulture),
            ["totaldiscs"] = (song.TotalDiscs ?? 1).ToString(CultureInfo.InvariantCulture),
            ["year"] = song.Year?.ToString(CultureInfo.InvariantCulture) ?? "",
            ["date"] = song.ReleaseDate ?? "",
            ["isrc"] = song.Isrc ?? ""
        };
        if (profile.MusicBrainzTags)
        {
            foreach (var key in new[] { "musicbrainz_recordingid", "musicbrainz_releaseid", "musicbrainz_releasegroupid", "musicbrainz_artistid" }) tags[key] = "";
            foreach (var pair in song.ProviderIds.Where(pair => pair.Key is "musicbrainz_recordingid" or "musicbrainz_releaseid" or
                         "musicbrainz_releasegroupid" or "musicbrainz_artistid" or "originaldate")) tags[pair.Key] = pair.Value;
        }
        foreach (var key in new[] { "track", "totaltracks", "year", "date" })
            if (tags[key] is "" or "0") tags.Remove(key);
        if (track.Lossy) tags["comment"] = "Lossy source";
        return tags;
    }

    private static Task WritePrivateAsync(string path, string content, CancellationToken token) => WritePrivateBytesAsync(path, System.Text.Encoding.UTF8.GetBytes(content), token);

    private static async Task WritePrivateBytesAsync(string path, byte[] content, CancellationToken token)
    {
        PromotionRoots.RejectLinks(path);
        var temporary = path + ".partial";
        PromotionRoots.RejectLinks(temporary);
        await File.WriteAllBytesAsync(temporary, content, token);
        File.Move(temporary, path, true);
    }

    private async Task<string?> PromotionLyricsAsync(ProtocolExecutionContext context, PromotionTrack track, PromotionProfile profile, CancellationToken token)
    {
        if (lyrics == null || profile.LyricsMode == "off" || track.DurationMilliseconds is not > 0) return null;
        var song = MusicLyrics.ForLookup(track.Song);
        song.PreciseDuration = TimeSpan.FromMilliseconds(track.DurationMilliseconds.Value);
        song.Duration = null;
        var lookup = new ProtocolExecutionContext(context.Protocol, context.BackendInstanceId, context.VerifiedBackendPrincipalId,
            context.Principal, context.CorrelationId, DateTimeOffset.UtcNow.AddMinutes(5), token)
        { Policy = context.Policy };
        var candidates = await lyrics.FindAllAsync(lookup, song, track.RecordingId.ToString("N"), new() { ForPermanentSidecar = true });
        return SelectPromotionLyrics(candidates, song, track.DurationMilliseconds.Value);
    }

    internal static string? SelectPromotionLyrics(IReadOnlyList<ProtocolLyricsCandidate> candidates, allstarr.Models.Domain.Song song, long measuredDurationMilliseconds)
    {
        var ranked = candidates.Select(item => (Candidate: item, Score: LyricsQuality.Evaluate(item.Lyrics, song, measuredDurationMilliseconds)))
            .Where(item => item.Candidate.AutomaticEligible && item.Score != null).OrderByDescending(item => item.Score!.Score)
            .ThenByDescending(item => item.Score!.LineCount).FirstOrDefault();
        if (ranked.Score?.Reasons.Contains("duration_fits") != true) return null;
        return ranked.Candidate.Lyrics is { } selected ? LyricsTiming.SidecarContent(selected, measuredDurationMilliseconds) : null;
    }

    private async Task ConvertCeilingAsync(PromotionRunRecord run, PromotionTrack track, PromotionProfile profile, CancellationToken token)
    {
        if (!PromotionQuality.IsLossless(track.Song.Codec) ||
            !(track.Song.BitDepth > profile.HiResCeiling.MaxBitDepth || track.Song.SampleRate > profile.HiResCeiling.MaxSampleRate)) return;
        var source = new allstarr.Core.Capabilities.ProviderMediaFormat("audio/" + track.Extension!.TrimStart('.'), track.Extension.TrimStart('.'), track.Song.Codec!,
            sampleRate: track.Song.SampleRate, bitDepth: track.Song.BitDepth);
        var delivery = new ProviderAudioDeliveryRequest
        {
            MaxSampleRate = profile.HiResCeiling.MaxSampleRate,
            MaxBitDepth = profile.HiResCeiling.MaxBitDepth,
            SampleRate = Math.Min(track.Song.SampleRate ?? profile.HiResCeiling.MaxSampleRate, profile.HiResCeiling.MaxSampleRate),
            PreferredCodec = "flac",
            Formats = [new(["flac"], ["flac"], [])]
        };
        var plan = ProviderAudioTranscoder.Plan(delivery, source) ?? throw new IOException("The quality ceiling could not be applied.");
        if (!PromotionQuality.Accepts(profile, plan.Media.Codec, plan.Media.BitDepth, plan.Media.SampleRate))
            throw new IOException("The ceiling conversion would fall below the quality floor.");
        var oldPath = TrackPath(run.Id, track);
        await VerifyAsync(oldPath, track.ContentSha256!, track.Length, token);
        var converted = Path.Combine(TrackWorkspace(run.Id, track), "converted.flac");
        PromotionRoots.RejectLinks(converted);
        using var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StreamContent(File.OpenRead(oldPath)) };
        var outgoing = new DefaultHttpContext().Response;
        await ProviderAudioTranscoder.ApplyAsync(response, outgoing, plan, conversions, token);
        await using (var output = new FileStream(converted, FileMode.Create, FileAccess.Write, FileShare.None)) await response.Content.CopyToAsync(output, token);
        using (var file = TagLib.File.Create(converted))
        {
            if (!PromotionQuality.Accepts(profile, file.Properties.Codecs.OfType<TagLib.IAudioCodec>().FirstOrDefault()?.Description.Contains("FLAC", StringComparison.OrdinalIgnoreCase) == true ? "flac" : plan.Media.Codec,
                    file.Properties.BitsPerSample, file.Properties.AudioSampleRate)) throw new IOException("The converted file failed its quality floor.");
        }
        var newPath = Path.Combine(TrackWorkspace(run.Id, track), "ceiling.flac");
        PromotionRoots.RejectLinks(newPath);
        File.Move(converted, newPath, true);
        track.Extension = ".flac";
        track.WorkingFileName = "ceiling.flac";
        track.Song.Codec = plan.Media.Codec;
        track.Song.BitDepth = plan.Media.BitDepth;
        track.Song.SampleRate = plan.Media.SampleRate;
        await using var stream = File.OpenRead(newPath);
        track.ContentSha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
        track.Length = stream.Length;
    }

    private async Task PlaceSidecarsAsync(Guid runId, PromotionTrack track, CancellationToken token)
    {
        var path = TrackPath(runId, track);
        foreach (var pair in new[] { (Source: Path.ChangeExtension(path, ".lrc"), Target: Path.ChangeExtension(track.DestinationPath!, ".lrc")),
                     (Source: Path.Combine(TrackWorkspace(runId, track), "cover.jpg"), Target: Path.Combine(Path.GetDirectoryName(track.DestinationPath!)!, "cover.jpg")) })
        {
            PromotionRoots.RejectLinks(pair.Source);
            PromotionRoots.RejectLinks(pair.Target);
            if (!File.Exists(pair.Source)) continue;
            var bytes = await File.ReadAllBytesAsync(pair.Source, token);
            if (File.Exists(pair.Target))
            {
                if (pair.Target.EndsWith("cover.jpg", StringComparison.Ordinal)) continue;
                if (!(await File.ReadAllBytesAsync(pair.Target, token)).AsSpan().SequenceEqual(bytes)) throw new IOException("An unrelated lyrics sidecar already exists.");
            }
            else
            {
                var temporary = Path.Combine(Path.GetDirectoryName(pair.Target)!, $".allstarr-sidecar-{Guid.NewGuid():N}.partial");
                PromotionRoots.RejectLinks(temporary);
                try
                {
                    await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        await output.WriteAsync(bytes, token);
                        await output.FlushAsync(token);
                    }
                    PromotionRoots.RejectLinks(pair.Target);
                    File.Move(temporary, pair.Target, false);
                    LibraryFileOwner.MatchParent(pair.Target);
                }
                finally { File.Delete(temporary); }
            }
        }
    }
}

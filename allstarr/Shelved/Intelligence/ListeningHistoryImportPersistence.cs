using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Playback;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Intelligence;

public enum ListeningHistoryImportState
{
    Previewed,
    Pending,
    Running,
    Completed,
    Cancelled,
    Failed,
    Expired
}

public sealed class ListeningHistoryImportRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid OwnerUserId { get; set; }
    public string Protocol { get; set; } = "";
    public string BackendInstanceId { get; set; } = "";
    public string LibraryScopeId { get; set; } = "";
    public string DisplayFileName { get; set; } = "";
    public string Format { get; set; } = "";
    public string ContentSha256 { get; set; } = "";
    public long SizeBytes { get; set; }
    public string PreviewJson { get; set; } = "{}";
    public string PreviewRevision { get; set; } = "";
    public ListeningHistoryImportState State { get; set; }
    public Guid? JobId { get; set; }
    public int ApplyGeneration { get; set; }
    public long NextSequence { get; set; }
    public long ImportedRows { get; set; }
    public long DuplicateRows { get; set; }
    public long ResolvedRows { get; set; }
    public long UnresolvedRows { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public long Revision { get; set; }

    public void ExpireWithoutArtifact()
    {
        if (State is not (ListeningHistoryImportState.Previewed or ListeningHistoryImportState.Pending or ListeningHistoryImportState.Running)) return;
        State = ListeningHistoryImportState.Expired;
        Revision++;
    }
}

internal static class ListeningHistoryImportStateTransfer
{
    public static HashSet<Guid> ExpireActiveImports(IEnumerable<ListeningHistoryImportRecord> imports)
    {
        var jobIds = imports.Where(item => item.State is ListeningHistoryImportState.Previewed or
                ListeningHistoryImportState.Pending or ListeningHistoryImportState.Running)
            .Select(item => item.JobId).OfType<Guid>().ToHashSet();
        foreach (var import in imports) import.ExpireWithoutArtifact();
        return jobIds;
    }

    public static void CancelJobs(IEnumerable<DurableJobRecord> jobs, IReadOnlySet<Guid> jobIds, DateTimeOffset now)
    {
        foreach (var job in jobs.Where(item => jobIds.Contains(item.Id) &&
                     item.State is DurableJobState.Pending or DurableJobState.RetryScheduled or DurableJobState.Running))
        {
            job.State = DurableJobState.Cancelled;
            job.CancellationRequestedAt ??= now;
            job.CompletedAt = now;
            job.LeaseOwner = null;
            job.LeaseExpiresAt = null;
            job.UpdatedAt = now;
            job.Revision++;
        }
    }

    public static void CancelAttempts(IEnumerable<JobAttemptRecord> attempts, IReadOnlySet<Guid> jobIds, DateTimeOffset now)
    {
        foreach (var attempt in attempts.Where(item => jobIds.Contains(item.JobId) && item.CompletedAt == null))
        {
            attempt.CompletedAt = now;
            attempt.Outcome = "cancelled";
            attempt.ErrorCode = "history_import_artifact_not_transferred";
            attempt.ErrorMessage = "The private upload artifact is not included in state transfer.";
        }
    }
}

public sealed record ListeningHistoryImportPreview(
    string Format,
    long FileRows,
    long MusicRows,
    long Completed,
    long Partial,
    long Skipped,
    long Episodes,
    long NonTrack,
    long Malformed,
    long DuplicateInFile,
    long DuplicateExisting,
    long NewRows,
    long ResolvedNewRows,
    long UnresolvedNewRows,
    long RowsWithoutProviderIdentity,
    int SourceUserCount,
    int EstimatedMusicBrainzLookups,
    DateTimeOffset? Earliest,
    DateTimeOffset? Latest,
    IReadOnlyDictionary<string, long> ReasonCounts,
    long OutsideRetentionRows = 0);

public sealed record ListeningHistoryImportPreviewResult(
    Guid ImportId,
    string Revision,
    string DisplayFileName,
    long SizeBytes,
    DateTimeOffset ExpiresAt,
    ListeningHistoryImportState State,
    Guid? JobId,
    string? JobState,
    string? LastErrorCode,
    string? LastErrorMessage,
    long ImportedRows,
    long DuplicateRows,
    long ResolvedRows,
    long UnresolvedRows,
    ListeningHistoryImportPreview Preview);

public sealed record ListeningHistoryImportRemovalResult(long RemovedListens);

public sealed class ListeningHistoryImportOptions
{
    public const string SectionName = "Intelligence:HistoryImport";
    public string RootPath { get; set; } = "/app/cache/listening-history-imports";
    public long MaximumUploadBytes { get; set; } = 64L * 1024 * 1024;
    public int MaximumRows { get; set; } = 1_000_000;
    public int PreviewLifetimeHours { get; set; } = 24;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(RootPath) || MaximumUploadBytes is < 1 or > 1024L * 1024 * 1024 ||
            MaximumRows is < 1 or > 10_000_000 || PreviewLifetimeHours is < 1 or > 168)
            throw new InvalidOperationException("Listening-history import limits are invalid.");
    }
}

public sealed record ListeningHistoryImportArtifact(string ContentSha256, long SizeBytes);

public sealed class ListeningHistoryImportArtifactStore(ListeningHistoryImportOptions options)
{
    public async Task<ListeningHistoryImportArtifact> StageAsync(
        Guid importId,
        Stream source,
        long expectedBytes,
        CancellationToken cancellationToken)
    {
        if (importId == Guid.Empty || expectedBytes is < 1 || expectedBytes > options.MaximumUploadBytes || !source.CanRead)
            throw new ArgumentException("The listening-history upload is invalid.");
        var root = Root();
        var destination = PathFor(root, importId);
        var partial = destination + ".partial-" + Guid.NewGuid().ToString("N");
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            long written = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                int read;
                while ((read = await source.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
                {
                    if (written > options.MaximumUploadBytes - read)
                        throw new InvalidDataException("The listening-history upload exceeds the size limit.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    hash.AppendData(buffer, 0, read);
                    written += read;
                }
                await output.FlushAsync(cancellationToken);
            }
            if (written != expectedBytes)
                throw new InvalidDataException("The listening-history upload length changed during transfer.");
            File.Move(partial, destination, overwrite: false);
            return new(Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), written);
        }
        catch
        {
            if (File.Exists(partial)) File.Delete(partial);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    public Stream OpenRead(Guid importId)
    {
        var path = PathFor(Root(), importId);
        if (!File.Exists(path)) throw new FileNotFoundException("The listening-history upload is unavailable.");
        RejectSymlink(path);
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    public async Task VerifyAsync(
        Guid importId,
        string expectedSha256,
        long expectedBytes,
        CancellationToken cancellationToken)
    {
        await using var source = OpenRead(importId);
        if (source.Length != expectedBytes)
            throw new InvalidDataException("The listening-history upload length no longer matches its preview.");
        var actual = await SHA256.HashDataAsync(source, cancellationToken);
        byte[] expected;
        try { expected = Convert.FromHexString(expectedSha256); }
        catch (FormatException exception) { throw new InvalidDataException("The stored upload checksum is invalid.", exception); }
        if (expected.Length != SHA256.HashSizeInBytes || !CryptographicOperations.FixedTimeEquals(actual, expected))
            throw new InvalidDataException("The listening-history upload changed after preview.");
    }

    public void Delete(Guid importId)
    {
        var path = PathFor(Root(), importId);
        if (!File.Exists(path)) return;
        RejectSymlink(path);
        File.Delete(path);
    }

    private string Root()
    {
        options.Validate();
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.RootPath));
        Directory.CreateDirectory(root);
        RejectSymlink(root);
        return root;
    }

    private static string PathFor(string root, Guid importId)
    {
        var path = Path.GetFullPath(Path.Combine(root, $"{importId:N}.json"));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The listening-history upload escapes its workspace.");
        return path;
    }

    private static void RejectSymlink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("Listening-history upload paths may not be symbolic links.");
    }
}

public sealed class ListeningHistoryImportService(
    IDbContextFactory<AllstarrDbContext> factory,
    ListeningHistoryImporterRegistry importers,
    ListeningHistoryImportArtifactStore artifacts,
    ListeningHistoryImportOptions options,
    IPlatformClock clock,
    DurableJobQueue jobs)
{
    public async Task<ListeningHistoryImportPreviewResult> PreviewAsync(
        IntelligenceScope scope,
        string displayFileName,
        Stream content,
        long sizeBytes,
        CancellationToken cancellationToken)
    {
        options.Validate();
        displayFileName = Path.GetFileName(displayFileName).Trim();
        if (displayFileName.Length is < 1 or > 255 || displayFileName.Any(char.IsControl))
            throw new ListeningHistoryImportException("history_import_filename_invalid", "The selected filename is invalid.");
        if (displayFileName.StartsWith("Streaming_History_Video_", StringComparison.OrdinalIgnoreCase) ||
            displayFileName.Equals("Streaming_History_Video.json", StringComparison.OrdinalIgnoreCase))
            throw new ListeningHistoryImportException(
                "history_import_video_unsupported",
                "Spotify video viewing history is not music listening history. Choose the Streaming_History_Audio JSON files instead.");
        if (sizeBytes is < 1 || sizeBytes > options.MaximumUploadBytes)
            throw new ListeningHistoryImportException(
                "history_import_file_invalid",
                $"Choose a history file up to {options.MaximumUploadBytes / (1024 * 1024)} MB.");
        var importId = Guid.CreateVersion7();
        var previewedAt = clock.UtcNow;
        try
        {
            var artifact = await artifacts.StageAsync(importId, content, sizeBytes, cancellationToken);
            await using var policyDb = await factory.CreateDbContextAsync(cancellationToken);
            var retentionDays = await IntelligencePolicyService.Query(policyDb, scope).AsNoTracking()
                .Select(item => (int?)item.RetentionDays)
                .SingleOrDefaultAsync(cancellationToken);
            var retentionCutoff = retentionDays == null
                ? (DateTimeOffset?)null
                : IntelligencePolicyService.RetentionCutoff(previewedAt, retentionDays.Value);
            var accumulator = new PreviewAccumulator(factory, scope, retentionCutoff);
            var scan = await importers.ScanAsync(
                () => artifacts.OpenRead(importId),
                new(previewedAt, options.MaximumRows),
                accumulator.AddAsync,
                cancellationToken);
            await accumulator.FlushAsync(cancellationToken);
            var preview = new ListeningHistoryImportPreview(
                scan.Format,
                scan.Rows,
                scan.MusicRows,
                scan.Completed,
                scan.Partial,
                scan.Skipped,
                scan.Episodes,
                scan.NonTrack,
                scan.Malformed,
                scan.Duplicate,
                accumulator.DuplicateRows,
                accumulator.NewRows,
                accumulator.ResolvedRows,
                accumulator.UnresolvedRows,
                scan.RowsWithoutProviderIdentity,
                scan.SourceUserCount,
                Math.Min(scan.EstimatedMusicBrainzLookups, (int)Math.Min(int.MaxValue, accumulator.UnresolvedRows)),
                scan.Earliest,
                scan.Latest,
                scan.ReasonCounts,
                accumulator.OutsideRetentionRows);
            var previewJson = JsonSerializer.Serialize(preview);
            var revision = Revision(scope, scan.Format, importers.RevisionFor(scan.Format), artifact.ContentSha256, previewJson);
            var expiresAt = previewedAt.AddHours(options.PreviewLifetimeHours);
            var previewCompletedAt = clock.UtcNow;
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            db.ListeningHistoryImports.Add(new()
            {
                Id = importId,
                TenantId = scope.TenantId,
                OwnerUserId = scope.OwnerUserId,
                Protocol = scope.Protocol,
                BackendInstanceId = scope.BackendInstanceId,
                LibraryScopeId = scope.LibraryScopeId,
                DisplayFileName = displayFileName,
                Format = scan.Format,
                ContentSha256 = artifact.ContentSha256,
                SizeBytes = artifact.SizeBytes,
                PreviewJson = previewJson,
                PreviewRevision = revision,
                State = ListeningHistoryImportState.Previewed,
                CreatedAt = previewedAt,
                UpdatedAt = previewedAt,
                ExpiresAt = expiresAt,
                Revision = 1
            });
            db.AuditEvents.Add(new AuditEventRecord
            {
                Id = Guid.CreateVersion7(),
                TenantId = scope.TenantId,
                ActorUserId = scope.OwnerUserId,
                Category = "listening-history-import",
                Action = "previewed",
                Outcome = "success",
                CorrelationId = importId.ToString("N"),
                DetailsJson = JsonSerializer.Serialize(new
                {
                    format = scan.Format,
                    sourceProvider = scan.Format,
                    sizeBytes = artifact.SizeBytes,
                    fileRows = scan.Rows,
                    preview.NewRows,
                    duplicateRows = scan.Duplicate + accumulator.DuplicateRows,
                    preview.ResolvedNewRows,
                    preview.UnresolvedNewRows,
                    durationMilliseconds = Math.Max(0L, (long)(previewCompletedAt - previewedAt).TotalMilliseconds),
                    reasonCode = "history_import_previewed",
                    runId = importId
                }),
                CreatedAt = previewCompletedAt
            });
            await db.SaveChangesAsync(cancellationToken);
            return new(importId, revision, displayFileName, artifact.SizeBytes, expiresAt,
                ListeningHistoryImportState.Previewed, null, null, null, null, 0, 0, 0, 0, preview);
        }
        catch
        {
            artifacts.Delete(importId);
            throw;
        }
    }

    public async Task<ListeningHistoryImportPreviewResult?> GetAsync(
        IntelligenceScope scope,
        Guid importId,
        CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var record = await ScopedImport(db, scope, importId).SingleOrDefaultAsync(cancellationToken);
        if (record == null) return null;
        var job = record.JobId == null
            ? null
            : await db.Jobs.AsNoTracking().SingleOrDefaultAsync(item => item.Id == record.JobId, cancellationToken);
        var reconciled = false;
        var deleteArtifact = false;
        if (record.State == ListeningHistoryImportState.Previewed && record.ExpiresAt <= clock.UtcNow)
        {
            record.State = ListeningHistoryImportState.Expired;
            deleteArtifact = reconciled = true;
        }
        else if (record.State is ListeningHistoryImportState.Pending or ListeningHistoryImportState.Running &&
                 job?.State is DurableJobState.Failed or DurableJobState.Cancelled or DurableJobState.Succeeded)
        {
            record.State = job.State switch
            {
                DurableJobState.Cancelled => ListeningHistoryImportState.Cancelled,
                _ => ListeningHistoryImportState.Failed
            };
            deleteArtifact = job.State == DurableJobState.Cancelled;
            reconciled = true;
        }
        if (reconciled)
        {
            if (deleteArtifact) artifacts.Delete(record.Id);
            var now = clock.UtcNow;
            record.CompletedAt = now;
            record.UpdatedAt = now;
            record.Revision++;
            db.AuditEvents.Add(Audit(record, "reconciled", record.State.ToString().ToLowerInvariant(), now));
            await db.SaveChangesAsync(cancellationToken);
        }
        var preview = JsonSerializer.Deserialize<ListeningHistoryImportPreview>(record.PreviewJson)
                      ?? throw new InvalidDataException("The saved listening-history preview is invalid.");
        return new(record.Id, record.PreviewRevision, record.DisplayFileName, record.SizeBytes, record.ExpiresAt,
            record.State, record.JobId, job?.State.ToString().ToLowerInvariant(), job?.LastErrorCode,
            job?.LastErrorMessage, record.ImportedRows, record.DuplicateRows, record.ResolvedRows,
            record.UnresolvedRows, preview);
    }

    public async Task<IReadOnlyList<ListeningHistoryImportPreviewResult>> ListAsync(
        IntelligenceScope scope,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var records = await ScopedImports(db, scope).AsNoTracking()
            .OrderByDescending(item => item.CreatedAt)
            .Take(Math.Clamp(limit, 1, 50))
            .ToListAsync(cancellationToken);
        var jobIds = records.Select(item => item.JobId).OfType<Guid>().Distinct().ToArray();
        var jobsById = jobIds.Length == 0
            ? []
            : await db.Jobs.AsNoTracking().Where(item => jobIds.Contains(item.Id))
                .ToDictionaryAsync(item => item.Id, cancellationToken);

        return records.Select(record =>
        {
            var preview = JsonSerializer.Deserialize<ListeningHistoryImportPreview>(record.PreviewJson)
                          ?? throw new InvalidDataException("The saved listening-history preview is invalid.");
            var job = record.JobId is { } jobId ? jobsById.GetValueOrDefault(jobId) : null;
            return new ListeningHistoryImportPreviewResult(
                record.Id, record.PreviewRevision, record.DisplayFileName, record.SizeBytes, record.ExpiresAt,
                record.State, record.JobId, job?.State.ToString().ToLowerInvariant(), job?.LastErrorCode,
                job?.LastErrorMessage, record.ImportedRows, record.DuplicateRows, record.ResolvedRows,
                record.UnresolvedRows, preview);
        }).ToArray();
    }

    public Task<ListeningHistoryImportPreviewResult?> ApplyAsync(
        IntelligenceScope scope,
        Guid importId,
        string expectedRevision,
        CancellationToken cancellationToken) =>
        QueueAsync(scope, importId, expectedRevision, resume: false, cancellationToken);

    public Task<ListeningHistoryImportPreviewResult?> ResumeAsync(
        IntelligenceScope scope,
        Guid importId,
        string expectedRevision,
        CancellationToken cancellationToken) =>
        QueueAsync(scope, importId, expectedRevision, resume: true, cancellationToken);

    public async Task<ListeningHistoryImportPreviewResult?> CancelAsync(
        IntelligenceScope scope,
        Guid importId,
        string expectedRevision,
        CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var record = await ScopedImport(db, scope, importId).SingleOrDefaultAsync(cancellationToken);
        if (record == null) return null;
        RequireRevision(record, expectedRevision);
        if (record.State is ListeningHistoryImportState.Completed or ListeningHistoryImportState.Expired)
            throw new ListeningHistoryImportException(
                "history_import_state_conflict",
                "This history import can no longer be cancelled.");

        var cancelled = record.JobId == null;
        if (record.JobId is { } jobId)
        {
            await jobs.RequestCancellationAsync(jobId, scope.TenantId, cancellationToken);
            cancelled = await db.Jobs.AsNoTracking().Where(item => item.Id == jobId)
                .Select(item => item.State == DurableJobState.Cancelled)
                .SingleOrDefaultAsync(cancellationToken);
        }
        if (cancelled || record.State is ListeningHistoryImportState.Previewed or ListeningHistoryImportState.Failed)
        {
            var now = clock.UtcNow;
            record.State = ListeningHistoryImportState.Cancelled;
            record.CompletedAt = now;
            record.UpdatedAt = now;
            record.Revision++;
            db.AuditEvents.Add(Audit(record, "cancelled", "success", now));
            await db.SaveChangesAsync(cancellationToken);
            artifacts.Delete(importId);
        }
        return await GetAsync(scope, importId, cancellationToken);
    }

    public async Task<ListeningHistoryImportRemovalResult?> RemoveAsync(
        IntelligenceScope scope,
        Guid importId,
        string expectedRevision,
        CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var record = await ScopedImport(db, scope, importId).SingleOrDefaultAsync(cancellationToken);
        if (record == null) return null;
        RequireRevision(record, expectedRevision);
        if (record.State is ListeningHistoryImportState.Pending or ListeningHistoryImportState.Running)
            throw new ListeningHistoryImportException(
                "history_import_state_conflict",
                "Cancel this active history import before removing it.");

        var provenance = $"history-import:{importId:N}:";
        var importedEvents = ScopedListeningEvents(db, scope).Where(item =>
            item.SourceKind == "import" && item.ImportProvenance != null &&
            item.ImportProvenance.StartsWith(provenance));
        var occurrenceKeys = importedEvents.Select(item => item.OccurrenceKey);
        await db.Set<PlaybackDeliveryCheckpointEntity>().Where(item =>
                item.TenantId == scope.TenantId && item.OwnerUserId == scope.OwnerUserId &&
                item.OccurrenceKey != null && occurrenceKeys.Contains(item.OccurrenceKey))
            .ExecuteDeleteAsync(cancellationToken);
        var removedListens = await importedEvents.ExecuteDeleteAsync(cancellationToken);

        var now = clock.UtcNow;
        db.AuditEvents.Add(Audit(record, "removed", "success", now));
        db.ListeningHistoryImports.Remove(record);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        artifacts.Delete(importId);
        return new(removedListens);
    }

    private async Task<ListeningHistoryImportPreviewResult?> QueueAsync(
        IntelligenceScope scope,
        Guid importId,
        string expectedRevision,
        bool resume,
        CancellationToken cancellationToken)
    {
        await using var initialDb = await factory.CreateDbContextAsync(cancellationToken);
        var initial = await ScopedImport(initialDb, scope, importId).AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);
        if (initial == null) return null;
        RequireRevision(initial, expectedRevision);
        var requiredState = resume ? ListeningHistoryImportState.Failed : ListeningHistoryImportState.Previewed;
        if (initial.State != requiredState)
            throw new ListeningHistoryImportException(
                "history_import_state_conflict",
                resume ? "Only a failed history import can be resumed." : "This history import was already applied or cancelled.");
        if (initial.ExpiresAt <= clock.UtcNow)
            throw new ListeningHistoryImportException("history_import_expired", "This history import preview has expired.");
        if (initial.PreviewRevision != Revision(
                scope,
                initial.Format,
                importers.RevisionFor(initial.Format),
                initial.ContentSha256,
                initial.PreviewJson))
            throw new ListeningHistoryImportException(
                "history_import_revision_conflict",
                "The importer changed after this preview. Preview the file again.");
        try
        {
            await artifacts.VerifyAsync(importId, initial.ContentSha256, initial.SizeBytes, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ListeningHistoryImportException(
                "history_import_file_unavailable",
                "The previewed history file is unavailable or changed.",
                exception);
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        var record = await ScopedImport(db, scope, importId).SingleAsync(cancellationToken);
        RequireRevision(record, expectedRevision);
        if (record.State != requiredState)
            throw new ListeningHistoryImportException("history_import_state_conflict", "The history import state changed. Refresh and try again.");
        var generation = checked(record.ApplyGeneration + 1);
        var queued = await jobs.EnqueueInExistingTransactionAsync(db,
            new DurableJobEnqueueRequest<ListeningHistoryImportJobPayload>(
                ListeningHistoryImportJobHandler.JobTypeName,
                Hash($"{record.Id:N}\u001f{record.PreviewRevision}\u001f{generation}"),
                new(record.Id, scope, record.PreviewRevision, generation),
                scope.TenantId,
                scope.OwnerUserId,
                LibraryScopeId: scope.LibraryScopeId,
                CorrelationId: $"history-import:{record.Id:N}"),
            cancellationToken);
        var now = clock.UtcNow;
        record.JobId = queued.JobId;
        record.ApplyGeneration = generation;
        record.State = ListeningHistoryImportState.Pending;
        record.CompletedAt = null;
        record.ExpiresAt = now.AddHours(options.PreviewLifetimeHours);
        record.UpdatedAt = now;
        record.Revision++;
        db.AuditEvents.Add(Audit(record, resume ? "resumed" : "applied", "queued", now));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetAsync(scope, importId, cancellationToken);
    }

    internal static string OccurrenceKey(IntelligenceScope scope, ListeningHistoryImportRow row) =>
        Hash($"{scope.TenantId:N}\u001f{scope.OwnerUserId:N}\u001f{scope.Protocol}\u001f{scope.BackendInstanceId}\u001f{scope.LibraryScopeId}\u001fimport\u001f{row.SourceService}\u001f{row.SourceUserKey}\u001f{row.ListenedAt.ToUnixTimeMilliseconds()}\u001f{row.SourceItemKey}");

    internal static string? ProviderIdentityHash(ListeningHistoryImportRow row)
    {
        const string spotifyPrefix = "spotify:track:";
        return row.SourceService == "spotify" &&
               row.ProviderTrackReference?.StartsWith(spotifyPrefix, StringComparison.Ordinal) == true
            ? Hash(row.ProviderTrackReference[spotifyPrefix.Length..])
            : null;
    }

    private static string Revision(
        IntelligenceScope scope,
        string format,
        string importerRevision,
        string contentSha256,
        string previewJson) =>
        Hash($"{format}\u001f{importerRevision}\u001f{scope.TenantId:N}\u001f{scope.OwnerUserId:N}\u001f{scope.Protocol}\u001f{scope.BackendInstanceId}\u001f{scope.LibraryScopeId}\u001f{contentSha256}\u001f{previewJson}");

    internal static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static IQueryable<ListeningHistoryImportRecord> ScopedImport(
        AllstarrDbContext db,
        IntelligenceScope scope,
        Guid importId) =>
        ScopedImports(db, scope).Where(item => item.Id == importId);

    private static IQueryable<ListeningHistoryImportRecord> ScopedImports(
        AllstarrDbContext db,
        IntelligenceScope scope) =>
        db.ListeningHistoryImports.Where(item =>
            item.TenantId == scope.TenantId && item.OwnerUserId == scope.OwnerUserId &&
            item.Protocol == scope.Protocol && item.BackendInstanceId == scope.BackendInstanceId &&
            item.LibraryScopeId == scope.LibraryScopeId);

    private static IQueryable<ListeningEventRecord> ScopedListeningEvents(
        AllstarrDbContext db,
        IntelligenceScope scope) =>
        db.ListeningEvents.Where(item =>
            item.TenantId == scope.TenantId && item.OwnerUserId == scope.OwnerUserId &&
            item.Protocol == scope.Protocol && item.BackendInstanceId == scope.BackendInstanceId &&
            item.LibraryScopeId == scope.LibraryScopeId);

    private static void RequireRevision(ListeningHistoryImportRecord record, string expectedRevision)
    {
        var normalized = expectedRevision?.Trim().ToLowerInvariant();
        if (normalized is not { Length: 64 } || !normalized.All(Uri.IsHexDigit) ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(record.PreviewRevision),
                Encoding.ASCII.GetBytes(normalized)))
            throw new ListeningHistoryImportException(
                "history_import_revision_conflict",
                "The history import preview changed. Refresh and try again.");
    }

    private static AuditEventRecord Audit(
        ListeningHistoryImportRecord record,
        string action,
        string outcome,
        DateTimeOffset now) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = record.TenantId,
            ActorUserId = record.OwnerUserId,
            Category = "listening-history-import",
            Action = action,
            Outcome = outcome,
            CorrelationId = record.Id.ToString("N"),
            DetailsJson = JsonSerializer.Serialize(new
            {
                importId = record.Id,
                record.ApplyGeneration,
                record.NextSequence,
                record.ImportedRows,
                record.DuplicateRows,
                record.ResolvedRows,
                record.UnresolvedRows,
                sourceProvider = record.Format,
                runId = record.JobId,
                durationMilliseconds = Math.Max(0L, (long)(now - record.CreatedAt).TotalMilliseconds),
                reasonCode = outcome is "success" or "queued" ? $"history_import_{action}" : outcome
            }),
            CreatedAt = now
        };

    private sealed class PreviewAccumulator(
        IDbContextFactory<AllstarrDbContext> factory,
        IntelligenceScope scope,
        DateTimeOffset? retentionCutoff)
    {
        private readonly List<(string OccurrenceKey, string? ExternalIdHash, string? RecordingMbid)> _rows = new(500);
        public long DuplicateRows { get; private set; }
        public long NewRows { get; private set; }
        public long ResolvedRows { get; private set; }
        public long UnresolvedRows { get; private set; }
        public long OutsideRetentionRows { get; private set; }

        public async ValueTask AddAsync(ListeningHistoryImportRow row, CancellationToken cancellationToken)
        {
            if (retentionCutoff is { } cutoff && row.ListenedAt < cutoff)
            {
                OutsideRetentionRows++;
                return;
            }
            _rows.Add((OccurrenceKey(scope, row), ProviderIdentityHash(row), row.RecordingMusicBrainzId));
            if (_rows.Count == 500) await FlushAsync(cancellationToken);
        }

        public async Task FlushAsync(CancellationToken cancellationToken)
        {
            if (_rows.Count == 0) return;
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            var occurrenceKeys = _rows.Select(item => item.OccurrenceKey).ToArray();
            var existing = await db.ListeningEvents.AsNoTracking().Where(item =>
                    item.TenantId == scope.TenantId && item.OwnerUserId == scope.OwnerUserId &&
                    occurrenceKeys.Contains(item.OccurrenceKey))
                .Select(item => item.OccurrenceKey).ToHashSetAsync(cancellationToken);
            var newRows = _rows.Where(item => !existing.Contains(item.OccurrenceKey)).ToArray();
            var identityHashes = newRows.Select(item => item.ExternalIdHash).OfType<string>().Distinct().ToArray();
            var resolved = identityHashes.Length == 0
                ? []
                : await db.ProviderTrackIdentities.AsNoTracking().Where(item =>
                        item.TenantId == scope.TenantId && item.ProviderId == "spotify" &&
                        item.ResourceKind == ProviderResourceKind.Track &&
                        item.Scope == ProviderIdentityScope.Catalog && identityHashes.Contains(item.ExternalIdHash))
                    .Select(item => item.ExternalIdHash).ToHashSetAsync(cancellationToken);
            var recordingMbids = newRows.Select(item => item.RecordingMbid).OfType<string>().Distinct().ToArray();
            var resolvedMbids = recordingMbids.Length == 0
                ? []
                : await db.CanonicalRecordings.AsNoTracking().Where(item =>
                        item.TenantId == scope.TenantId && item.MusicBrainzRecordingId != null &&
                        recordingMbids.Contains(item.MusicBrainzRecordingId))
                    .Select(item => item.MusicBrainzRecordingId!).ToHashSetAsync(cancellationToken);
            DuplicateRows += existing.Count;
            NewRows += newRows.Length;
            ResolvedRows += newRows.LongCount(item =>
                item.ExternalIdHash != null && resolved.Contains(item.ExternalIdHash) ||
                item.RecordingMbid != null && resolvedMbids.Contains(item.RecordingMbid));
            UnresolvedRows += newRows.LongCount(item =>
                (item.ExternalIdHash == null || !resolved.Contains(item.ExternalIdHash)) &&
                (item.RecordingMbid == null || !resolvedMbids.Contains(item.RecordingMbid)));
            _rows.Clear();
        }
    }
}

using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Intelligence;

public sealed record ListeningHistoryImportJobPayload(
    Guid ImportId,
    IntelligenceScope Scope,
    string PreviewRevision,
    int Generation);

public sealed class ListeningHistoryImportJobHandler(
    IDbContextFactory<AllstarrDbContext> factory,
    ListeningHistoryImporterRegistry importers,
    ListeningHistoryImportArtifactStore artifacts,
    ListeningHistoryImportOptions options,
    IPlatformClock clock,
    MusicBrainzListeningEnrichmentQueue musicBrainz) : IDurableJobHandler
{
    public const string JobTypeName = "listening-history.import";
    public string JobType => JobTypeName;

    public async Task<DurableJobCompletion> ExecuteAsync(
        DurableJobExecutionContext execution,
        CancellationToken cancellationToken)
    {
        var payload = execution.Claim.Payload.Deserialize<ListeningHistoryImportJobPayload>();
        if (payload == null || payload.ImportId == Guid.Empty || payload.Generation < 1 ||
            payload.PreviewRevision.Length != 64 || !payload.PreviewRevision.All(Uri.IsHexDigit) ||
            execution.Claim.TenantId != payload.Scope.TenantId ||
            execution.Claim.OwnerUserId != payload.Scope.OwnerUserId ||
            execution.Claim.LibraryScopeId != payload.Scope.LibraryScopeId)
            return DurableJobCompletion.Failure(
                "history_import_job_scope_invalid",
                "The saved history import scope is invalid.");

        ListeningHistoryImportRecord record;
        await using (var db = await factory.CreateDbContextAsync(cancellationToken))
        {
            record = await Query(db, payload, execution.Claim.JobId).SingleOrDefaultAsync(cancellationToken)
                     ?? throw new ListeningHistoryImportException(
                         "history_import_job_missing",
                         "The saved history import is unavailable.");
            if (record.State == ListeningHistoryImportState.Completed)
            {
                artifacts.Delete(record.Id);
                return DurableJobCompletion.Success();
            }
            if (record.State == ListeningHistoryImportState.Cancelled)
            {
                artifacts.Delete(record.Id);
                return DurableJobCompletion.Cancelled();
            }
            if (record.State is not (ListeningHistoryImportState.Pending or ListeningHistoryImportState.Running))
                return DurableJobCompletion.Failure(
                    "history_import_job_state_invalid",
                    "The saved history import is not ready to run.");
            try
            {
                await artifacts.VerifyAsync(record.Id, record.ContentSha256, record.SizeBytes, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await CancelAsync(payload, execution.Claim.JobId);
                return DurableJobCompletion.Cancelled();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                await FailAsync(payload, execution.Claim.JobId, "history_import_artifact_invalid", cancellationToken);
                artifacts.Delete(record.Id);
                return DurableJobCompletion.Failure(
                    "history_import_artifact_invalid",
                    "The previewed history file is unavailable or changed.");
            }
            record.State = ListeningHistoryImportState.Running;
            record.UpdatedAt = clock.UtcNow;
            record.Revision++;
            await db.SaveChangesAsync(cancellationToken);
        }

        var preview = JsonSerializer.Deserialize<ListeningHistoryImportPreview>(record.PreviewJson)
                      ?? throw new ListeningHistoryImportException(
                          "history_import_preview_invalid",
                          "The saved history import preview is invalid.");
        var accumulator = new ApplyAccumulator(factory, payload, execution.Claim.JobId, clock, musicBrainz.Enabled);
        try
        {
            var scan = await importers.ScanAsync(
                () => artifacts.OpenRead(payload.ImportId),
                new(record.CreatedAt, options.MaximumRows),
                accumulator.AddAsync,
                cancellationToken);
            await accumulator.FlushAsync(cancellationToken);
            if (scan.Format != record.Format || scan.Rows != preview.FileRows || scan.MusicRows != preview.MusicRows)
            {
                await FailAsync(payload, execution.Claim.JobId, "history_import_preview_changed", cancellationToken);
                artifacts.Delete(payload.ImportId);
                return DurableJobCompletion.Failure(
                    "history_import_preview_changed",
                    "The history file no longer matches its preview.");
            }
            await QueueMusicBrainzAsync(payload, execution, cancellationToken);
            await CompleteAsync(payload, execution.Claim.JobId, cancellationToken);
            artifacts.Delete(payload.ImportId);
            await execution.ReportProgressAsync(new(
                "history-import.complete",
                "Listening history import completed.",
                Completed: (int)Math.Min(int.MaxValue, preview.MusicRows),
                Total: (int)Math.Min(int.MaxValue, preview.MusicRows)), cancellationToken);
            return DurableJobCompletion.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CancelAsync(payload, execution.Claim.JobId);
            artifacts.Delete(payload.ImportId);
            return DurableJobCompletion.Cancelled();
        }
        catch (ListeningHistoryImportException exception)
        {
            await FailAsync(payload, execution.Claim.JobId, exception.Code, CancellationToken.None);
            return DurableJobCompletion.Failure(exception.Code, exception.Message);
        }
        catch (Exception)
        {
            if (await IsLastAttemptAsync(execution.Claim.JobId, execution.Claim.AttemptNumber))
                await FailAsync(payload, execution.Claim.JobId, "history_import_temporary_failure", CancellationToken.None);
            return DurableJobCompletion.Retry(
                "history_import_temporary_failure",
                "Listening history import will retry after a temporary failure.");
        }
    }

    private async Task<bool> IsLastAttemptAsync(Guid jobId, int attemptNumber)
    {
        await using var db = await factory.CreateDbContextAsync(CancellationToken.None);
        var maxAttempts = await db.Jobs.AsNoTracking().Where(item => item.Id == jobId)
            .Select(item => item.MaxAttempts).SingleAsync(CancellationToken.None);
        return attemptNumber >= maxAttempts;
    }

    private async Task QueueMusicBrainzAsync(
        ListeningHistoryImportJobPayload payload,
        DurableJobExecutionContext execution,
        CancellationToken cancellationToken)
    {
        if (!musicBrainz.Enabled) return;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var provenance = $"history-import:{payload.ImportId:N}:";
        var candidates = await db.ListeningEvents.AsNoTracking().Where(item =>
                item.TenantId == payload.Scope.TenantId && item.OwnerUserId == payload.Scope.OwnerUserId &&
                item.Protocol == payload.Scope.Protocol && item.BackendInstanceId == payload.Scope.BackendInstanceId &&
                item.LibraryScopeId == payload.Scope.LibraryScopeId && item.State == ListeningEventState.Completed &&
                item.SourceKind == "import" && item.ImportProvenance != null &&
                item.ImportProvenance.StartsWith(provenance) &&
                item.MusicBrainzEnrichmentState == MusicBrainzEnrichmentState.Pending)
            .GroupBy(item => item.TrackReference)
            .Select(group => new { TrackReference = group.Key, OccurrenceKey = group.Min(item => item.OccurrenceKey) })
            .ToListAsync(cancellationToken);
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            await musicBrainz.EnqueueImportedTrackAsync(
                payload.Scope,
                candidate.TrackReference,
                candidate.OccurrenceKey!,
                $"history-import:{payload.ImportId:N}",
                cancellationToken);
            if ((index + 1) % 100 == 0 || index + 1 == candidates.Count)
                await execution.ReportProgressAsync(new(
                    "history-import.enrichment",
                    "Queued unique tracks for metadata enrichment.",
                    index + 1,
                    candidates.Count), cancellationToken);
        }
    }

    private async Task CompleteAsync(
        ListeningHistoryImportJobPayload payload,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var record = await Query(db, payload, jobId).SingleAsync(cancellationToken);
        var now = clock.UtcNow;
        record.State = ListeningHistoryImportState.Completed;
        record.CompletedAt = now;
        record.UpdatedAt = now;
        record.Revision++;
        db.AuditEvents.Add(Audit(record, "completed", "success", now));
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task CancelAsync(ListeningHistoryImportJobPayload payload, Guid jobId)
    {
        await using var db = await factory.CreateDbContextAsync(CancellationToken.None);
        var record = await Query(db, payload, jobId).SingleOrDefaultAsync(CancellationToken.None);
        if (record == null || record.State == ListeningHistoryImportState.Completed) return;
        var now = clock.UtcNow;
        record.State = ListeningHistoryImportState.Cancelled;
        record.CompletedAt = now;
        record.UpdatedAt = now;
        record.Revision++;
        db.AuditEvents.Add(Audit(record, "cancelled", "success", now));
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private async Task FailAsync(
        ListeningHistoryImportJobPayload payload,
        Guid jobId,
        string code,
        CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var record = await Query(db, payload, jobId).SingleOrDefaultAsync(cancellationToken);
        if (record == null || record.State is ListeningHistoryImportState.Completed or ListeningHistoryImportState.Cancelled) return;
        var now = clock.UtcNow;
        record.State = ListeningHistoryImportState.Failed;
        record.CompletedAt = now;
        record.UpdatedAt = now;
        record.Revision++;
        db.AuditEvents.Add(Audit(record, "failed", code, now));
        await db.SaveChangesAsync(cancellationToken);
    }

    private static IQueryable<ListeningHistoryImportRecord> Query(
        AllstarrDbContext db,
        ListeningHistoryImportJobPayload payload,
        Guid jobId) =>
        db.ListeningHistoryImports.Where(item => item.Id == payload.ImportId &&
            item.TenantId == payload.Scope.TenantId && item.OwnerUserId == payload.Scope.OwnerUserId &&
            item.Protocol == payload.Scope.Protocol && item.BackendInstanceId == payload.Scope.BackendInstanceId &&
            item.LibraryScopeId == payload.Scope.LibraryScopeId && item.JobId == jobId &&
            item.PreviewRevision == payload.PreviewRevision && item.ApplyGeneration == payload.Generation);

    internal static ListeningEventRecord CreateEvent(
        ListeningHistoryImportJobPayload payload,
        ListeningHistoryImportRow row,
        string occurrenceKey,
        ProviderTrackIdentityRecord? identity,
        CanonicalRecordingRecord? canonical,
        LibraryTrackRecord? libraryTrack,
        bool enrichWithMusicBrainz)
    {
        var completed = row.Classification == ListeningHistoryImportClassification.Completed;
        return new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = payload.Scope.TenantId,
            OwnerUserId = payload.Scope.OwnerUserId,
            Protocol = payload.Scope.Protocol,
            BackendInstanceId = payload.Scope.BackendInstanceId,
            LibraryScopeId = payload.Scope.LibraryScopeId,
            OccurrenceKey = occurrenceKey,
            State = row.Classification switch
            {
                ListeningHistoryImportClassification.Completed => ListeningEventState.Completed,
                ListeningHistoryImportClassification.Skipped => ListeningEventState.Skipped,
                _ => ListeningEventState.Abandoned
            },
            StartedAt = row.StartedAt,
            ListenedAt = completed ? row.ListenedAt : null,
            UpdatedAt = row.ListenedAt,
            PositionTicks = row.MillisecondsPlayed * TimeSpan.TicksPerMillisecond,
            DurationMilliseconds = libraryTrack?.DurationMilliseconds ?? row.DurationMilliseconds,
            ClientClass = row.Client,
            SourceKind = "import",
            ImportProvenance = $"history-import:{payload.ImportId:N}:{row.SourceService}:{row.Sequence}:{row.ReasonCode}:{(row.Offline ? "offline" : "online")}:{(row.PrivateSession ? "private" : "standard")}",
            TrackReference = libraryTrack == null ? $"{row.SourceService}:{row.SourceItemKey}" : $"library:{libraryTrack.Id:N}",
            Title = row.Title,
            Artist = row.Artist,
            Album = row.Album,
            RecordingMusicBrainzId = libraryTrack?.MusicBrainzRecordingId ?? canonical?.MusicBrainzRecordingId ?? row.RecordingMusicBrainzId,
            Isrc = libraryTrack?.Isrc ?? canonical?.Isrc,
            MusicBrainzEnrichmentState = enrichWithMusicBrainz && completed && canonical == null
                ? MusicBrainzEnrichmentState.Pending
                : MusicBrainzEnrichmentState.NotRequested,
            CanonicalRecordingId = canonical?.Id,
            LibraryTrackId = libraryTrack?.Id,
            ProviderId = row.SourceService,
            ProviderAccountId = identity?.ProviderAccountId,
            ProviderTrackIdentityId = identity?.Id,
            ProviderTrackReference = row.ProviderTrackReference,
            Revision = 1
        };
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
                record.UnresolvedRows
            }),
            CreatedAt = now
        };

    private sealed class ApplyAccumulator(
        IDbContextFactory<AllstarrDbContext> factory,
        ListeningHistoryImportJobPayload payload,
        Guid jobId,
        IPlatformClock clock,
        bool enrichWithMusicBrainz)
    {
        private readonly List<ListeningHistoryImportRow> _rows = new(500);
        private long _nextSequence = -1;

        public async ValueTask AddAsync(ListeningHistoryImportRow row, CancellationToken cancellationToken)
        {
            if (_nextSequence < 0)
            {
                await using var db = await factory.CreateDbContextAsync(cancellationToken);
                _nextSequence = await Query(db, payload, jobId)
                    .Select(item => item.NextSequence)
                    .SingleAsync(cancellationToken);
            }
            if (row.Sequence <= _nextSequence) return;
            _rows.Add(row);
            if (_rows.Count == 500) await FlushAsync(cancellationToken);
        }

        public async Task FlushAsync(CancellationToken cancellationToken)
        {
            if (_rows.Count == 0) return;
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            var import = await Query(db, payload, jobId).SingleAsync(cancellationToken);
            if (import.State == ListeningHistoryImportState.Cancelled)
                throw new OperationCanceledException(cancellationToken);

            var occurrenceKeys = _rows.Select(row => ListeningHistoryImportService.OccurrenceKey(payload.Scope, row)).ToArray();
            var existing = await db.ListeningEvents.AsNoTracking().Where(item =>
                    item.TenantId == payload.Scope.TenantId && item.OwnerUserId == payload.Scope.OwnerUserId &&
                    occurrenceKeys.Contains(item.OccurrenceKey))
                .Select(item => item.OccurrenceKey).ToHashSetAsync(cancellationToken);
            var externalHashes = _rows.Select(ListeningHistoryImportService.ProviderIdentityHash)
                .OfType<string>().Distinct().ToArray();
            var identities = externalHashes.Length == 0
                ? []
                : await db.ProviderTrackIdentities.AsNoTracking().Where(item =>
                        item.TenantId == payload.Scope.TenantId && item.ProviderId == "spotify" &&
                        item.ResourceKind == ProviderResourceKind.Track && item.Scope == ProviderIdentityScope.Catalog &&
                        externalHashes.Contains(item.ExternalIdHash))
                    .OrderBy(item => item.CatalogNamespace).ThenBy(item => item.Id)
                    .ToListAsync(cancellationToken);
            var identityByHash = identities.GroupBy(item => item.ExternalIdHash)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var canonicalIds = identities.Select(item => item.CanonicalRecordingId).Distinct().ToArray();
            var recordingMbids = _rows.Select(item => item.RecordingMusicBrainzId).OfType<string>().Distinct().ToArray();
            var canonicals = canonicalIds.Length == 0 && recordingMbids.Length == 0
                ? []
                : await db.CanonicalRecordings.AsNoTracking().Where(item =>
                        item.TenantId == payload.Scope.TenantId &&
                        (canonicalIds.Contains(item.Id) ||
                         item.MusicBrainzRecordingId != null && recordingMbids.Contains(item.MusicBrainzRecordingId)))
                    .ToListAsync(cancellationToken);
            var canonicalById = canonicals.ToDictionary(item => item.Id);
            var canonicalByMbid = canonicals.Where(item => item.MusicBrainzRecordingId != null)
                .GroupBy(item => item.MusicBrainzRecordingId!)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var resolvedCanonicalIds = canonicals.Select(item => item.Id).ToArray();
            var libraryTracks = resolvedCanonicalIds.Length == 0
                ? []
                : await db.LibraryTracks.AsNoTracking().Where(item =>
                        item.TenantId == payload.Scope.TenantId && item.OwnerUserId == payload.Scope.OwnerUserId &&
                        item.Protocol == payload.Scope.Protocol && item.BackendInstanceId == payload.Scope.BackendInstanceId &&
                        item.LibraryScopeId == payload.Scope.LibraryScopeId &&
                        item.CanonicalRecordingId != null && resolvedCanonicalIds.Contains(item.CanonicalRecordingId.Value))
                    .OrderBy(item => item.Id).ToListAsync(cancellationToken);
            var libraryByCanonical = libraryTracks.GroupBy(item => item.CanonicalRecordingId!.Value)
                .ToDictionary(group => group.Key, group => group.First());

            long imported = 0, duplicates = 0, resolved = 0, unresolved = 0;
            foreach (var row in _rows)
            {
                var occurrenceKey = ListeningHistoryImportService.OccurrenceKey(payload.Scope, row);
                if (existing.Contains(occurrenceKey))
                {
                    duplicates++;
                    continue;
                }
                ProviderTrackIdentityRecord? identity = null;
                var externalHash = ListeningHistoryImportService.ProviderIdentityHash(row);
                if (externalHash != null)
                    identityByHash.TryGetValue(externalHash, out identity);
                LibraryTrackRecord? libraryTrack = null;
                CanonicalRecordingRecord? canonical = null;
                if (identity != null)
                {
                    canonicalById.TryGetValue(identity.CanonicalRecordingId, out canonical);
                }
                else if (row.RecordingMusicBrainzId != null)
                {
                    canonicalByMbid.TryGetValue(row.RecordingMusicBrainzId, out canonical);
                }
                if (canonical != null) libraryByCanonical.TryGetValue(canonical.Id, out libraryTrack);
                db.ListeningEvents.Add(ListeningHistoryImportJobHandler.CreateEvent(
                    payload, row, occurrenceKey, identity, canonical, libraryTrack, enrichWithMusicBrainz));
                imported++;
                if (canonical == null) unresolved++; else resolved++;
            }
            _nextSequence = _rows.Max(row => row.Sequence);
            import.NextSequence = _nextSequence;
            import.ImportedRows += imported;
            import.DuplicateRows += duplicates;
            import.ResolvedRows += resolved;
            import.UnresolvedRows += unresolved;
            import.UpdatedAt = clock.UtcNow;
            import.Revision++;
            await db.SaveChangesAsync(cancellationToken);
            _rows.Clear();
        }

    }
}

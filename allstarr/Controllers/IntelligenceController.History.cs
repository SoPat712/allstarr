using System.Globalization;
using System.Text.Json;
using allstarr.Core.Intelligence;
using allstarr.Core.Playback;
using allstarr.Core.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Controllers;

public sealed partial class IntelligenceController
{
    private const int MaximumHistoryPageSize = 100;
    private const long MaximumHistoryImportUploadBytes = 64L * 1024 * 1024;

    [HttpGet("history/overview")]
    public async Task<IActionResult> GetHistoryOverview(
        [FromQuery] IntelligenceHistoryPeriodRequest request,
        CancellationToken cancellationToken)
    {
        if (!TrySessionScope(request, out var scope, out var error)) return error!;
        if (!ListeningHistoryPeriod.TryCreate(request.From, request.To, Now, out var period) ||
            !ValidTimeZone(request.TimeZoneId))
            return BadRequest(new { error = "listening_history_period_invalid" });
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        if (!await OwnsBackend(db, scope, cancellationToken)) return NotFound();

        var completed = ScopedCompletedHistory(db, scope);
        var allTime = await StatsAsync(completed, cancellationToken);
        var selected = await StatsAsync(completed.Where(item =>
            item.ListenedAt >= period.From && item.ListenedAt < period.To), cancellationToken);
        var recentRecords = await completed.Where(item => item.ListenedAt >= period.From && item.ListenedAt < period.To)
            .OrderByDescending(item => item.ListenedAt).ThenByDescending(item => item.Id)
            .Take(10).ToListAsync(cancellationToken);
        var nowPlaying = await ScopedHistory(db, scope).Where(item =>
                item.State == ListeningEventState.Playing && item.UpdatedAt >= Now.AddHours(-8))
            .OrderByDescending(item => item.UpdatedAt).FirstOrDefaultAsync(cancellationToken);
        var activity = await ActivityAsync(db, scope, period, request.TimeZoneId, cancellationToken);
        var recent = await HistoryItemsAsync(db, scope, recentRecords, cancellationToken);

        return Ok(new
        {
            period = PublicPeriod(period, request.TimeZoneId),
            allTime,
            selected,
            currentStreakDays = activity.CurrentStreakDays,
            longestStreakDays = activity.LongestStreakDays,
            nowPlaying = nowPlaying == null ? null : HistoryItem(nowPlaying, null, []),
            recent
        });
    }

    [HttpGet("history")]
    public async Task<IActionResult> GetHistory(
        [FromQuery] IntelligenceHistoryRequest request,
        CancellationToken cancellationToken)
    {
        if (!TrySessionScope(request, out var scope, out var error)) return error!;
        if (!ListeningHistoryPeriod.TryCreate(request.From, request.To, Now, out var period) ||
            !ValidTimeZone(request.TimeZoneId))
            return BadRequest(new { error = "listening_history_period_invalid" });
        if (!ValidHistoryFilters(request))
            return BadRequest(new { error = "listening_history_filter_invalid" });
        if (request.Cursor != null && !ListeningHistoryCursor.TryParse(request.Cursor, out _))
            return BadRequest(new { error = "listening_history_cursor_invalid" });

        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        if (!await OwnsBackend(db, scope, cancellationToken)) return NotFound();
        var limit = Math.Clamp(request.Limit, 1, MaximumHistoryPageSize);
        var query = ScopedCompletedHistory(db, scope).Where(item =>
            item.ListenedAt >= period.From && item.ListenedAt < period.To);
        query = ApplyHistoryFilters(query, request);
        if (request.Cursor != null && ListeningHistoryCursor.TryParse(request.Cursor, out var cursor))
        {
            query = query.Where(item => item.ListenedAt < cursor.ListenedAt ||
                item.ListenedAt == cursor.ListenedAt && item.Id.CompareTo(cursor.Id) < 0);
        }

        var records = await query.OrderByDescending(item => item.ListenedAt).ThenByDescending(item => item.Id)
            .Take(limit + 1).ToListAsync(cancellationToken);
        var hasMore = records.Count > limit;
        if (hasMore) records.RemoveAt(records.Count - 1);
        var items = await HistoryItemsAsync(db, scope, records, cancellationToken);
        var last = records.LastOrDefault();
        return Ok(new
        {
            period = PublicPeriod(period, request.TimeZoneId),
            items,
            nextCursor = hasMore && last?.ListenedAt is { } listenedAt
                ? new ListeningHistoryCursor(listenedAt, last.Id).ToString()
                : null
        });
    }

    [HttpGet("history/activity")]
    public async Task<IActionResult> GetHistoryActivity(
        [FromQuery] IntelligenceHistoryPeriodRequest request,
        CancellationToken cancellationToken)
    {
        if (!TrySessionScope(request, out var scope, out var error)) return error!;
        if (!ListeningHistoryPeriod.TryCreate(request.From, request.To, Now, out var period) ||
            !ValidTimeZone(request.TimeZoneId))
            return BadRequest(new { error = "listening_history_period_invalid" });
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        if (!await OwnsBackend(db, scope, cancellationToken)) return NotFound();
        var activity = await ActivityAsync(db, scope, period, request.TimeZoneId, cancellationToken);
        return Ok(new
        {
            period = PublicPeriod(period, request.TimeZoneId),
            activity.CurrentStreakDays,
            activity.LongestStreakDays,
            buckets = activity.Buckets
        });
    }

    [HttpGet("history/top/{kind}")]
    public async Task<IActionResult> GetHistoryTopItems(
        string kind,
        [FromQuery] IntelligenceHistoryTopRequest request,
        CancellationToken cancellationToken)
    {
        if (!TrySessionScope(request, out var scope, out var error)) return error!;
        if (!ListeningHistoryPeriod.TryCreate(request.From, request.To, Now, out var period) ||
            !ValidTimeZone(request.TimeZoneId))
            return BadRequest(new { error = "listening_history_period_invalid" });
        kind = kind.Trim().ToLowerInvariant();
        if (kind is not ("artist" or "album" or "track"))
            return BadRequest(new { error = "listening_history_top_kind_invalid" });
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        if (!await OwnsBackend(db, scope, cancellationToken)) return NotFound();
        var query = ScopedCompletedHistory(db, scope).Where(item =>
            item.ListenedAt >= period.From && item.ListenedAt < period.To);
        var items = await TopItemsAsync(query, kind, Math.Clamp(request.Limit, 1, 50), cancellationToken);
        return Ok(new { period = PublicPeriod(period, request.TimeZoneId), kind, items });
    }

    [HttpGet("history/{eventId:guid}")]
    public async Task<IActionResult> GetHistoryDetail(
        Guid eventId,
        [FromQuery] IntelligenceScopeRequest request,
        CancellationToken cancellationToken)
    {
        if (!TrySessionScope(request, out var scope, out var error)) return error!;
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        if (!await OwnsBackend(db, scope, cancellationToken)) return NotFound();
        var record = await ScopedHistory(db, scope).SingleOrDefaultAsync(item => item.Id == eventId, cancellationToken);
        if (record == null) return NotFound();
        var item = (await HistoryItemsAsync(db, scope, [record], cancellationToken)).Single();
        return Ok(new
        {
            item,
            identity = new
            {
                recordingMusicBrainzId = record.RecordingMusicBrainzId,
                record.Isrc,
                record.AlbumArtist,
                record.TrackNumber,
                record.MusicBrainzEnrichmentConfidence,
                record.MusicBrainzSourceRevision,
                record.MusicBrainzEnrichedAt,
                musicBrainzFacts = ParseObject(record.MusicBrainzFactsJson)
            },
            provenance = new
            {
                source = record.SourceKind,
                client = record.ClientClass,
                device = record.DeviceClass,
                provider = record.ProviderId,
                imported = record.ImportProvenance != null
            }
        });
    }

    [HttpGet("history/{eventId:guid}/targets")]
    public async Task<IActionResult> GetHistoryTargetStatus(
        Guid eventId,
        [FromQuery] IntelligenceScopeRequest request,
        CancellationToken cancellationToken)
    {
        if (!TrySessionScope(request, out var scope, out var error)) return error!;
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        if (!await OwnsBackend(db, scope, cancellationToken)) return NotFound();
        var occurrenceKey = await ScopedHistory(db, scope).Where(item => item.Id == eventId)
            .Select(item => item.OccurrenceKey).SingleOrDefaultAsync(cancellationToken);
        if (occurrenceKey == null) return NotFound();
        var statuses = await TargetStatusesAsync(db, scope, [occurrenceKey], cancellationToken);
        return Ok(new { eventId, targets = statuses.GetValueOrDefault(occurrenceKey, []) });
    }

    [HttpPut("history/{eventId:guid}")]
    public async Task<IActionResult> CorrectHistory(
        Guid eventId,
        [FromBody] IntelligenceHistoryCorrectionRequest request,
        CancellationToken cancellationToken)
    {
        if (!TrySessionScope(request, out var scope, out var error)) return error!;
        if (!TryCleanRequired(request.Title, out var title) ||
            !TryCleanRequired(request.Artist, out var artist) ||
            !TryCleanOptional(request.Album, out var album) ||
            !TryCleanOptional(request.AlbumArtist, out var albumArtist))
            return BadRequest(new { error = "listening_history_correction_invalid" });
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        if (!await OwnsBackend(db, scope, cancellationToken)) return NotFound();
        var record = await ScopedHistory(db, scope).SingleOrDefaultAsync(item => item.Id == eventId, cancellationToken);
        if (record == null) return NotFound();
        if (record.Revision != request.ExpectedRevision)
            return Conflict(new { error = "listening_history_revision_conflict" });

        var changed = new List<string>(4);
        if (record.Title != title) { record.Title = title; changed.Add("title"); }
        if (record.Artist != artist) { record.Artist = artist; changed.Add("artist"); }
        if (record.Album != album) { record.Album = album; changed.Add("album"); }
        if (record.AlbumArtist != albumArtist) { record.AlbumArtist = albumArtist; changed.Add("albumArtist"); }
        if (changed.Count == 0) return Ok(new { record.Id, record.Revision });
        record.Revision++;
        record.UpdatedAt = Now;
        AddHistoryAudit(db, scope, "corrected", record.Id, record.Revision, changed);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return Ok(new { record.Id, record.Revision });
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { error = "listening_history_revision_conflict" });
        }
    }

    [HttpDelete("history/{eventId:guid}")]
    public async Task<IActionResult> DeleteHistory(
        Guid eventId,
        [FromBody] IntelligenceHistoryDeleteRequest request,
        CancellationToken cancellationToken)
    {
        if (!TrySessionScope(request, out var scope, out var error)) return error!;
        if (!request.Confirmed) return BadRequest(new { error = "listening_history_confirmation_required" });
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        if (!await OwnsBackend(db, scope, cancellationToken)) return NotFound();
        var record = await ScopedHistory(db, scope).SingleOrDefaultAsync(item => item.Id == eventId, cancellationToken);
        if (record == null) return NotFound();
        if (record.Revision != request.ExpectedRevision)
            return Conflict(new { error = "listening_history_revision_conflict" });
        var checkpoints = await db.Set<PlaybackDeliveryCheckpointEntity>().Where(item =>
            item.TenantId == scope.TenantId && item.OwnerUserId == scope.OwnerUserId &&
            item.OccurrenceKey == record.OccurrenceKey).ToListAsync(cancellationToken);
        db.RemoveRange(checkpoints);
        db.ListeningEvents.Remove(record);
        AddHistoryAudit(db, scope, "deleted", record.Id, record.Revision, []);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return NoContent();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { error = "listening_history_revision_conflict" });
        }
    }

    [HttpGet("history/export")]
    public async Task<IActionResult> ExportHistory(
        [FromQuery] IntelligenceScopeRequest request,
        CancellationToken cancellationToken)
    {
        if (!TrySessionScope(request, out var scope, out var error)) return error!;
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        if (!await OwnsBackend(db, scope, cancellationToken)) return NotFound();
        return new ListeningHistoryExportResult(_factory, scope, Now);
    }

    [HttpPost("history/imports/preview")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaximumHistoryImportUploadBytes + 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaximumHistoryImportUploadBytes + 1024 * 1024)]
    public async Task<IActionResult> PreviewHistoryImport(
        [FromForm] IntelligenceHistoryImportPreviewRequest request,
        [FromServices] ListeningHistoryImportService imports,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
        if (!TrySessionScope(request, out var scope, out var error)) return error!;
        if (request.File == null || request.File.Length is < 1 or > MaximumHistoryImportUploadBytes)
            return BadRequest(new { error = "history_import_file_invalid", message = "Choose a history file up to 64 MB." });
        var contentType = request.File.ContentType?.Trim().ToLowerInvariant();
        if (contentType is not (null or "" or "application/json" or "text/json" or "text/plain" or
            "application/jsonl" or "application/ndjson" or "application/x-ndjson" or
            "application/zip" or "application/x-zip" or "application/x-zip-compressed" or
            "application/octet-stream"))
            return BadRequest(new { error = "history_import_content_type_invalid", message = "Choose a supported history export file." });
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        if (!await OwnsBackend(db, scope, cancellationToken)) return NotFound();
        try
        {
            await using var source = request.File.OpenReadStream();
            var result = await imports.PreviewAsync(
                scope,
                request.File.FileName,
                source,
                request.File.Length,
                cancellationToken);
            return Ok(new
            {
                scope = PublicScope(scope),
                result.ImportId,
                result.Revision,
                result.DisplayFileName,
                result.SizeBytes,
                result.ExpiresAt,
                state = result.State.ToString().ToLowerInvariant(),
                outboundReplay = false,
                preview = result.Preview
            });
        }
        catch (ListeningHistoryImportException exception)
        {
            return BadRequest(new { error = exception.Code, message = exception.Message });
        }
        catch (InvalidDataException exception)
        {
            return BadRequest(new { error = "history_import_file_invalid", message = exception.Message });
        }
    }

    [HttpGet("history/imports/{importId:guid}")]
    public async Task<IActionResult> GetHistoryImport(
        Guid importId,
        [FromQuery] IntelligenceScopeRequest request,
        [FromServices] ListeningHistoryImportService imports,
        CancellationToken cancellationToken)
    {
        if (!TrySessionScope(request, out var scope, out var error)) return error!;
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        if (!await OwnsBackend(db, scope, cancellationToken)) return NotFound();
        var result = await imports.GetAsync(scope, importId, cancellationToken);
        return result == null ? NotFound() : Ok(new
        {
            scope = PublicScope(scope),
            result.ImportId,
            result.Revision,
            result.DisplayFileName,
            result.SizeBytes,
            result.ExpiresAt,
            state = result.State.ToString().ToLowerInvariant(),
            result.JobId,
            result.JobState,
            result.LastErrorCode,
            result.LastErrorMessage,
            result.ImportedRows,
            result.DuplicateRows,
            result.ResolvedRows,
            result.UnresolvedRows,
            outboundReplay = false,
            preview = result.Preview
        });
    }

    [HttpPost("history/imports/{importId:guid}/apply")]
    public Task<IActionResult> ApplyHistoryImport(
        Guid importId,
        [FromBody] IntelligenceHistoryImportCommandRequest request,
        [FromServices] ListeningHistoryImportService imports,
        CancellationToken cancellationToken) =>
        ChangeHistoryImport(importId, request, imports, "apply", cancellationToken);

    [HttpPost("history/imports/{importId:guid}/resume")]
    public Task<IActionResult> ResumeHistoryImport(
        Guid importId,
        [FromBody] IntelligenceHistoryImportCommandRequest request,
        [FromServices] ListeningHistoryImportService imports,
        CancellationToken cancellationToken) =>
        ChangeHistoryImport(importId, request, imports, "resume", cancellationToken);

    [HttpPost("history/imports/{importId:guid}/cancel")]
    public Task<IActionResult> CancelHistoryImport(
        Guid importId,
        [FromBody] IntelligenceHistoryImportCommandRequest request,
        [FromServices] ListeningHistoryImportService imports,
        CancellationToken cancellationToken) =>
        ChangeHistoryImport(importId, request, imports, "cancel", cancellationToken);

    private async Task<IActionResult> ChangeHistoryImport(
        Guid importId,
        IntelligenceHistoryImportCommandRequest request,
        ListeningHistoryImportService imports,
        string operation,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
        if (!TrySessionScope(request, out var scope, out var error)) return error!;
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        if (!await OwnsBackend(db, scope, cancellationToken)) return NotFound();
        try
        {
            var result = operation switch
            {
                "apply" => await imports.ApplyAsync(scope, importId, request.Revision, cancellationToken),
                "resume" => await imports.ResumeAsync(scope, importId, request.Revision, cancellationToken),
                _ => await imports.CancelAsync(scope, importId, request.Revision, cancellationToken)
            };
            if (result == null) return NotFound();
            var response = new
            {
                scope = PublicScope(scope),
                result.ImportId,
                result.Revision,
                state = result.State.ToString().ToLowerInvariant(),
                result.JobId,
                result.JobState,
                result.LastErrorCode,
                result.LastErrorMessage,
                result.ImportedRows,
                result.DuplicateRows,
                result.ResolvedRows,
                result.UnresolvedRows,
                outboundReplay = false
            };
            return operation == "cancel" ? Ok(response) : Accepted(response);
        }
        catch (ListeningHistoryImportException exception) when (exception.Code.EndsWith("_conflict", StringComparison.Ordinal))
        {
            return Conflict(new { error = exception.Code, message = exception.Message });
        }
        catch (ListeningHistoryImportException exception) when (exception.Code == "history_import_expired")
        {
            return StatusCode(StatusCodes.Status410Gone, new { error = exception.Code, message = exception.Message });
        }
        catch (ListeningHistoryImportException exception)
        {
            return BadRequest(new { error = exception.Code, message = exception.Message });
        }
        catch (InvalidDataException exception)
        {
            return BadRequest(new { error = "history_import_file_invalid", message = exception.Message });
        }
    }

    private DateTimeOffset Now => _clock?.UtcNow ?? DateTimeOffset.UtcNow;

    private static IQueryable<ListeningEventRecord> ScopedHistory(AllstarrDbContext db, IntelligenceScope scope) =>
        db.ListeningEvents.Where(item => item.TenantId == scope.TenantId &&
            item.OwnerUserId == scope.OwnerUserId && item.Protocol == scope.Protocol &&
            item.BackendInstanceId == scope.BackendInstanceId && item.LibraryScopeId == scope.LibraryScopeId);

    private static IQueryable<ListeningEventRecord> ScopedCompletedHistory(AllstarrDbContext db, IntelligenceScope scope) =>
        ScopedHistory(db, scope).AsNoTracking().Where(item =>
            item.State == ListeningEventState.Completed && item.ListenedAt != null);

    private static async Task<ListeningHistoryStats> StatsAsync(
        IQueryable<ListeningEventRecord> query,
        CancellationToken cancellationToken) =>
        await query.GroupBy(_ => 1).Select(group => new ListeningHistoryStats
        {
            CompletedListens = group.Count(),
            DistinctTracks = group.Select(item => item.TrackReference).Distinct().Count(),
            DistinctArtists = group.Where(item => item.Artist != null)
                .Select(item => item.Artist).Distinct().Count(),
            ListeningTimeMilliseconds = group.Sum(item => (long?)item.DurationMilliseconds) ?? 0,
            FirstListen = group.Min(item => item.ListenedAt)
        }).SingleOrDefaultAsync(cancellationToken) ?? new();

    private static IQueryable<ListeningEventRecord> ApplyHistoryFilters(
        IQueryable<ListeningEventRecord> query,
        IntelligenceHistoryRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Source))
        {
            var source = request.Source.Trim().ToLowerInvariant();
            query = query.Where(item => item.SourceKind == source);
        }
        if (!string.IsNullOrWhiteSpace(request.Client))
        {
            var client = request.Client.Trim();
            query = query.Where(item => item.ClientClass == client);
        }
        if (!string.IsNullOrWhiteSpace(request.Artist))
            query = query.Where(item => item.Artist != null && EF.Functions.ILike(item.Artist, LiteralPattern(request.Artist), "\\"));
        if (!string.IsNullOrWhiteSpace(request.Album))
            query = query.Where(item => item.Album != null && EF.Functions.ILike(item.Album, LiteralPattern(request.Album), "\\"));
        if (!string.IsNullOrWhiteSpace(request.Track))
            query = query.Where(item => item.Title != null && EF.Functions.ILike(item.Title, LiteralPattern(request.Track), "\\"));
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var pattern = LiteralPattern(request.Search);
            query = query.Where(item =>
                item.Title != null && EF.Functions.ILike(item.Title, pattern, "\\") ||
                item.Artist != null && EF.Functions.ILike(item.Artist, pattern, "\\") ||
                item.Album != null && EF.Functions.ILike(item.Album, pattern, "\\"));
        }
        return query;
    }

    private static string LiteralPattern(string value) => $"%{value.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal)}%";

    private static bool ValidHistoryFilters(IntelligenceHistoryRequest request) =>
        ValidFilter(request.Source, 32) && ValidFilter(request.Client, 200) &&
        ValidFilter(request.Artist, 500) && ValidFilter(request.Album, 500) &&
        ValidFilter(request.Track, 500) && ValidFilter(request.Search, 200);

    private static bool ValidFilter(string? value, int maximum) =>
        value == null || value.Trim().Length <= maximum && !value.Any(char.IsControl);

    private static bool ValidTimeZone(string value)
    {
        if (!ValidFilter(value, 100) || string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(value);
            return true;
        }
        catch (TimeZoneNotFoundException) { return false; }
        catch (InvalidTimeZoneException) { return false; }
    }

    private static async Task<ListeningHistoryActivity> ActivityAsync(
        AllstarrDbContext db,
        IntelligenceScope scope,
        ListeningHistoryPeriod period,
        string timeZoneId,
        CancellationToken cancellationToken)
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        var from = period.From.ToUnixTimeMilliseconds();
        var to = period.To.ToUnixTimeMilliseconds();
        var buckets = await db.Database.SqlQuery<ListeningHistoryActivityBucket>($$"""
            SELECT to_char(timezone({{timeZoneId}}, to_timestamp("ListenedAt" / 1000.0)), 'YYYY-MM-DD') AS "Date",
                   count(*)::integer AS "Count",
                   coalesce(sum("DurationMilliseconds"), 0)::bigint AS "DurationMilliseconds"
            FROM listening_events
            WHERE "TenantId" = {{scope.TenantId}} AND "OwnerUserId" = {{scope.OwnerUserId}}
              AND "Protocol" = {{scope.Protocol}} AND "BackendInstanceId" = {{scope.BackendInstanceId}}
              AND "LibraryScopeId" = {{scope.LibraryScopeId}} AND "State" = 'Completed'
              AND "ListenedAt" >= {{from}} AND "ListenedAt" < {{to}}
            GROUP BY 1
            ORDER BY 1
            """).ToListAsync(cancellationToken);
        var dates = buckets.Select(item => DateOnly.ParseExact(item.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture)).ToArray();
        var (current, longest) = ListeningHistoryStreaks.Calculate(
            dates,
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(period.To.AddMilliseconds(-1), timeZone).Date));
        return new(buckets, current, longest);
    }

    private static async Task<IReadOnlyList<ListeningHistoryTopItem>> TopItemsAsync(
        IQueryable<ListeningEventRecord> query,
        string kind,
        int limit,
        CancellationToken cancellationToken) => kind switch
        {
            "artist" => await query.Where(item => item.Artist != null).GroupBy(item => item.Artist)
                .Select(group => new ListeningHistoryTopItem
                {
                    Artist = group.Key,
                    ListenCount = group.Count(),
                    ListeningTimeMilliseconds = group.Sum(item => (long?)item.DurationMilliseconds) ?? 0,
                    LastListenedAt = group.Max(item => item.ListenedAt)
                }).OrderByDescending(item => item.ListenCount).ThenByDescending(item => item.LastListenedAt)
                .ThenBy(item => item.Artist).Take(limit).ToListAsync(cancellationToken),
            "album" => await query.Where(item => item.Album != null).GroupBy(item => new { item.Album, item.AlbumArtist, item.Artist })
                .Select(group => new ListeningHistoryTopItem
                {
                    Album = group.Key.Album,
                    Artist = group.Key.AlbumArtist ?? group.Key.Artist,
                    ListenCount = group.Count(),
                    ListeningTimeMilliseconds = group.Sum(item => (long?)item.DurationMilliseconds) ?? 0,
                    LastListenedAt = group.Max(item => item.ListenedAt)
                }).OrderByDescending(item => item.ListenCount).ThenByDescending(item => item.LastListenedAt)
                .ThenBy(item => item.Artist).ThenBy(item => item.Album).Take(limit).ToListAsync(cancellationToken),
            _ => await query.Where(item => item.Title != null).GroupBy(item => new { item.Title, item.Artist, item.Album })
                .Select(group => new ListeningHistoryTopItem
                {
                    Title = group.Key.Title,
                    Artist = group.Key.Artist,
                    Album = group.Key.Album,
                    ListenCount = group.Count(),
                    ListeningTimeMilliseconds = group.Sum(item => (long?)item.DurationMilliseconds) ?? 0,
                    LastListenedAt = group.Max(item => item.ListenedAt)
                }).OrderByDescending(item => item.ListenCount).ThenByDescending(item => item.LastListenedAt)
                .ThenBy(item => item.Artist).ThenBy(item => item.Title).Take(limit).ToListAsync(cancellationToken)
        };

    private static async Task<IReadOnlyList<ListeningHistoryItem>> HistoryItemsAsync(
        AllstarrDbContext db,
        IntelligenceScope scope,
        IReadOnlyCollection<ListeningEventRecord> records,
        CancellationToken cancellationToken)
    {
        if (records.Count == 0) return [];
        var trackIds = records.Select(item => item.LibraryTrackId).OfType<Guid>().Distinct().ToArray();
        var artwork = trackIds.Length == 0
            ? new Dictionary<Guid, string>()
            : await db.LibraryTracks.AsNoTracking().Where(item =>
                    item.TenantId == scope.TenantId && item.OwnerUserId == scope.OwnerUserId &&
                    item.Protocol == scope.Protocol && item.BackendInstanceId == scope.BackendInstanceId &&
                    item.LibraryScopeId == scope.LibraryScopeId && trackIds.Contains(item.Id))
                .ToDictionaryAsync(item => item.Id, item => item.BackendItemId, cancellationToken);
        var statuses = await TargetStatusesAsync(db, scope, records.Select(item => item.OccurrenceKey).ToArray(), cancellationToken);
        return records.Select(item => HistoryItem(
            item,
            item.LibraryTrackId is { } id && artwork.TryGetValue(id, out var backendItemId)
                ? $"/api/admin/downloads/artwork/{Uri.EscapeDataString(backendItemId)}"
                : null,
            statuses.GetValueOrDefault(item.OccurrenceKey, []))).ToArray();
    }

    private static ListeningHistoryItem HistoryItem(
        ListeningEventRecord record,
        string? artworkUrl,
        IReadOnlyList<ListeningHistoryTargetStatus> targetStatuses) => new()
        {
            Id = record.Id,
            Title = record.Title,
            Artist = record.Artist,
            Album = record.Album,
            ListenedAt = record.ListenedAt ?? record.StartedAt,
            DurationMilliseconds = record.DurationMilliseconds,
            Client = record.ClientClass,
            Source = record.SourceKind,
            Provider = record.ProviderId,
            State = record.State.ToString().ToLowerInvariant(),
            EnrichmentState = record.MusicBrainzEnrichmentState.ToString().ToLowerInvariant(),
            ArtworkUrl = artworkUrl,
            TargetStatuses = targetStatuses,
            Revision = record.Revision
        };

    private static async Task<Dictionary<string, IReadOnlyList<ListeningHistoryTargetStatus>>> TargetStatusesAsync(
        AllstarrDbContext db,
        IntelligenceScope scope,
        IReadOnlyCollection<string> occurrenceKeys,
        CancellationToken cancellationToken)
    {
        if (occurrenceKeys.Count == 0) return [];
        var rows = await db.Set<PlaybackDeliveryCheckpointEntity>().AsNoTracking().Where(item =>
                item.TenantId == scope.TenantId && item.OwnerUserId == scope.OwnerUserId &&
                item.OccurrenceKey != null && occurrenceKeys.Contains(item.OccurrenceKey) &&
                item.Kind == PlaybackScrobbleDeliveryKind.Completed)
            .OrderBy(item => item.TargetId).ToListAsync(cancellationToken);
        return rows.GroupBy(item => item.OccurrenceKey!, StringComparer.Ordinal).ToDictionary(
            group => group.Key,
            group => (IReadOnlyList<ListeningHistoryTargetStatus>)group.Select(item => new ListeningHistoryTargetStatus(
                item.TargetId,
                item.State.ToString().ToLowerInvariant(),
                item.ProviderCode,
                item.SafeMessage,
                item.RetryAfter,
                item.RequiresReauthentication,
                item.UpdatedAt)).ToArray(),
            StringComparer.Ordinal);
    }

    private void AddHistoryAudit(
        AllstarrDbContext db,
        IntelligenceScope scope,
        string action,
        Guid eventId,
        long revision,
        IReadOnlyCollection<string> changedFields) =>
        db.AuditEvents.Add(new AuditEventRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = scope.TenantId,
            ActorUserId = scope.OwnerUserId,
            Category = "listening-history",
            Action = action,
            Outcome = "success",
            CorrelationId = HttpContext.TraceIdentifier,
            DetailsJson = JsonSerializer.Serialize(new { eventId, revision, changedFields }),
            CreatedAt = Now
        });

    private static bool TryCleanRequired(string? value, out string result)
    {
        result = value?.Trim() ?? "";
        return result.Length is > 0 and <= 500 && !result.Any(char.IsControl);
    }

    private static bool TryCleanOptional(string? value, out string? result)
    {
        result = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return result == null || result.Length <= 500 && !result.Any(char.IsControl);
    }

    private static JsonElement? ParseObject(string? json)
    {
        if (json == null) return null;
        try
        {
            var value = JsonSerializer.Deserialize<JsonElement>(json);
            return value.ValueKind == JsonValueKind.Object ? value : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static object PublicPeriod(ListeningHistoryPeriod period, string timeZoneId) =>
        new { period.From, period.To, timeZoneId };
}

public class IntelligenceHistoryPeriodRequest : IntelligenceScopeRequest
{
    public DateTimeOffset? From { get; set; }
    public DateTimeOffset? To { get; set; }
    public string TimeZoneId { get; set; } = "UTC";
}

public sealed class IntelligenceHistoryRequest : IntelligenceHistoryPeriodRequest
{
    public int Limit { get; set; } = 50;
    public string? Cursor { get; set; }
    public string? Source { get; set; }
    public string? Client { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public string? Track { get; set; }
    public string? Search { get; set; }
}

public sealed class IntelligenceHistoryTopRequest : IntelligenceHistoryPeriodRequest
{
    public int Limit { get; set; } = 10;
}

public sealed class IntelligenceHistoryCorrectionRequest : IntelligenceScopeRequest
{
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public string? AlbumArtist { get; set; }
    public long ExpectedRevision { get; set; }
}

public sealed class IntelligenceHistoryDeleteRequest : IntelligenceScopeRequest
{
    public long ExpectedRevision { get; set; }
    public bool Confirmed { get; set; }
}

public sealed class IntelligenceHistoryImportPreviewRequest : IntelligenceScopeRequest
{
    public IFormFile? File { get; set; }
}

public sealed class IntelligenceHistoryImportCommandRequest : IntelligenceScopeRequest
{
    public string Revision { get; set; } = "";
}

internal readonly record struct ListeningHistoryPeriod(DateTimeOffset From, DateTimeOffset To)
{
    public static bool TryCreate(
        DateTimeOffset? from,
        DateTimeOffset? to,
        DateTimeOffset now,
        out ListeningHistoryPeriod period)
    {
        var end = to ?? now;
        var start = from ?? end.AddDays(-30);
        period = new(start, end);
        return end > start && end - start <= TimeSpan.FromDays(3650);
    }
}

internal readonly record struct ListeningHistoryCursor(DateTimeOffset ListenedAt, Guid Id)
{
    public override string ToString() => $"{ListenedAt.ToUnixTimeMilliseconds()}.{Id:N}";

    public static bool TryParse(string value, out ListeningHistoryCursor cursor)
    {
        cursor = default;
        var parts = value.Split('.', 2, StringSplitOptions.None);
        if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds) ||
            !Guid.TryParseExact(parts[1], "N", out var id)) return false;
        try
        {
            cursor = new(DateTimeOffset.FromUnixTimeMilliseconds(milliseconds), id);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}

internal static class ListeningHistoryStreaks
{
    public static (int Current, int Longest) Calculate(IEnumerable<DateOnly> values, DateOnly periodEnd)
    {
        var dates = values.Distinct().Order().ToArray();
        if (dates.Length == 0) return (0, 0);
        var longest = 1;
        var run = 1;
        for (var index = 1; index < dates.Length; index++)
        {
            run = dates[index] == dates[index - 1].AddDays(1) ? run + 1 : 1;
            longest = Math.Max(longest, run);
        }
        var set = dates.ToHashSet();
        var day = set.Contains(periodEnd) ? periodEnd : periodEnd.AddDays(-1);
        var current = 0;
        while (set.Contains(day))
        {
            current++;
            day = day.AddDays(-1);
        }
        return (current, longest);
    }
}

internal sealed class ListeningHistoryStats
{
    public int CompletedListens { get; set; }
    public int DistinctTracks { get; set; }
    public int DistinctArtists { get; set; }
    public long ListeningTimeMilliseconds { get; set; }
    public DateTimeOffset? FirstListen { get; set; }
}

internal sealed class ListeningHistoryActivityBucket
{
    public string Date { get; set; } = "";
    public int Count { get; set; }
    public long DurationMilliseconds { get; set; }
}

internal sealed record ListeningHistoryActivity(
    IReadOnlyList<ListeningHistoryActivityBucket> Buckets,
    int CurrentStreakDays,
    int LongestStreakDays);

internal sealed class ListeningHistoryTopItem
{
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public int ListenCount { get; set; }
    public long ListeningTimeMilliseconds { get; set; }
    public DateTimeOffset? LastListenedAt { get; set; }
}

internal sealed class ListeningHistoryItem
{
    public Guid Id { get; set; }
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public DateTimeOffset? ListenedAt { get; set; }
    public long? DurationMilliseconds { get; set; }
    public string? Client { get; set; }
    public string Source { get; set; } = "";
    public string? Provider { get; set; }
    public string State { get; set; } = "";
    public string EnrichmentState { get; set; } = "";
    public string? ArtworkUrl { get; set; }
    public IReadOnlyList<ListeningHistoryTargetStatus> TargetStatuses { get; set; } = [];
    public long Revision { get; set; }
}

internal sealed record ListeningHistoryTargetStatus(
    string Target,
    string State,
    string? Code,
    string? Message,
    DateTimeOffset? RetryAfter,
    bool RequiresReauthentication,
    DateTimeOffset UpdatedAt);

internal sealed class ListeningHistoryExportResult(
    IDbContextFactory<AllstarrDbContext> factory,
    IntelligenceScope scope,
    DateTimeOffset exportedAt) : IActionResult
{
    public async Task ExecuteResultAsync(ActionContext context)
    {
        var response = context.HttpContext.Response;
        var cancellationToken = context.HttpContext.RequestAborted;
        response.ContentType = "application/json; charset=utf-8";
        response.Headers.ContentDisposition = "attachment; filename=allstarr-listening-history.json";
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var writer = new Utf8JsonWriter(response.BodyWriter);
        writer.WriteStartObject();
        writer.WriteString("schema", "allstarr-listening-history");
        writer.WriteNumber("version", 1);
        writer.WriteString("exportedAt", exportedAt);
        writer.WritePropertyName("scope");
        JsonSerializer.Serialize(writer, new
        {
            scope.Protocol,
            scope.BackendInstanceId,
            scope.LibraryScopeId
        });
        writer.WriteStartArray("events");
        var count = 0;
        var query = db.ListeningEvents.AsNoTracking().Where(item =>
                item.TenantId == scope.TenantId && item.OwnerUserId == scope.OwnerUserId &&
                item.Protocol == scope.Protocol && item.BackendInstanceId == scope.BackendInstanceId &&
                item.LibraryScopeId == scope.LibraryScopeId)
            .OrderBy(item => item.ListenedAt).ThenBy(item => item.Id).AsAsyncEnumerable();
        await foreach (var item in query.WithCancellation(cancellationToken))
        {
            JsonSerializer.Serialize(writer, new
            {
                occurrenceId = item.Id,
                state = item.State.ToString().ToLowerInvariant(),
                item.StartedAt,
                item.ListenedAt,
                item.PositionTicks,
                item.DurationMilliseconds,
                client = item.ClientClass,
                device = item.DeviceClass,
                source = item.SourceKind,
                item.Title,
                item.Artist,
                item.Album,
                item.AlbumArtist,
                recordingMusicBrainzId = item.RecordingMusicBrainzId,
                item.Isrc,
                item.TrackNumber,
                item.ChosenByUser,
                provider = item.ProviderId,
                enrichmentState = item.MusicBrainzEnrichmentState.ToString().ToLowerInvariant(),
                item.MusicBrainzEnrichmentConfidence,
                item.MusicBrainzSourceRevision,
                item.MusicBrainzFactsJson,
                item.MusicBrainzEnrichedAt
            });
            if (++count % 100 == 0) await writer.FlushAsync(cancellationToken);
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        await writer.FlushAsync(cancellationToken);
    }
}

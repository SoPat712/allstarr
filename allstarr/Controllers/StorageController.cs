using allstarr.Core.Storage;
using allstarr.Filters;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/storage")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class StorageController(DurableStorageState storageState, DurableBackupService backupService) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken = default)
    {
        if (RequireAdministrator() is { } error) return error;
        var storage = storageState.GetSnapshot();
        return Ok(new
        {
            storage = new
            {
                provider = storage.Provider.ToString(),
                readiness = storage.Readiness.ToString(),
                storage.SchemaVersion,
                storage.ErrorCode,
                storage.CheckedAt
            },
            backups = await backupService.ListAsync(cancellationToken),
            restorePending = backupService.HasPendingRestore
        });
    }

    [HttpPost("backups")]
    public async Task<IActionResult> CreateBackup(CancellationToken cancellationToken = default)
    {
        if (RequireAdministrator() is { } error) return error;
        try { return Ok(await backupService.CreateAsync(cancellationToken)); }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or SqliteException or UnauthorizedAccessException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "backup_unavailable",
                message = "The backup could not be created. Check storage readiness, disk space, and key-ring access."
            });
        }
    }

    [HttpGet("backups/{id:guid}/download")]
    public async Task<IActionResult> DownloadBackup(Guid id, CancellationToken cancellationToken = default)
    {
        if (RequireAdministrator() is { } error) return error;
        try
        {
            var download = await backupService.OpenVerifiedAsync(id, cancellationToken);
            return File(download.Stream, "application/zip", download.FileName);
        }
        catch (FileNotFoundException) { return NotFound(new { error = "backup_not_found" }); }
        catch (Exception exception) when (exception is BackupVerificationException or InvalidDataException)
        {
            return UnprocessableEntity(new { error = "backup_invalid", message = "Backup verification failed." });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "backup_unavailable" });
        }
    }

    [HttpPost("restore")]
    [RequestSizeLimit(DurableBackupService.MaximumArchiveBytes + 64 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = DurableBackupService.MaximumArchiveBytes)]
    public async Task<IActionResult> StageRestore([FromForm] IFormFile? backup, CancellationToken cancellationToken = default)
    {
        if (RequireAdministrator() is { } error) return error;
        if (backup is null || backup.Length == 0)
            return BadRequest(new { error = "backup_required", message = "Select a backup ZIP to restore." });
        if (backup.Length > DurableBackupService.MaximumArchiveBytes)
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new { error = "backup_too_large", message = "The backup ZIP must be no larger than 1 GiB." });
        try
        {
            await using var input = backup.OpenReadStream();
            await backupService.StageRestoreAsync(input, cancellationToken);
            return Ok(new { restartRequired = true, message = "Restart Allstarr to finish restoring." });
        }
        catch (RestorePendingException)
        {
            return Conflict(new { error = "restore_pending", message = "Restart Allstarr to finish the pending restore." });
        }
        catch (Exception exception) when (exception is BackupVerificationException or InvalidDataException or SqliteException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return UnprocessableEntity(new { error = "backup_invalid", message = "Backup verification failed. Check the archive, database schema, and key ring." });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "restore_unavailable", message = "The restore could not be staged. Check available disk space and data-directory access." });
        }
    }

    private IActionResult? RequireAdministrator()
    {
        if (!HttpContext.Items.TryGetValue(AdminAuthSessionService.HttpContextSessionItemKey, out var value) ||
            value is not AdminAuthSession session)
            return Unauthorized(new { error = "admin_session_required" });
        return session.IsAdministrator ? null : StatusCode(StatusCodes.Status403Forbidden, new { error = "administrator_required" });
    }
}

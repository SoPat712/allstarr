using allstarr.Core.Storage;
using allstarr.Filters;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/storage")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class StorageController : ControllerBase
{
    private readonly DurableStorageState _storageState;
    private readonly DurableBackupService _backupService;
    private readonly IDbContextFactory<AllstarrDbContext> _contextFactory;

    public StorageController(
        DurableStorageState storageState,
        DurableBackupService backupService,
        IDbContextFactory<AllstarrDbContext> contextFactory)
    {
        _storageState = storageState;
        _backupService = backupService;
        _contextFactory = contextFactory;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken = default)
    {
        var storage = _storageState.GetSnapshot();
        var storageResponse = new
        {
            provider = storage.Provider.ToString(),
            readiness = storage.Readiness.ToString(),
            storage.SchemaVersion,
            storage.ErrorCode,
            storage.CheckedAt
        };
        if (storage.Readiness != DurableStorageReadiness.Ready)
        {
            return Ok(new { storage = storageResponse, backups = Array.Empty<object>() });
        }

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var backups = await context.Backups.AsNoTracking()
            .OrderByDescending(item => item.CreatedAt)
            .Take(50)
            .Select(item => new
            {
                item.Id,
                item.StorageProvider,
                item.Sha256,
                item.SchemaVersion,
                item.ApplicationVersion,
                item.Status,
                item.CreatedAt,
                item.VerifiedAt,
                item.RestoreStatus,
                item.RestoreVerifiedAt
            })
            .ToListAsync(cancellationToken);
        return Ok(new { storage = storageResponse, backups });
    }

    [HttpPost("backups")]
    public Task<IActionResult> CreateBackup(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IActionResult>(StatusCode(StatusCodes.Status503ServiceUnavailable, new
        {
            error = "backups_unavailable",
            message = "Backups are being rebuilt."
        }));
    }
}

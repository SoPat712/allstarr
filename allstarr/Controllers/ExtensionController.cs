using Microsoft.AspNetCore.Mvc;
using allstarr.Filters;
using allstarr.Services.Common;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/extensions")]
[ServiceFilter(typeof(AdminPortFilter))]
public class ExtensionController : ControllerBase
{
    private readonly ExtensionManager _extensionManager;
    private readonly ILogger<ExtensionController> _logger;

    public ExtensionController(ExtensionManager extensionManager, ILogger<ExtensionController> logger)
    {
        _extensionManager = extensionManager;
        _logger = logger;
    }

    [HttpGet("repos")]
    public IActionResult GetRepositories()
    {
        return Ok(_extensionManager.GetConfiguredRepositories());
    }

    [HttpGet("store")]
    public async Task<IActionResult> GetStoreExtensions(CancellationToken cancellationToken)
    {
        var catalog = await _extensionManager.FetchStoreCatalogAsync(cancellationToken);
        return Ok(catalog);
    }

    [HttpGet("installed")]
    public IActionResult GetInstalledExtensions()
    {
        var items = _extensionManager.GetActiveExtensions()
            .Select(e => new
            {
                e.Id,
                e.Name,
                e.DisplayName,
                e.Description,
                e.Version,
                e.Types
            })
            .ToList();
        return Ok(items);
    }

    [HttpPost("install")]
    public async Task<IActionResult> InstallExtension([FromBody] InstallRequest request, CancellationToken cancellationToken)
    {
        var downloadUrl = request.DownloadUrl;
        if (string.IsNullOrWhiteSpace(downloadUrl) && !string.IsNullOrWhiteSpace(request.Id))
        {
            var catalog = await _extensionManager.FetchStoreCatalogAsync(cancellationToken);
            downloadUrl = catalog.Items
                .FirstOrDefault(item => item.Id.Equals(request.Id, StringComparison.OrdinalIgnoreCase))
                ?.DownloadUrl;
        }

        if (string.IsNullOrEmpty(downloadUrl))
        {
            return BadRequest(new { success = false, message = "Download URL or store extension ID is required." });
        }

        var success = await _extensionManager.InstallExtensionAsync(downloadUrl, cancellationToken);
        if (success)
        {
            return Ok(new { success = true, message = "Extension installed and loaded successfully." });
        }
        else
        {
            return StatusCode(500, new { success = false, message = "Failed to install extension." });
        }
    }

    [HttpDelete("uninstall/{id}")]
    public IActionResult UninstallExtension(string id)
    {
        var success = _extensionManager.UninstallExtension(id);
        if (success)
        {
            return Ok(new { success = true, message = "Extension uninstalled successfully." });
        }
        else
        {
            return NotFound(new { success = false, message = "Extension not found or failed to delete." });
        }
    }
}

public class InstallRequest
{
    public string Id { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
}

using Microsoft.AspNetCore.Mvc;
using allstarr.Filters;
using allstarr.Services.Common;
using allstarr.Core.Storage;
using allstarr.Core.Extensions;
using allstarr.Middleware;
using allstarr.Services.Admin;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/extensions")]
[ServiceFilter(typeof(AdminPortFilter))]
public class ExtensionController : ControllerBase
{
    private readonly ExtensionManager _extensionManager;
    private readonly ExtensionControlPlaneService _controlPlane;
    private readonly ExtensionRuntimeCoordinator? _runtime;
    private readonly ILogger<ExtensionController> _logger;

    public ExtensionController(
        ExtensionManager extensionManager,
        ExtensionControlPlaneService controlPlane,
        ExtensionRuntimeCoordinator? runtime,
        ILogger<ExtensionController> logger)
    {
        _extensionManager = extensionManager;
        _controlPlane = controlPlane;
        _runtime = runtime;
        _logger = logger;
    }

    [HttpGet("registries")]
    public async Task<IActionResult> ListRegistries(CancellationToken cancellationToken)
    {
        if (RequireAdministrator() is { } error) return error;
        var registries = await _controlPlane.ListRegistriesAsync(cancellationToken);
        return Ok(registries.Select(RegistryResponse));
    }

    [HttpPost("registries")]
    public async Task<IActionResult> AddRegistry([FromBody] RegistryRequest request, CancellationToken cancellationToken)
    {
        if (RequireAdministrator() is { } error) return error;
        try
        {
            await _extensionManager.ValidateStoreRegistryAsync(request.RegistryUrl ?? string.Empty, cancellationToken);
            var registry = await _controlPlane.AddRegistryAsync(
                new ExtensionRegistryInput(request.Name ?? string.Empty, request.RegistryUrl ?? string.Empty, request.Enabled),
                cancellationToken);
            return Ok(RegistryResponse(registry));
        }
        catch (ArgumentException exception) { return BadRequest(new { error = exception.Message }); }
        catch (InvalidDataException exception) { return BadRequest(new { error = exception.Message }); }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status504GatewayTimeout, new
            {
                error = "Registry validation timed out. Check that the URL points directly to a reachable JSON document."
            });
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(exception, "Failed to validate extension registry {RegistryUrl}", request.RegistryUrl);
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                error = "Registry could not be reached. Check the URL and try again."
            });
        }
    }

    [HttpPatch("registries/{registryId:guid}")]
    public async Task<IActionResult> SetRegistryEnabled(
        Guid registryId,
        [FromBody] RegistryStateRequest request,
        CancellationToken cancellationToken)
    {
        if (RequireAdministrator() is { } error) return error;
        try
        {
            var registry = await _controlPlane.SetRegistryEnabledAsync(
                registryId, request.Enabled, request.ExpectedRevision, cancellationToken);
            return Ok(RegistryResponse(registry));
        }
        catch (Exception exception) { return ControlPlaneError(exception); }
    }

    [HttpDelete("registries/{registryId:guid}")]
    public async Task<IActionResult> RemoveRegistry(
        Guid registryId,
        [FromQuery] long expectedRevision,
        CancellationToken cancellationToken)
    {
        if (RequireAdministrator() is { } error) return error;
        try
        {
            await _controlPlane.RemoveRegistryAsync(registryId, expectedRevision, cancellationToken);
            return Ok(new { success = true, message = "Extension registry removed." });
        }
        catch (Exception exception) { return ControlPlaneError(exception); }
    }

    [HttpGet("packages")]
    public async Task<IActionResult> ListPackages([FromQuery] string? extensionId, CancellationToken cancellationToken)
    {
        if (RequireAdministrator() is { } error) return error;
        var packages = await _controlPlane.ListPackagesAsync(extensionId, cancellationToken);
        return Ok(packages.Select(PackageResponse));
    }

    [HttpGet("packages/{packageId:guid}/permissions")]
    public async Task<IActionResult> ListPermissions(Guid packageId, CancellationToken cancellationToken)
    {
        if (RequireAdministrator() is { } error) return error;
        try
        {
            var reviews = await _controlPlane.ListPermissionReviewsAsync(packageId, cancellationToken);
            return Ok(reviews.Select(item => new
            {
                item.Id,
                item.ExtensionPackageId,
                item.PermissionKind,
                item.PermissionValue,
                item.Required,
                decision = item.Decision.ToString().ToLowerInvariant(),
                item.ReviewedByUserId,
                item.CreatedAt,
                item.ReviewedAt,
                item.Revision
            }));
        }
        catch (Exception exception) { return ControlPlaneError(exception); }
    }

    [HttpGet("packages/{packageId:guid}/icon")]
    public async Task<IActionResult> PackageIcon(Guid packageId, CancellationToken cancellationToken)
    {
        if (RequireAdministrator() is { } error) return error;
        var package = (await _controlPlane.ListPackagesAsync(cancellationToken: cancellationToken))
            .SingleOrDefault(item => item.Id == packageId);
        return package == null ? NotFound() : ServePackageIcon(package);
    }

    [HttpGet("providers/{extensionId}/icon")]
    public async Task<IActionResult> ProviderIcon(string extensionId, CancellationToken cancellationToken)
    {
        if (!HttpContext.Items.TryGetValue(AdminAuthSessionService.HttpContextSessionItemKey, out var value) ||
            value is not AdminAuthSession)
            return Unauthorized(new { error = "Authentication required" });
        var package = (await _controlPlane.ListPackagesAsync(extensionId, cancellationToken))
            .FirstOrDefault(item => item.State == ExtensionPackageState.Active);
        return package == null ? NotFound() : ServePackageIcon(package);
    }

    private IActionResult ServePackageIcon(ExtensionPackageRecord package)
    {
        try
        {
            var manifest = ExtensionSdkV1.ParseManifest(package.ManifestJson);
            var icon = manifest.IconPath;
            if (string.IsNullOrWhiteSpace(icon))
                icon = new[] { "icon.png", "icon.jpg", "icon.jpeg", "icon.webp" }
                    .FirstOrDefault(candidate => System.IO.File.Exists(Path.Combine(package.PackagePath, candidate)));
            if (string.IsNullOrWhiteSpace(icon)) return NotFound();
            var root = Path.GetFullPath(package.PackagePath);
            var path = Path.GetFullPath(Path.Combine(root, icon));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                !System.IO.File.Exists(path))
                return NotFound();
            var contentType = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                _ => null
            };
            return contentType == null ? NotFound() : PhysicalFile(path, contentType);
        }
        catch (ExtensionSdkValidationException)
        {
            return NotFound();
        }
    }

    [HttpPost("packages/{packageId:guid}/review")]
    public async Task<IActionResult> ReviewPermissions(
        Guid packageId,
        [FromBody] PermissionReviewRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetAdministrator(out var session, out var error)) return error!;
        if (!session.AllstarrUserId.HasValue)
            return Conflict(new { error = "The administrator session is not linked to an Allstarr user." });
        try
        {
            var package = await _controlPlane.ReviewAsync(
                packageId,
                session.AllstarrUserId.Value,
                request.ExpectedRevision,
                request.Decisions.Select(item => new ExtensionPermissionDecisionInput(
                    item.Kind ?? string.Empty, item.Value ?? string.Empty, item.Approved)).ToList(),
                cancellationToken);
            return Ok(PackageResponse(package));
        }
        catch (Exception exception) { return ControlPlaneError(exception); }
    }

    [HttpPost("packages/{packageId:guid}/activate")]
    public async Task<IActionResult> Activate(Guid packageId, [FromBody] RevisionRequest request, CancellationToken cancellationToken)
    {
        if (RequireAdministrator() is { } error) return error;
        try
        {
            return Ok(PackageResponse(_runtime == null
            ? await _controlPlane.ActivateAsync(packageId, request.ExpectedRevision, cancellationToken)
            : await _runtime.ActivateAsync(packageId, request.ExpectedRevision, cancellationToken)));
        }
        catch (Exception exception) { return ControlPlaneError(exception); }
    }

    [HttpPost("packages/{packageId:guid}/permissions/revoke")]
    public async Task<IActionResult> RevokePermissionGrants(
        Guid packageId,
        [FromBody] RevisionRequest request,
        CancellationToken cancellationToken)
    {
        if (RequireAdministrator() is { } error) return error;
        try
        {
            var package = _runtime == null
                ? await _controlPlane.ResetPermissionsForReviewAsync(
                    packageId, request.ExpectedRevision, cancellationToken)
                : await _runtime.ResetPermissionsForReviewAsync(
                    packageId, request.ExpectedRevision, cancellationToken);
            return Ok(PackageResponse(package));
        }
        catch (Exception exception) { return ControlPlaneError(exception); }
    }

    [HttpPost("packages/{packageId:guid}/staging/cancel")]
    public async Task<IActionResult> CancelStaging(
        Guid packageId,
        [FromBody] RevisionRequest request,
        CancellationToken cancellationToken)
    {
        if (RequireAdministrator() is { } error) return error;
        try
        {
            var package = _runtime == null
                ? await _controlPlane.CancelStagingAsync(
                    packageId, request.ExpectedRevision, cancellationToken)
                : await _runtime.CancelStagingAsync(
                    packageId, request.ExpectedRevision, cancellationToken);
            return Ok(PackageResponse(package));
        }
        catch (Exception exception) { return ControlPlaneError(exception); }
    }

    [HttpGet("packages/{packageId:guid}/session")]
    public IActionResult SignedSessionStatus(Guid packageId)
    {
        if (RequireAdministrator() is { } error) return error;
        if (_runtime == null) return Conflict(new { error = "The extension runtime is unavailable." });
        try { return Ok(_runtime.SignedSessionStatus(packageId)); }
        catch (Exception exception) { return ControlPlaneError(exception); }
    }

    [HttpPost("packages/{packageId:guid}/session/start")]
    public IActionResult StartSignedSession(Guid packageId)
    {
        if (RequireAdministrator() is { } error) return error;
        if (_runtime == null) return Conflict(new { error = "The extension runtime is unavailable." });
        try { return Ok(_runtime.StartSignedSessionVerification(packageId)); }
        catch (Exception exception) { return ControlPlaneError(exception); }
    }

    [HttpPost("packages/{packageId:guid}/session/grant")]
    public IActionResult CompleteSignedSession(Guid packageId, [FromBody] SignedSessionGrantRequest request)
    {
        if (RequireAdministrator() is { } error) return error;
        if (_runtime == null) return Conflict(new { error = "The extension runtime is unavailable." });
        if (string.IsNullOrWhiteSpace(request.Grant)) return BadRequest(new { error = "A session grant is required." });
        try { return Ok(_runtime.CompleteSignedSessionGrant(packageId, request.Grant)); }
        catch (Exception exception) { return ControlPlaneError(exception); }
    }

    [HttpDelete("packages/{packageId:guid}/session")]
    public IActionResult ClearSignedSession(Guid packageId)
    {
        if (RequireAdministrator() is { } error) return error;
        if (_runtime == null) return Conflict(new { error = "The extension runtime is unavailable." });
        try { return Ok(_runtime.ClearSignedSession(packageId)); }
        catch (Exception exception) { return ControlPlaneError(exception); }
    }

    [HttpPost("packages/{packageId:guid}/rollback")]
    public async Task<IActionResult> Rollback(Guid packageId, [FromBody] RevisionRequest request, CancellationToken cancellationToken)
    {
        if (RequireAdministrator() is { } error) return error;
        try
        {
            return Ok(PackageResponse(_runtime == null
            ? await _controlPlane.RollbackAsync(packageId, request.ExpectedRevision, cancellationToken)
            : await _runtime.RollbackAsync(packageId, request.ExpectedRevision, cancellationToken)));
        }
        catch (Exception exception) { return ControlPlaneError(exception); }
    }

    [HttpPost("packages/{packageId:guid}/disable")]
    public async Task<IActionResult> DisablePackage(Guid packageId, [FromBody] RevisionRequest request, CancellationToken cancellationToken)
    {
        if (RequireAdministrator() is { } error) return error;
        try
        {
            if (_runtime == null) await _controlPlane.DisableAsync(packageId, request.ExpectedRevision, cancellationToken);
            else await _runtime.DisableAsync(packageId, request.ExpectedRevision, cancellationToken);
            return NoContent();
        }
        catch (Exception exception) { return ControlPlaneError(exception); }
    }

    [HttpDelete("packages/{packageId:guid}")]
    public async Task<IActionResult> UninstallPackage(
        Guid packageId, [FromBody] RevisionRequest request, CancellationToken cancellationToken)
    {
        if (RequireAdministrator() is { } error) return error;
        if (_runtime == null) return Conflict(new { error = "The extension runtime is unavailable." });
        try
        {
            return Ok(PackageResponse(await _runtime.UninstallAsync(
                packageId, request.ExpectedRevision, cancellationToken)));
        }
        catch (Exception exception) { return ControlPlaneError(exception); }
    }

    [HttpGet("logs")]
    public async Task<IActionResult> ListLogs(
        [FromQuery] Guid? packageId,
        [FromQuery] string? extensionId,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (RequireAdministrator() is { } error) return error;
        try
        {
            var logs = await _controlPlane.ListLogsAsync(packageId, extensionId, limit, cancellationToken);
            return Ok(logs.Select(item => new
            {
                item.Id,
                item.ExtensionPackageId,
                item.ExtensionId,
                item.Level,
                item.EventCode,
                item.Message,
                summary = ExtensionLogSummary(item.EventCode, item.Message),
                category = "extension",
                item.CorrelationId,
                item.CreatedAt
            }));
        }
        catch (Exception exception) { return ControlPlaneError(exception); }
    }

    [HttpGet("store")]
    public async Task<IActionResult> GetStoreExtensions(CancellationToken cancellationToken)
    {
        if (RequireAdministrator() is { } error) return error;
        var catalog = await _extensionManager.FetchStoreCatalogAsync(cancellationToken);
        return Ok(catalog);
    }

    [HttpPost("install")]
    public async Task<IActionResult> InstallExtension([FromBody] InstallRequest request, CancellationToken cancellationToken)
    {
        if (RequireAdministrator() is { } authError) return authError;
        if (!_extensionManager.RemoteInstallEnabled)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                success = false,
                message = "Remote extension installation is disabled. An operator must explicitly set Extensions:AllowRemoteInstall=true."
            });
        }

        var downloadUrl = request.DownloadUrl;
        var sha256 = request.Sha256;
        var registryId = request.RegistryId;
        if (string.IsNullOrWhiteSpace(downloadUrl) && !string.IsNullOrWhiteSpace(request.Id))
        {
            var catalog = await _extensionManager.FetchStoreCatalogAsync(cancellationToken);
            var item = catalog.Items.FirstOrDefault(item => item.Id.Equals(request.Id, StringComparison.OrdinalIgnoreCase));
            downloadUrl = item?.DownloadUrl;
            sha256 = item?.Sha256;
            registryId = item?.RegistryId;
        }

        if (string.IsNullOrEmpty(downloadUrl) || string.IsNullOrWhiteSpace(sha256))
        {
            return BadRequest(new { success = false, message = "A package URL/store ID and mandatory SHA-256 checksum are required." });
        }

        try
        {
            if (registryId.HasValue)
            {
                var catalog = await _extensionManager.FetchStoreCatalogAsync(cancellationToken);
                var matchesRegistry = catalog.Items.Any(item => item.RegistryId == registryId &&
                    item.DownloadUrl.Equals(downloadUrl, StringComparison.Ordinal) &&
                    item.Sha256.Equals(sha256, StringComparison.OrdinalIgnoreCase));
                if (!matchesRegistry)
                    return BadRequest(new { success = false, message = "The package URL and checksum do not match the selected registry." });
            }
            var package = await _extensionManager.StageExtensionAsync(downloadUrl, sha256, registryId, cancellationToken);
            return Accepted(new
            {
                success = true,
                packageId = package.Id,
                package.ExtensionId,
                package.Version,
                state = package.State.ToString().ToLowerInvariant(),
                package.Revision,
                message = package.State == ExtensionPackageState.ReviewRequired
                    ? "Package verified and staged. Review its permissions before activation."
                    : "Package verified and staged for explicit activation."
            });
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or UnauthorizedAccessException or ExtensionSdkValidationException)
        {
            return BadRequest(new { success = false, message = exception.Message });
        }
    }

    private IActionResult? RequireAdministrator() =>
        TryGetAdministrator(out _, out var error) ? null : error;

    private bool TryGetAdministrator(out AdminAuthSession session, out IActionResult? error)
    {
        session = null!;
        error = null;
        if (!HttpContext.Items.TryGetValue(AdminAuthSessionService.HttpContextSessionItemKey, out var value) ||
            value is not AdminAuthSession current)
        {
            error = Unauthorized(new { error = "Authentication required" });
            return false;
        }
        if (!current.IsAdministrator)
        {
            error = StatusCode(StatusCodes.Status403Forbidden, new { error = "Administrator access required" });
            return false;
        }
        session = current;
        return true;
    }

    private IActionResult ControlPlaneError(Exception exception) => exception switch
    {
        KeyNotFoundException => NotFound(new { error = exception.Message }),
        DbUpdateConcurrencyException => Conflict(new { error = "The extension resource changed before this update." }),
        ExtensionRegistryInUseException registryInUse => Conflict(new
        {
            error = registryInUse.Message,
            code = "registry_in_use",
            dependencies = registryInUse.Dependencies.Select(item => new
            {
                item.PackageId,
                item.ExtensionId,
                item.DisplayName,
                item.Version,
                state = item.State.ToString().ToLowerInvariant()
            })
        }),
        UnauthorizedAccessException => StatusCode(StatusCodes.Status403Forbidden, new { error = exception.Message }),
        ArgumentException or InvalidOperationException or ExtensionSdkValidationException =>
            BadRequest(new { error = exception.Message }),
        _ => throw exception
    };

    private static object RegistryResponse(ExtensionRegistryRecord item) => new
    {
        item.Id,
        item.Name,
        item.RegistryUrl,
        item.Enabled,
        item.CreatedAt,
        item.UpdatedAt,
        item.Revision
    };

    private static object PackageResponse(ExtensionPackageRecord item)
    {
        ExtensionSdkManifest? manifest = null;
        try { manifest = ExtensionSdkV1.ParseManifest(item.ManifestJson); }
        catch (ExtensionSdkValidationException) { }
        var hasPackageIcon = !string.IsNullOrWhiteSpace(manifest?.IconPath) ||
            new[] { "icon.png", "icon.jpg", "icon.jpeg", "icon.webp" }
                .Any(candidate => System.IO.File.Exists(Path.Combine(item.PackagePath, candidate)));
        return new
        {
            item.Id,
            item.RegistryId,
            item.PreviousPackageId,
            item.ExtensionId,
            item.DisplayName,
            item.Version,
            item.SdkVersion,
            item.Sha256,
            lifecycle = item.State.ToString().ToLowerInvariant(),
            installed = item.State is ExtensionPackageState.Active or
                ExtensionPackageState.Disabled or ExtensionPackageState.RolledBack,
            active = item.State == ExtensionPackageState.Active,
            permissionReviewRequired = item.State == ExtensionPackageState.ReviewRequired,
            hasPermissions = manifest?.Permissions.Count > 0,
            description = manifest?.Description,
            author = manifest?.Author,
            iconUrl = hasPackageIcon ? $"/api/admin/extensions/packages/{item.Id}/icon" : null,
            settings = manifest?.Settings,
            capabilities = manifest?.Capabilities
                .Select(capability => capability.Kind.ToString().ToLowerInvariant())
                .Distinct()
                .ToArray(),
            qualityOptions = manifest?.QualityOptions,
            requiredRuntimeFeatures = manifest?.RequiredRuntimeFeatures,
            compatibility = manifest?.Compatibility,
            usesSignedSession = manifest?.SignedSession != null,
            state = item.State.ToString().ToLowerInvariant(),
            item.FailureCode,
            item.StagedAt,
            item.ReviewedAt,
            item.ActivatedAt,
            item.DisabledAt,
            item.Revision
        };
    }

    private static string ExtensionLogSummary(string? eventCode, string? message)
    {
        var code = eventCode?.Trim().ToLowerInvariant();
        if (code == "runtime.log" && !string.IsNullOrWhiteSpace(message))
            return RuntimeLogSummary(message);

        return code switch
        {
            "package.staged" => "Extension package staged",
            "package.reviewed" => "Extension permissions reviewed",
            "package.activated" => "Extension activated",
            "package.disabled" => "Extension disabled",
            "package.uninstalled" => "Extension uninstalled",
            "package.rollback" or "package.rolled_back" => "Extension rolled back",
            "session.started" => "Extension sign-in started",
            "session.granted" => "Extension sign-in completed",
            "session.cleared" => "Extension sign-in cleared",
            "runtime.started" => "Extension runtime started",
            "runtime.stopped" => "Extension runtime stopped",
            "runtime.failed" => "Extension runtime failed",
            null or "" => "Extension event",
            _ => string.Join(' ', code.Split('.', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => char.ToUpperInvariant(part[0]) + part[1..]))
        };
    }

    private static string RuntimeLogSummary(string message)
    {
        var value = message.Trim();
        var badResponse = Regex.Match(value,
            "^(?<operation>[A-Za-z][A-Za-z0-9]*) bad response (?<status>[1-5][0-9]{2})$",
            RegexOptions.CultureInvariant);
        if (!badResponse.Success) return value;

        var operation = badResponse.Groups["operation"].Value switch
        {
            "performSearchSync" => "Provider search",
            "performGetTrackSync" => "Track lookup",
            "performGetAlbumSync" => "Album lookup",
            "performGetArtistSync" => "Artist lookup",
            "performLyricsSync" => "Lyrics lookup",
            "performDownloadSync" => "Download",
            _ => "Extension request"
        };
        return $"{operation} failed ({badResponse.Groups["status"].Value})";
    }
}

public class InstallRequest
{
    public string Id { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public Guid? RegistryId { get; set; }
}

public sealed class SignedSessionGrantRequest
{
    public string Grant { get; set; } = "";
}

public sealed class RegistryRequest
{
    public string? Name { get; set; }
    public string? RegistryUrl { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed class RegistryStateRequest
{
    public bool Enabled { get; set; }
    public long ExpectedRevision { get; set; }
}

public class RevisionRequest
{
    public long ExpectedRevision { get; set; }
}

public sealed class PermissionReviewRequest : RevisionRequest
{
    public IReadOnlyList<PermissionDecisionRequest> Decisions { get; set; } = [];
}

public sealed class PermissionDecisionRequest
{
    public string? Kind { get; set; }
    public string? Value { get; set; }
    public bool Approved { get; set; }
}

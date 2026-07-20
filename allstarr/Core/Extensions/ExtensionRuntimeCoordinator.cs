using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Spotify;
using allstarr.Core.Storage;
using allstarr.Core.Downloads;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using System.Collections.Concurrent;

namespace allstarr.Core.Extensions;

public sealed class ExtensionRuntimeCoordinator : IHostedService
{
    private readonly IDbContextFactory<AllstarrDbContext> _factory;
    private readonly ExtensionControlPlaneService _controlPlane;
    private readonly IDynamicProviderRegistry _registry;
    private readonly IProviderRegistry _readRegistry;
    private readonly IHttpClientFactory _clients;
    private readonly IProviderAccountSecretAccessor _secrets;
    private readonly ILogger<ExtensionRuntimeCoordinator> _logger;
    private readonly FirstPartyExtensionPolicy _firstPartyPolicy;
    private readonly ProviderDownloadArtifactResolver _downloadArtifacts;
    private readonly ProviderDownloadWorkspaceOptions _downloadOptions;
    private readonly string _runtimeRoot;
    private readonly string _packageRoot;
    private readonly IDataProtector _sessionProtector;
    private readonly ConcurrentDictionary<Guid, ExtensionSandbox> _sandboxes = new();

    public ExtensionRuntimeCoordinator(
        IDbContextFactory<AllstarrDbContext> factory,
        ExtensionControlPlaneService controlPlane,
        IDynamicProviderRegistry registry,
        IProviderRegistry readRegistry,
        IHttpClientFactory clients,
        IProviderAccountSecretAccessor secrets,
        FirstPartyExtensionPolicy firstPartyPolicy,
        ProviderDownloadArtifactResolver downloadArtifacts,
        ProviderDownloadWorkspaceOptions downloadOptions,
        IConfiguration configuration,
        ILogger<ExtensionRuntimeCoordinator> logger,
        IDataProtectionProvider? dataProtectionProvider = null)
    {
        _factory = factory;
        _controlPlane = controlPlane;
        _registry = registry;
        _readRegistry = readRegistry;
        _clients = clients;
        _secrets = secrets;
        _firstPartyPolicy = firstPartyPolicy;
        _downloadArtifacts = downloadArtifacts;
        _downloadOptions = downloadOptions;
        _logger = logger;
        _sessionProtector = (dataProtectionProvider ?? new EphemeralDataProtectionProvider())
            .CreateProtector("allstarr.extensions.signed-session.v1");
        _packageRoot = Path.GetFullPath(configuration["Extensions:Directory"] ??
                                        Path.Combine(Directory.GetCurrentDirectory(), "extensions"));
        _runtimeRoot = Path.Combine(_packageRoot, ".runtime");
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var active = await db.ExtensionPackages.AsNoTracking()
            .Where(item => item.State == ExtensionPackageState.Active)
            .OrderBy(item => item.ExtensionId)
            .ToListAsync(cancellationToken);
        foreach (var package in active)
        {
            try
            {
                var registration = await BuildRegistrationAsync(package, cancellationToken);
                RegisterVerified(registration, package);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Active extension {ExtensionId} could not be restored", package.ExtensionId);
                await _controlPlane.WriteLogAsync(package.Id, "error", "runtime.restore-failed",
                    "The active package could not be restored into the provider runtime.", "extension-startup", cancellationToken);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task<ExtensionPackageRecord> ActivateAsync(
        Guid packageId, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var package = await GetPackageAsync(packageId, cancellationToken);
        var registration = await BuildRegistrationAsync(package, cancellationToken);
        RejectBuiltInCollision(registration.Descriptor.Id, package);
        var active = await _controlPlane.ActivateAsync(packageId, expectedRevision, cancellationToken);
        RegisterVerified(registration, package);
        return active;
    }

    public async Task<ExtensionPackageRecord> RollbackAsync(
        Guid activePackageId, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var active = await GetPackageAsync(activePackageId, cancellationToken);
        if (!active.PreviousPackageId.HasValue)
            throw new InvalidOperationException("The active package has no rollback version.");
        var previous = await GetPackageAsync(active.PreviousPackageId.Value, cancellationToken);
        var registration = await BuildRegistrationAsync(previous, cancellationToken);
        RejectBuiltInCollision(registration.Descriptor.Id, previous);
        var restored = await _controlPlane.RollbackAsync(activePackageId, expectedRevision, cancellationToken);
        RegisterVerified(registration, previous);
        return restored;
    }

    public async Task DisableAsync(Guid packageId, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var package = await GetPackageAsync(packageId, cancellationToken);
        await _controlPlane.DisableAsync(packageId, expectedRevision, cancellationToken);
        _registry.RemoveExtension(package.ExtensionId);
        _sandboxes.TryRemove(packageId, out _);
    }

    public async Task<ExtensionPackageRecord> UninstallAsync(
        Guid packageId, long expectedRevision, bool retainProviderAccounts,
        CancellationToken cancellationToken = default)
    {
        if (!retainProviderAccounts)
            throw new InvalidOperationException(
                "SDK v1 uninstall retains provider accounts and encrypted secrets. Delete accounts separately with tenant-aware confirmation.");
        var package = await GetPackageAsync(packageId, cancellationToken);
        var uninstalled = await _controlPlane.UninstallAsync(packageId, expectedRevision, cancellationToken);
        _registry.RemoveExtension(package.ExtensionId);
        _sandboxes.TryRemove(packageId, out _);
        try
        {
            var packagePath = Path.GetFullPath(package.PackagePath);
            var relative = Path.GetRelativePath(_packageRoot, packagePath);
            if (Path.IsPathRooted(relative) || relative is "" or "." or ".." ||
                relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("Extension package cleanup path is outside the package root.");
            if (Directory.Exists(packagePath)) Directory.Delete(packagePath, recursive: true);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Uninstalled extension content requires filesystem cleanup for {ExtensionId}", package.ExtensionId);
            await _controlPlane.WriteLogAsync(package.Id, "warning", "package.cleanup-pending",
                "Package state is uninstalled but its staged directory still requires cleanup.",
                "extension-uninstall", cancellationToken);
        }
        return uninstalled;
    }

    private async Task<ProviderRegistration> BuildRegistrationAsync(
        ExtensionPackageRecord package, CancellationToken cancellationToken)
    {
        var manifest = ExtensionSdkV1.ParseManifest(package.ManifestJson);
        if (!manifest.Id.Equals(package.ExtensionId, StringComparison.Ordinal) ||
            !manifest.Version.Equals(package.Version, StringComparison.Ordinal))
            throw new ExtensionSdkValidationException("Stored extension identity does not match its manifest.");
        var actualHash = ExtensionSdkV1.ComputePackageContentSha256(package.PackagePath);
        if (!actualHash.Equals(package.ContentSha256, StringComparison.OrdinalIgnoreCase))
            throw new ExtensionSdkValidationException("Extension package contents changed after staging.");
        var unsupportedRuntimeFeatures = (manifest.RequiredRuntimeFeatures ?? [])
            .Where(feature => !SupportedRuntimeFeatures.Contains(feature))
            .ToArray();
        if (unsupportedRuntimeFeatures.Length != 0)
            throw new ExtensionSdkValidationException(
                $"This extension requires runtime features that Allstarr does not support yet: {string.Join(", ", unsupportedRuntimeFeatures)}.");
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var reviews = await db.ExtensionPermissionReviews.AsNoTracking()
            .Where(item => item.ExtensionPackageId == package.Id)
            .ToListAsync(cancellationToken);
        if (reviews.Any(item => item.Decision == ExtensionPermissionDecision.Pending) ||
            reviews.Any(item => item.Required && item.Decision != ExtensionPermissionDecision.Approved))
            throw new InvalidOperationException("Extension permissions have not been approved for activation.");
        var approved = reviews.Where(item => item.Decision == ExtensionPermissionDecision.Approved).ToArray();
        var approvedKeys = approved.Select(item => (item.PermissionKind, item.PermissionValue)).ToHashSet();
        var runtimeManifest = manifest with
        {
            Permissions = manifest.Permissions.Where(item =>
                approvedKeys.Contains((item.Kind.ToString().ToLowerInvariant(), item.Value))).ToArray()
        };
        var networkPermissions = approved.Where(item => item.PermissionKind == "network")
            .Select(item => item.PermissionValue).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var origins = networkPermissions.Where(item => !item.Contains('*'))
            .Select(item => new Uri(item)).ToArray();
        var cacheKeys = approved.Where(item => item.PermissionKind == "cache")
            .Select(item => item.PermissionValue).ToHashSet(StringComparer.Ordinal);
        var secretKeys = approved.Where(item => item.PermissionKind == "secret")
            .Select(item => item.PermissionValue).ToHashSet(StringComparer.Ordinal);
        var permissions = new ExtensionRuntimePermissionSet(
            networkPermissions,
            cacheKeys, secretKeys, ExtensionInvocationSecretScope.Resolve,
            (level, message) => _controlPlane.WriteLogAsync(package.Id, level, "runtime.log", message,
                "extension-runtime", CancellationToken.None).GetAwaiter().GetResult(),
            (manifest.Settings ?? []).Select(setting => setting.Key).ToHashSet(StringComparer.Ordinal));
        var sandbox = new ExtensionSandbox(package.PackagePath, package.ManifestJson,
            await File.ReadAllTextAsync(Path.Combine(package.PackagePath, manifest.EntryPoint), cancellationToken),
            _clients, _logger, permissions, Path.Combine(_runtimeRoot, manifest.Id),
            _sessionProtector);
        _sandboxes[package.Id] = sandbox;
        if (manifest.Capabilities.SelectMany(item => item.Hooks).Any(hook => !sandbox.HasCallableHook(hook)))
            throw new ExtensionSdkValidationException("The extension does not implement every declared SDK hook.");
        var implementations = manifest.Capabilities.Select(capability => (IProviderCapability)(capability.Kind switch
        {
            ProviderCapabilityKind.Metadata => new ExtensionMetadataCapabilityAdapter(sandbox, runtimeManifest, _secrets),
            ProviderCapabilityKind.Streaming => new ExtensionStreamingCapabilityAdapter(sandbox, runtimeManifest, _secrets),
            ProviderCapabilityKind.Download => new ExtensionDownloadCapabilityAdapter(
                sandbox, runtimeManifest, _secrets, _downloadArtifacts, _downloadOptions),
            ProviderCapabilityKind.Playlist => new ExtensionPlaylistCapabilityAdapter(sandbox, runtimeManifest, _secrets),
            ProviderCapabilityKind.Lyrics => new ExtensionLyricsCapabilityAdapter(sandbox, runtimeManifest, _secrets),
            ProviderCapabilityKind.Health => new ExtensionHealthCapabilityAdapter(sandbox, runtimeManifest, _secrets),
            _ => throw new ExtensionSdkValidationException("Unsupported extension capability.")
        })).ToArray();
        var declaredSettings = manifest.Settings ?? [];
        var settings = declaredSettings.Select(setting => new ProviderSettingDescriptor(
            setting.Key,
            SettingKind(setting),
            ProviderSettingScope.ProviderAccount,
            setting.Label,
            setting.Required,
            SettingKind(setting) == ProviderSettingValueKind.Choice ? setting.Choices : null,
            setting.Description,
            setting.DefaultJson))
            .Concat(secretKeys
                .Where(key => declaredSettings.All(setting => !setting.Key.Equals(key, StringComparison.Ordinal)))
                .Select(key => new ProviderSettingDescriptor(
                    key,
                    ProviderSettingValueKind.Secret,
                    ProviderSettingScope.ProviderAccount,
                    key,
                    approved.Any(item => item.PermissionKind == "secret" &&
                                         item.PermissionValue == key && item.Required))))
            .ToArray();
        var branding = manifest.IconPath == null ? null : new ProviderBrandingDescriptor(manifest.IconPath, manifest.Author);
        var descriptor = new ProviderDescriptor(manifest.Id, manifest.DisplayName,
            manifest.Description ?? $"{manifest.DisplayName} extension provider", ProviderOrigin.Extension, manifest.SdkVersion, "1.0",
            manifest.Capabilities.Select(item => new ProviderCapabilityDescriptor(item.Kind,
                ProviderCapabilitySupportState.Supported,
                item.AccountRequired
                    ? ProviderAccountRequirement.Required
                    : item.AccountScopes.Count > 0
                        ? ProviderAccountRequirement.Optional
                        : ProviderAccountRequirement.None,
                "1.0", item.Hooks, item.AccountScopes)),
            new ProviderPermissionDescriptor(origins, cacheKeys.Count != 0, secretKeys),
            settings,
            branding,
            entryPoint: manifest.EntryPoint,
            healthProbe: manifest.Capabilities.Any(item => item.Kind == ProviderCapabilityKind.Health && item.Hooks.Count > 0));
        return new ProviderRegistration(descriptor, implementations);
    }

    private static readonly HashSet<string> SupportedRuntimeFeatures = new(StringComparer.OrdinalIgnoreCase)
    {
        "fetch@1",
        "file@1",
        "storage@1",
        "signedSession@1",
        "sessionGrant@1"
    };

    public object SignedSessionStatus(Guid packageId) => RequireSandbox(packageId).SignedSessionStatus();
    public object StartSignedSessionVerification(Guid packageId) => RequireSandbox(packageId).StartSignedSessionVerification();
    public object CompleteSignedSessionGrant(Guid packageId, string grant) => RequireSandbox(packageId).CompleteSignedSessionGrant(grant);
    public object ClearSignedSession(Guid packageId) => RequireSandbox(packageId).ClearSignedSession();

    private ExtensionSandbox RequireSandbox(Guid packageId)
    {
        if (!_sandboxes.TryGetValue(packageId, out var sandbox))
            throw new KeyNotFoundException("The extension is not active in the runtime.");
        if (!sandbox.HasSignedSession)
            throw new InvalidOperationException("This extension does not use session authorization.");
        return sandbox;
    }

    private static ProviderSettingValueKind SettingKind(ExtensionSdkSetting setting)
    {
        if (setting.Sensitive) return ProviderSettingValueKind.Secret;
        if ((setting.Choices?.Count ?? 0) > 0 || setting.InputType is "select" or "choice")
            return ProviderSettingValueKind.Choice;
        return setting.InputType switch
        {
            "boolean" or "bool" or "toggle" or "checkbox" => ProviderSettingValueKind.Boolean,
            "integer" or "int" or "number" => ProviderSettingValueKind.Integer,
            _ => ProviderSettingValueKind.Text
        };
    }

    private async Task<ExtensionPackageRecord> GetPackageAsync(Guid packageId, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        return await db.ExtensionPackages.AsNoTracking().SingleOrDefaultAsync(item => item.Id == packageId, cancellationToken)
               ?? throw new KeyNotFoundException("Extension package not found.");
    }

    private void RejectBuiltInCollision(string extensionId, ExtensionPackageRecord package)
    {
        if (_readRegistry.TryGet(extensionId, out var current) && current!.Origin != ProviderOrigin.Extension &&
            !_firstPartyPolicy.IsApprovedReplacement(package))
            throw new ExtensionSdkValidationException("An extension cannot replace a built-in provider.");
    }

    private void RegisterVerified(ProviderRegistration registration, ExtensionPackageRecord package)
    {
        if (_readRegistry.TryGet(registration.Descriptor.Id, out var current) && current!.Origin != ProviderOrigin.Extension)
        {
            if (!_firstPartyPolicy.IsApprovedReplacement(package))
                throw new ExtensionSdkValidationException("An extension cannot replace a built-in provider.");
            _registry.RegisterOrReplaceFirstPartyExtension(registration);
            return;
        }
        _registry.RegisterOrReplaceExtension(registration);
    }
}

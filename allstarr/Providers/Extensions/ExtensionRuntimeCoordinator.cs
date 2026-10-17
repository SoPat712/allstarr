using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Spotify;
using allstarr.Core.Storage;
using allstarr.Core.Downloads;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using System.Collections.Concurrent;
using System.Text.Json;

namespace allstarr.Core.Extensions;

public sealed class ExtensionRuntimeCoordinator : IHostedService
{
    private const long MaximumStorageBytesPerExtension = 4L * 1024 * 1024;
    private readonly IDbContextFactory<AllstarrDbContext> _factory;
    private readonly ExtensionControlPlaneService _controlPlane;
    private readonly IDynamicProviderRegistry _registry;
    private readonly IProviderRegistry _readRegistry;
    private readonly IHttpClientFactory _clients;
    private readonly IProviderAccountSecretAccessor _secrets;
    private readonly ILogger<ExtensionRuntimeCoordinator> _logger;
    private readonly ProviderDownloadArtifactResolver _downloadArtifacts;
    private readonly ProviderDownloadWorkspaceOptions _downloadOptions;
    private readonly ExtensionExecutionOptions _executionOptions;
    private readonly string _runtimeRoot;
    private readonly TimeProvider _timeProvider;
    private readonly string _packageRoot;
    private readonly IDataProtector _sessionProtector;
    private readonly ConcurrentDictionary<Guid, ExtensionSandbox> _sandboxes = new();
    private readonly object _runtimeMutationLock = new();
    private readonly SemaphoreSlim _runtimeGate = new(1, 1);
    private readonly SemaphoreSlim _sessionAccountGate = new(1, 1);
    private const string SessionSourceSetting = "__extensionSessionSource";
    private readonly allstarr.Services.Common.IProviderCheckRequests? _checkRequests;
    private int _startupRenewalAttempted;

    public ExtensionRuntimeCoordinator(
        IDbContextFactory<AllstarrDbContext> factory,
        ExtensionControlPlaneService controlPlane,
        IDynamicProviderRegistry registry,
        IProviderRegistry readRegistry,
        IHttpClientFactory clients,
        IProviderAccountSecretAccessor secrets,
        ProviderDownloadArtifactResolver downloadArtifacts,
        ProviderDownloadWorkspaceOptions downloadOptions,
        IConfiguration configuration,
        ILogger<ExtensionRuntimeCoordinator> logger,
        IDataProtectionProvider? dataProtectionProvider = null,
        allstarr.Services.Common.IProviderCheckRequests? checkRequests = null,
        TimeProvider? timeProvider = null)
    {
        _executionOptions = configuration.GetSection(ExtensionExecutionOptions.SectionName)
            .Get<ExtensionExecutionOptions>() ?? new();
        _executionOptions.GetSignedDownloadRequestTimeout();
        _checkRequests = checkRequests;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _factory = factory;
        _controlPlane = controlPlane;
        _registry = registry;
        _readRegistry = readRegistry;
        _clients = clients;
        _secrets = new ExtensionAccountValues(factory, secrets);
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
        await WithRuntimeGateAsync(async () =>
        {
            await _controlPlane.ReconcileSupersededPackagesAsync(cancellationToken);
            return true;
        }, cancellationToken);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var active = await db.ExtensionPackages.AsNoTracking()
            .Where(item => item.State == ExtensionPackageState.Active)
            .OrderBy(item => item.ExtensionId)
            .ToListAsync(cancellationToken);
        foreach (var package in active)
        {
            try
            {
                var build = await BuildRegistrationAsync(package, cancellationToken);
                await WithRuntimeGateAsync(() =>
                {
                    RegisterRuntime(package, build);
                    return Task.FromResult(true);
                }, cancellationToken);
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

    public Task RefreshSignedSessionsAsync(CancellationToken cancellationToken = default)
    {
        var firstInvocation = Interlocked.Exchange(ref _startupRenewalAttempted, 1) == 0;
        return firstInvocation
            ? ExtensionSessionStartupRetry.RunAsync(RefreshSignedSessionsOnceAsync, _timeProvider, cancellationToken)
            : RefreshSignedSessionsOnceAsync(cancellationToken);
    }

    private async Task<bool> RefreshSignedSessionsOnceAsync(CancellationToken cancellationToken)
    {
        var networkNotReady = false;
        foreach (var packageId in _sandboxes.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                networkNotReady |= await WithRuntimeGateAsync(async () =>
                {
                    var package = await GetPackageAsync(packageId, cancellationToken);
                    if (package.State != ExtensionPackageState.Active ||
                        !_sandboxes.TryGetValue(packageId, out var sandbox) || !sandbox.HasSignedSession)
                        return false;
                    var packageNetworkNotReady = false;
                    await using var db = await _factory.CreateDbContextAsync(cancellationToken);
                    var accounts = await db.ProviderAccounts.AsNoTracking()
                        .Where(item => item.ProviderId == package.ExtensionId && item.Enabled &&
                            (item.OwnerUserId == null || db.Users.Any(user => user.Id == item.OwnerUserId && user.Enabled)))
                        .ToListAsync(cancellationToken);
                    foreach (var account in accounts.Where(item => !ProviderAccountSettings.IsRemoved(item)))
                    {
                        using var scope = sandbox.OpenSignedSessionAccount(account.Id);
                        var view = await sandbox.RefreshSignedSessionIfDueAsync(cancellationToken);
                        if (view == null) continue;
                        packageNetworkNotReady |= view.LastRenewalFailure == "network_not_ready";
                        if (view.Restored) _checkRequests?.Request(package.ExtensionId);
                        var (level, code, text) = RenewalLog(view);
                        await _controlPlane.WriteLogAsync(packageId, level, code, text, "extension-session", cancellationToken);
                    }
                    return packageNetworkNotReady;
                }, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                // Do not include transport details or session material in diagnostics.
                _logger.LogWarning("An extension session renewal check could not complete; it will be retried next tick");
            }
        }
        return networkNotReady;
    }

    public async Task<ExtensionPackageRecord> ActivateAsync(
        Guid packageId, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var package = await GetPackageAsync(packageId, cancellationToken);
        RuntimeBuild build;
        try
        {
            build = await BuildRegistrationAsync(package, cancellationToken);
            RejectBuiltInCollision(build.Registration.Descriptor.Id);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await _controlPlane.AbandonStagingAsync(packageId, expectedRevision, CancellationToken.None);
            throw;
        }
        catch
        {
            await _controlPlane.FailStagingAsync(
                packageId, expectedRevision, "runtime_validation_failed", CancellationToken.None);
            throw;
        }
        return await WithRuntimeGateAsync(async () =>
        {
            var active = await _controlPlane.ActivateAsync(packageId, expectedRevision, cancellationToken);
            RegisterRuntime(package, build);
            return active;
        }, cancellationToken);
    }

    public Task DisableAsync(Guid packageId, long expectedRevision, CancellationToken cancellationToken = default) =>
        WithRuntimeGateAsync(async () =>
        {
            var package = await GetPackageAsync(packageId, cancellationToken);
            await _controlPlane.DisableAsync(packageId, expectedRevision, cancellationToken);
            RemoveRuntime(package);
            return true;
        }, cancellationToken);

    public Task<ExtensionPackageRecord> ResetPermissionsForReviewAsync(
        Guid packageId,
        long expectedRevision,
        CancellationToken cancellationToken = default) => WithRuntimeGateAsync(async () =>
        {
            var package = await GetPackageAsync(packageId, cancellationToken);
            var reset = await _controlPlane.ResetPermissionsForReviewAsync(
                packageId, expectedRevision, cancellationToken);
            RemoveRuntime(package);
            return reset;
        }, cancellationToken);

    public Task<ExtensionPackageRecord> UninstallAsync(
        Guid packageId, long expectedRevision,
        CancellationToken cancellationToken = default) => WithRuntimeGateAsync(async () =>
        {
            var package = await GetPackageAsync(packageId, cancellationToken);
            var uninstalled = await _controlPlane.UninstallAsync(packageId, expectedRevision, cancellationToken);
            RemoveRuntime(package);
            try
            {
                var packagePath = ContainedPath(_packageRoot, package.PackagePath);
                if (Directory.Exists(packagePath)) Directory.Delete(packagePath, recursive: true);
                if (await RetiredRuntimeStateAsync(package, cancellationToken) is { } runtimeState &&
                    Directory.Exists(runtimeState))
                    Directory.Delete(runtimeState, recursive: true);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Uninstalled extension content requires filesystem cleanup for {ExtensionId}", package.ExtensionId);
                await _controlPlane.WriteLogAsync(package.Id, "warning", "package.cleanup-pending",
                    "Package state is uninstalled but its staged directory still requires cleanup.",
                    "extension-uninstall", cancellationToken);
            }
            return uninstalled;
        }, cancellationToken);

    private async Task<string?> RetiredRuntimeStateAsync(ExtensionPackageRecord package, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var remainingSources = await db.ExtensionPackages.AsNoTracking()
            .Where(item => item.Id != package.Id && item.ExtensionId == package.ExtensionId &&
                           item.State != ExtensionPackageState.Uninstalled && item.State != ExtensionPackageState.RolledBack)
            .Select(item => item.RegistryId)
            .ToListAsync(cancellationToken);
        if (remainingSources.Count == 0) return ContainedPath(_runtimeRoot, Path.Combine(_runtimeRoot, package.ExtensionId));
        return remainingSources.Contains(package.RegistryId)
            ? null
            : SessionStateDirectory(package.ExtensionId, package.RegistryId);
    }

    private string SessionStateDirectory(string extensionId, Guid? registryId) => ContainedPath(_runtimeRoot,
        Path.Combine(_runtimeRoot, extensionId, "sessions", registryId?.ToString("N") ?? "local"));

    private static string ContainedPath(string root, string path)
    {
        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(root, fullPath);
        if (Path.IsPathRooted(relative) || relative is "" or "." or ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Extension cleanup path is outside its root.");
        return fullPath;
    }

    private async Task<T> WithRuntimeGateAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        await _runtimeGate.WaitAsync(cancellationToken);
        try { return await operation(); }
        finally { _runtimeGate.Release(); }
    }

    internal static void ValidateRuntimeFeatures(ExtensionSdkManifest manifest)
    {
        var unsupported = (manifest.RequiredRuntimeFeatures ?? [])
            .Where(feature => !SupportedRuntimeFeatures.Contains(feature)).ToArray();
        if (unsupported.Length != 0)
            throw new ExtensionSdkValidationException(
                $"This extension requires runtime features that Allstarr does not support yet: {string.Join(", ", unsupported)}.");
    }

    private async Task<RuntimeBuild> BuildRegistrationAsync(
        ExtensionPackageRecord package, CancellationToken cancellationToken)
    {
        var storedManifest = ExtensionSdkV1.ParseManifest(package.ManifestJson);
        var downloadBackedStreaming = SpotiFlacExtensionCompatibility.UsesDownloadBackedStreaming(storedManifest);
        var manifestJson = SpotiFlacExtensionCompatibility.EnsureDownloadStreamingCapability(
            package.ManifestJson);
        var manifest = ExtensionSdkV1.ParseManifest(manifestJson);
        if (!manifest.Id.Equals(package.ExtensionId, StringComparison.Ordinal) ||
            !manifest.Version.Equals(package.Version, StringComparison.Ordinal))
            throw new ExtensionSdkValidationException("Stored extension identity does not match its manifest.");
        var actualHash = ExtensionSdkV1.ComputePackageContentSha256(package.PackagePath);
        if (!actualHash.Equals(package.ContentSha256, StringComparison.OrdinalIgnoreCase))
            throw new ExtensionSdkValidationException("Extension package contents changed after staging.");
        ValidateRuntimeFeatures(manifest);
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
            (manifest.Settings ?? []).Select(setting => setting.Key).ToHashSet(StringComparer.Ordinal),
            (level, code, message) => _controlPlane.WriteLogAsync(package.Id, level, code, message,
                "extension-session", CancellationToken.None).GetAwaiter().GetResult());
        var sandbox = new ExtensionSandbox(package.PackagePath, manifestJson,
            await File.ReadAllTextAsync(Path.Combine(package.PackagePath, manifest.EntryPoint), cancellationToken),
            _clients, _logger, permissions, Path.Combine(_runtimeRoot, manifest.Id),
            _sessionProtector, SessionStateDirectory(manifest.Id, package.RegistryId),
            timeProvider: _timeProvider, executionOptions: _executionOptions);
        if (manifest.SignedSession != null)
        {
            var account = await EnsureDefaultSessionAccountAsync(package, manifest.DisplayName, cancellationToken);
            sandbox.BindSignedSessionAccounts(account.Id, SessionAccountIsCurrentAsync);
        }
        if (manifest.Capabilities.SelectMany(item => item.Hooks).Any(hook =>
                !(downloadBackedStreaming && hook is "getStreamLease" or "probeStream") &&
                !sandbox.HasCallableHook(hook)))
            throw new ExtensionSdkValidationException("The extension does not implement every declared SDK hook.");
        var implementations = manifest.Capabilities.Select(capability => (IProviderCapability)(capability.Kind switch
        {
            ProviderCapabilityKind.Metadata => new ExtensionMetadataCapabilityAdapter(sandbox, runtimeManifest, _secrets),
            ProviderCapabilityKind.Streaming when downloadBackedStreaming =>
                new ExtensionDownloadStreamingCapabilityAdapter(
                    sandbox, runtimeManifest, _secrets, _downloadArtifacts, _downloadOptions),
            ProviderCapabilityKind.Streaming => new ExtensionStreamingCapabilityAdapter(sandbox, runtimeManifest, _secrets),
            ProviderCapabilityKind.Download => new ExtensionDownloadCapabilityAdapter(
                sandbox, runtimeManifest, _secrets, _downloadArtifacts, _downloadOptions),
            ProviderCapabilityKind.Playlist => new ExtensionPlaylistCapabilityAdapter(sandbox, runtimeManifest, _secrets),
            ProviderCapabilityKind.Lyrics => new ExtensionLyricsCapabilityAdapter(sandbox, runtimeManifest, _secrets),
            ProviderCapabilityKind.Intelligence => new ExtensionIntelligenceCapabilityAdapter(sandbox, runtimeManifest, _secrets),
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
                item.AccountRequired || manifest.SignedSession != null
                    ? ProviderAccountRequirement.Required
                    : item.AccountScopes.Count > 0
                        ? ProviderAccountRequirement.Optional
                        : ProviderAccountRequirement.None,
                "1.0", item.Hooks, item.AccountScopes.Count > 0 || manifest.SignedSession != null
                    ? [ProviderAccountScope.Shared, ProviderAccountScope.Personal] : [])),
            new ProviderPermissionDescriptor(origins, cacheKeys.Count != 0, secretKeys),
            settings,
            branding,
            entryPoint: manifest.EntryPoint,
            healthProbe: manifest.Capabilities.Any(item => item.Kind == ProviderCapabilityKind.Health && item.Hooks.Count > 0))
        { UsesSignedSession = manifest.SignedSession != null };
        return new RuntimeBuild(
            new ProviderRegistration(descriptor, implementations),
            sandbox);
    }

    internal static readonly IReadOnlySet<string> SupportedRuntimeFeatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "fetch@1",
        "file@1",
        "storage@1",
        "signedSession@1",
        "signedSession@2",
        "signedSession@3",
        "patternedFileTransform@1",
        "downloadSegments@1",
        "sessionGrant@1",
        "preparedContext@1"
    };

    public async Task<ExtensionSessionView> SignedSessionStatusAsync(Guid packageId,
        CancellationToken cancellationToken = default, Guid? accountId = null)
    {
        var sandbox = RequireSandbox(packageId);
        using var scope = sandbox.OpenSignedSessionAccount(await ResolveSessionAccountIdAsync(packageId, accountId, cancellationToken));
        return await sandbox.SignedSessionStatusAsync(cancellationToken);
    }

    public async Task<ExtensionSessionView> StartSignedSessionVerificationAsync(
        Guid packageId, long expectedRevision, CancellationToken cancellationToken = default, Guid? accountId = null)
    {
        var sandbox = await RequireCurrentSandboxAsync(packageId, expectedRevision, cancellationToken);
        using var scope = sandbox.OpenSignedSessionAccount(await ResolveSessionAccountIdAsync(packageId, accountId, cancellationToken));
        var view = await sandbox.StartSignedSessionVerificationAsync(cancellationToken);
        await RequestChecksIfSignedInAsync(packageId, view, cancellationToken);
        await LogSessionAsync(packageId, view, view.State == ExtensionSessionStates.SignedIn
            ? "Sign-in restored without verification."
            : "Sign-in verification started.", "session.started");
        return view;
    }

    public async Task<ExtensionSessionView> CompleteSignedSessionGrantAsync(
        Guid packageId, long expectedRevision, string grant, CancellationToken cancellationToken = default, Guid? accountId = null)
    {
        var sandbox = await RequireCurrentSandboxAsync(packageId, expectedRevision, cancellationToken);
        using var scope = sandbox.OpenSignedSessionAccount(await ResolveSessionAccountIdAsync(packageId, accountId, cancellationToken));
        var view = await sandbox.CompleteSignedSessionGrantAsync(grant, cancellationToken);
        await RequestChecksIfSignedInAsync(packageId, view, cancellationToken);
        await LogSessionAsync(packageId, view, "Sign-in completed.", "session.granted");
        return view;
    }

    // A fresh sign-in is checked straight away instead of waiting for the next scheduled sweep.
    private async Task RequestChecksIfSignedInAsync(Guid packageId, ExtensionSessionView view, CancellationToken cancellationToken)
    {
        if (_checkRequests == null || view.State != ExtensionSessionStates.SignedIn) return;
        _checkRequests.Request((await GetPackageAsync(packageId, cancellationToken)).ExtensionId);
    }

    public async Task<ExtensionSessionView> ClearSignedSessionAsync(
        Guid packageId, long expectedRevision, CancellationToken cancellationToken = default, Guid? accountId = null)
    {
        var sandbox = await RequireCurrentSandboxAsync(packageId, expectedRevision, cancellationToken);
        using var scope = sandbox.OpenSignedSessionAccount(await ResolveSessionAccountIdAsync(packageId, accountId, cancellationToken));
        var view = await sandbox.ClearSignedSessionAsync(cancellationToken);
        await LogSessionAsync(packageId, view, "Sign-in cleared.", "session.cleared");
        return view;
    }

    internal static (string Level, string Code, string Message) RenewalLog(ExtensionSessionView view) => view switch
    {
        { Restored: true } => ("information", "session.restored", "Sign-in expired and was restored automatically."),
        { LastRenewalFailure: "renewal_capped" } => ("warning", "session.renewal-capped",
            $"The service will not extend this sign-in past {view.ExpiresAt}. Sign in again before then to avoid an interruption."),
        { LastRenewalFailure: "verification_required" } => ("warning", "session.verification-required",
            "The service requires sign-in verification. Open the extension's sign-in to verify again."),
        { LastRenewalFailure: { } failure } => ("warning", "session.renewal-failed",
            $"Automatic renewal failed ({failure}); the existing sign-in was retained."),
        _ => ("information", "session.renewed", $"Sign-in renewed automatically until {view.ExpiresAt}.")
    };

    private static bool IsSessionSource(ProviderAccountRecord account, string source)
    {
        try
        {
            using var settings = JsonDocument.Parse(account.SettingsJson);
            return settings.RootElement.ValueKind == JsonValueKind.Object &&
                settings.RootElement.TryGetProperty(SessionSourceSetting, out var value) &&
                value.ValueKind == JsonValueKind.String && value.GetString() == source;
        }
        catch (JsonException) { return false; }
    }

    private static bool HasSessionSource(ProviderAccountRecord account)
    {
        try
        {
            using var settings = JsonDocument.Parse(account.SettingsJson);
            return settings.RootElement.ValueKind != JsonValueKind.Object ||
                settings.RootElement.TryGetProperty(SessionSourceSetting, out _);
        }
        catch (JsonException) { return true; }
    }

    private async Task<ProviderAccountRecord> EnsureDefaultSessionAccountAsync(
        ExtensionPackageRecord package, string displayName, CancellationToken cancellationToken)
    {
        await _sessionAccountGate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken);
            var source = package.RegistryId?.ToString("N") ?? "local";
            var accounts = await db.ProviderAccounts.AsNoTracking()
                .Where(item => item.ProviderId == package.ExtensionId).ToListAsync(cancellationToken);
            var available = accounts.Where(item => !ProviderAccountSettings.IsRemoved(item)).ToArray();
            var existing = available.FirstOrDefault(item => IsSessionSource(item, source));
            if (existing != null) return existing;
            var legacyAccount = available
                .Where(item => !HasSessionSource(item))
                .OrderBy(item => item.CreatedAt).ThenBy(item => item.Id).FirstOrDefault();
            if (legacyAccount != null)
            {
                db.ProviderAccounts.Attach(legacyAccount);
                var settings = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(legacyAccount.SettingsJson) ?? [];
                settings[SessionSourceSetting] = JsonSerializer.SerializeToElement(source);
                legacyAccount.SettingsJson = JsonSerializer.Serialize(settings);
                legacyAccount.Revision++;
                legacyAccount.UpdatedAt = DateTimeOffset.UtcNow;
                db.Entry(legacyAccount).State = EntityState.Modified;
                await db.SaveChangesAsync(cancellationToken);
                return legacyAccount;
            }
            var now = DateTimeOffset.UtcNow;
            var account = new ProviderAccountRecord
            {
                Id = Guid.CreateVersion7(),
                ProviderId = package.ExtensionId,
                DisplayName = displayName,
                Enabled = true,
                Revision = 1,
                CreatedAt = now,
                UpdatedAt = now,
                SettingsJson = JsonSerializer.Serialize(new Dictionary<string, string> { [SessionSourceSetting] = source })
            };
            db.ProviderAccounts.Add(account);
            await db.SaveChangesAsync(cancellationToken);
            return account;
        }
        finally { _sessionAccountGate.Release(); }
    }

    private async Task<Guid> ResolveSessionAccountIdAsync(Guid packageId, Guid? accountId, CancellationToken cancellationToken)
    {
        var package = await GetPackageAsync(packageId, cancellationToken);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var accounts = await db.ProviderAccounts.AsNoTracking()
            .Where(item => item.ProviderId == package.ExtensionId).ToListAsync(cancellationToken);
        var available = accounts.Where(item => !ProviderAccountSettings.IsRemoved(item)).ToArray();
        var account = accountId.HasValue ? available.SingleOrDefault(item => item.Id == accountId)
            : available.FirstOrDefault(item => IsSessionSource(item, package.RegistryId?.ToString("N") ?? "local"));
        if (account == null) throw new KeyNotFoundException("The extension account was not found.");
        return account.Id;
    }

    private async Task<bool> SessionAccountIsCurrentAsync(ProviderAccountContext account, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var current = await db.ProviderAccounts.AsNoTracking().SingleOrDefaultAsync(item => item.Id == account.AccountId &&
            item.ProviderId == account.ProviderId && item.Enabled && item.Revision == account.Revision &&
            item.OwnerUserId == account.OwnerUserId && item.SecretReferenceId == account.SecretReferenceId &&
            (item.OwnerUserId == null || db.Users.Any(user => user.Id == item.OwnerUserId && user.Enabled)), cancellationToken);
        return current != null && !ProviderAccountSettings.IsRemoved(current);
    }

    private sealed class ExtensionAccountValues(
        IDbContextFactory<AllstarrDbContext> factory,
        IProviderAccountSecretAccessor secrets) : IProviderAccountSecretAccessor
    {
        public async Task<T> UseAsync<T>(ProviderAccountContext account, Func<ReadOnlyMemory<byte>, Task<T>> operation,
            CancellationToken cancellationToken)
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            var row = await db.ProviderAccounts.AsNoTracking().SingleOrDefaultAsync(item =>
                item.Id == account.AccountId && item.ProviderId == account.ProviderId && item.Enabled &&
                item.Revision == account.Revision && item.OwnerUserId == account.OwnerUserId &&
                item.SecretReferenceId == account.SecretReferenceId, cancellationToken)
                ?? throw new UnauthorizedAccessException("The extension account is no longer current.");
            // Keys starting with "__" are Allstarr's own bookkeeping, not extension settings.
            var values = (JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(row.SettingsJson) ?? [])
                .Where(item => !item.Key.StartsWith("__", StringComparison.Ordinal))
                .ToDictionary(item => item.Key, item => item.Value);
            if (account.SecretReferenceId == null)
                return await operation(JsonSerializer.SerializeToUtf8Bytes(values));
            return await secrets.UseAsync(account, async secret =>
            {
                using var document = JsonDocument.Parse(secret);
                foreach (var property in document.RootElement.EnumerateObject())
                    values[property.Name] = property.Value.Clone();
                return await operation(JsonSerializer.SerializeToUtf8Bytes(values));
            }, cancellationToken);
        }
    }

    private Task<ExtensionSandbox> RequireCurrentSandboxAsync(
        Guid packageId, long expectedRevision, CancellationToken cancellationToken) =>
        WithRuntimeGateAsync(async () =>
        {
            var package = await GetPackageAsync(packageId, cancellationToken);
            if (package.Revision != expectedRevision || package.State != ExtensionPackageState.Active)
                throw new DbUpdateConcurrencyException("The extension package changed before this session update.");
            return RequireSandbox(packageId);
        }, cancellationToken);

    private Task LogSessionAsync(Guid packageId, ExtensionSessionView view, string success, string eventCode) =>
        view.ReasonCode == null
            ? _controlPlane.WriteLogAsync(packageId, "information", eventCode, success, "extension-session")
            : _controlPlane.WriteLogAsync(packageId, "warning", "session.failed",
                $"Sign-in did not complete ({view.ReasonCode}).", "extension-session");

    public ExtensionStorageUsageSnapshot GetStorageUsage()
    {
        var usage = _sandboxes.Values.Select(item => item.StorageUsage()).ToArray();
        return new(
            usage.Length,
            usage.Sum(item => item.EntryCount),
            usage.Sum(item => item.PayloadBytes),
            usage.Length * MaximumStorageBytesPerExtension);
    }

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

    private void RejectBuiltInCollision(string extensionId)
    {
        if (_readRegistry.TryGet(extensionId, out var current) && current!.Origin != ProviderOrigin.Extension)
            throw new ExtensionSdkValidationException("An extension cannot replace a built-in provider.");
    }

    private void RegisterRuntime(ExtensionPackageRecord package, RuntimeBuild build)
    {
        lock (_runtimeMutationLock)
        {
            RejectBuiltInCollision(build.Registration.Descriptor.Id);
            _registry.RegisterOrReplaceExtension(build.Registration);
            StoreSandbox(package.Id, package.ExtensionId, build.Sandbox);
        }
    }

    private void RemoveRuntime(ExtensionPackageRecord package)
    {
        lock (_runtimeMutationLock)
        {
            if (_sandboxes.TryRemove(package.Id, out _))
                _registry.RemoveExtension(package.ExtensionId);
        }
    }

    private void StoreSandbox(
        Guid packageId,
        string extensionId,
        ExtensionSandbox sandbox)
    {
        foreach (var item in _sandboxes
                     .Where(item =>
                         item.Key != packageId &&
                         item.Value.Id.Equals(extensionId, StringComparison.Ordinal))
                     .ToArray())
        {
            _sandboxes.TryRemove(item.Key, out _);
        }

        _sandboxes[packageId] = sandbox;
    }

    private sealed record RuntimeBuild(
        ProviderRegistration Registration,
        ExtensionSandbox Sandbox);
}

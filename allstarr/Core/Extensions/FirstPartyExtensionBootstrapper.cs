using allstarr.Core.Storage;

namespace allstarr.Core.Extensions;

public sealed class FirstPartyExtensionBootstrapper : IHostedService
{
    private readonly FirstPartyExtensionPolicy _policy;
    private readonly ExtensionControlPlaneService _controlPlane;
    private readonly ILogger<FirstPartyExtensionBootstrapper> _logger;
    private readonly bool _enabled;
    private readonly string _packageRoot;

    public FirstPartyExtensionBootstrapper(FirstPartyExtensionPolicy policy,
        ExtensionControlPlaneService controlPlane, IConfiguration configuration,
        ILogger<FirstPartyExtensionBootstrapper> logger)
    {
        _policy = policy;
        _controlPlane = controlPlane;
        _logger = logger;
        _enabled = configuration.GetValue<bool>("Extensions:BootstrapFirstPartyBundle");
        _packageRoot = Path.GetFullPath(configuration["Extensions:Directory"] ??
                                        Path.Combine(Directory.GetCurrentDirectory(), "extensions"));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_enabled) return;
        foreach (var locked in _policy.ReadyPackages())
        {
            var existing = await _controlPlane.ListPackagesAsync(locked.Id, cancellationToken);
            if (existing.Any(item => item.Version == locked.Version &&
                                     item.Sha256.Equals(locked.ArchiveSha256, StringComparison.OrdinalIgnoreCase) &&
                                     item.State != ExtensionPackageState.Uninstalled)) continue;
            var extractionRoot = Path.GetFullPath(Path.Combine(_packageRoot, ".first-party", locked.Id,
                $"{locked.Version}-{locked.ArchiveSha256[..12]}"));
            var relative = Path.GetRelativePath(_packageRoot, extractionRoot);
            if (Path.IsPathRooted(relative) || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new ExtensionSdkValidationException("A first-party extraction path is outside the extension package root.");
            if (Directory.Exists(extractionRoot)) Directory.Delete(extractionRoot, recursive: true);
            var verified = ExtensionSdkV1.VerifyArchive(
                _policy.ResolveArchivePath(locked), locked.ArchiveSha256, extractionRoot);
            if (!verified.Manifest.Id.Equals(locked.Id, StringComparison.Ordinal) ||
                !verified.Manifest.Version.Equals(locked.Version, StringComparison.Ordinal) ||
                !verified.ContentSha256.Equals(locked.ContentSha256, StringComparison.OrdinalIgnoreCase))
            {
                Directory.Delete(extractionRoot, recursive: true);
                throw new ExtensionSdkValidationException("A bundled first-party package does not match its immutable lock entry.");
            }
            var staged = await _controlPlane.StageAsync(verified, cancellationToken: cancellationToken);
            _logger.LogInformation("Staged locked first-party extension {ExtensionId} {Version} in state {State}",
                staged.ExtensionId, staged.Version, staged.State);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

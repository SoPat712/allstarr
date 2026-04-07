using allstarr.Models.Settings;
using allstarr.Services.Spotify;
using Microsoft.Extensions.Options;

namespace allstarr.Services.Common;

/// <summary>
/// Triggers a one-time full rebuild when the app is upgraded across major/minor versions.
/// </summary>
public class VersionUpgradeRebuildService : IHostedService
{
    private const string VersionStateFile = "/app/cache/version-state.txt";

    private readonly SpotifyTrackMatchingService _matchingService;
    private readonly SpotifyImportSettings _spotifyImportSettings;
    private readonly ILogger<VersionUpgradeRebuildService> _logger;
    private CancellationTokenSource? _backgroundRebuildCts;
    private Task? _backgroundRebuildTask;

    public VersionUpgradeRebuildService(
        SpotifyTrackMatchingService matchingService,
        IOptions<SpotifyImportSettings> spotifyImportSettings,
        ILogger<VersionUpgradeRebuildService> logger)
    {
        _matchingService = matchingService;
        _spotifyImportSettings = spotifyImportSettings.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var currentVersion = AppVersion.Version;
        var previousVersion = await ReadPreviousVersionAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(previousVersion))
        {
            _logger.LogInformation("No prior version state found, saving current version {Version}", currentVersion);
            await WriteCurrentVersionAsync(currentVersion, cancellationToken);
            return;
        }

        if (VersionUpgradePolicy.ShouldTriggerRebuild(previousVersion, currentVersion, out var reason))
        {
            _logger.LogInformation(
                "Detected {Reason}: {PreviousVersion} -> {CurrentVersion}",
                reason, previousVersion, currentVersion);

            if (!_spotifyImportSettings.Enabled)
            {
                _logger.LogInformation("Skipping auto rebuild: Spotify import is disabled");
            }
            else if (_spotifyImportSettings.Playlists.Count == 0)
            {
                _logger.LogInformation("Skipping auto rebuild: no Spotify playlists are configured");
            }
            else
            {
                _logger.LogInformation(
                    "Scheduling full rebuild for all playlists in background after version upgrade");

                _backgroundRebuildCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _backgroundRebuildTask = RunBackgroundRebuildAsync(currentVersion, _backgroundRebuildCts.Token);
                return;
            }
        }
        else
        {
            _logger.LogDebug(
                "Version upgrade check did not require rebuild: {PreviousVersion} -> {CurrentVersion} ({Reason})",
                previousVersion, currentVersion, reason);
        }

        await WriteCurrentVersionAsync(currentVersion, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return StopBackgroundRebuildAsync(cancellationToken);
    }

    private async Task RunBackgroundRebuildAsync(string currentVersion, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Starting background full rebuild for all playlists after version upgrade");
            await _matchingService.TriggerRebuildAllAsync(cancellationToken);
            _logger.LogInformation("Background full rebuild after version upgrade completed");
            await WriteCurrentVersionAsync(currentVersion, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Background full rebuild after version upgrade was cancelled before completion");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to trigger auto rebuild after version upgrade");
            await WriteCurrentVersionAsync(currentVersion, CancellationToken.None);
        }
    }

    private async Task StopBackgroundRebuildAsync(CancellationToken cancellationToken)
    {
        if (_backgroundRebuildTask == null)
        {
            return;
        }

        try
        {
            _backgroundRebuildCts?.Cancel();
            await _backgroundRebuildTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Host shutdown is in progress or the background task observed cancellation.
        }
        finally
        {
            _backgroundRebuildCts?.Dispose();
            _backgroundRebuildCts = null;
            _backgroundRebuildTask = null;
        }
    }

    private async Task<string?> ReadPreviousVersionAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(VersionStateFile))
            {
                return null;
            }

            return (await File.ReadAllTextAsync(VersionStateFile, cancellationToken)).Trim();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read version state file: {Path}", VersionStateFile);
            return null;
        }
    }

    private async Task WriteCurrentVersionAsync(string version, CancellationToken cancellationToken)
    {
        try
        {
            var directory = Path.GetDirectoryName(VersionStateFile);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(VersionStateFile, version, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write version state file: {Path}", VersionStateFile);
        }
    }
}

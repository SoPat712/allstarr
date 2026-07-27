using System.Security.Cryptography;
using allstarr.Core.Secrets;
using allstarr.Core.Storage;

namespace allstarr.Core.Operations;

public sealed class ReadinessOptions
{
    public const string SectionName = "Readiness";

    public bool RequireSecretKeyRing { get; set; }
    public long MinimumFreeBytes { get; set; } = 16 * 1024 * 1024;
    public List<string> RequiredDirectories { get; set; } = [];
}

public sealed record ReadinessComponent(string Id, string State, bool Required, string? ErrorCode = null);

public sealed record PlatformReadinessSnapshot(
    bool Ready,
    string Status,
    IReadOnlyList<ReadinessComponent> Components,
    DateTimeOffset CheckedAt);

public sealed class PlatformReadinessService
{
    private readonly DurableStorageState _storageState;
    private readonly ReadinessOptions _options;
    private readonly FileSecretKeyRingProvider _keyRingProvider;
    private readonly SidecarStatusCatalog _sidecars;
    private readonly IDurableStorageRuntimeProbe _storageProbe;

    public PlatformReadinessService(
        DurableStorageState storageState,
        ReadinessOptions options,
        FileSecretKeyRingProvider keyRingProvider,
        SidecarStatusCatalog sidecars,
        IDurableStorageRuntimeProbe storageProbe)
    {
        _storageState = storageState;
        _options = options;
        _keyRingProvider = keyRingProvider;
        _sidecars = sidecars;
        _storageProbe = storageProbe;
    }

    public async Task<PlatformReadinessSnapshot> CheckAsync(
        CancellationToken cancellationToken = default)
    {
        var components = new List<ReadinessComponent>();
        await _storageProbe.CheckAsync(cancellationToken);
        var storage = _storageState.GetSnapshot();
        components.Add(new ReadinessComponent(
            $"storage:{storage.Provider.ToString().ToLowerInvariant()}",
            storage.Readiness.ToString().ToLowerInvariant(),
            true,
            storage.ErrorCode));

        var directories = _options.RequiredDirectories
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        for (var index = 0; index < directories.Count; index++)
        {
            var result = CheckDirectory(directories[index]);
            components.Add(new ReadinessComponent(
                $"directory:{index}",
                result.Ready ? "ready" : "unavailable",
                true,
                result.ErrorCode));
        }

        if (_options.RequireSecretKeyRing)
        {
            try
            {
                var keyRing = await _keyRingProvider.LoadAsync(cancellationToken);
                foreach (var key in keyRing.Keys.Values)
                {
                    CryptographicOperations.ZeroMemory(key);
                }

                components.Add(new ReadinessComponent("secret-key-ring", "ready", true));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                components.Add(new ReadinessComponent(
                    "secret-key-ring",
                    "unavailable",
                    true,
                    "secret_key_ring_unavailable"));
            }
        }

        components.AddRange(_sidecars.GetAll().Select(sidecar => new ReadinessComponent(
            $"sidecar:{sidecar.Id}",
            SidecarState(sidecar.State),
            sidecar.Required,
            sidecar.ErrorCode)));
        var ready = components.All(component =>
            !component.Required || component.State == "ready");
        return new PlatformReadinessSnapshot(
            ready,
            ready ? "ready" : "not_ready",
            components,
            DateTimeOffset.UtcNow);
    }

    private static string SidecarState(SidecarRuntimeState state) => state switch
    {
        SidecarRuntimeState.NotInstalled => "not_installed",
        SidecarRuntimeState.NeedsConfiguration => "needs_configuration",
        _ => state.ToString().ToLowerInvariant()
    };

    private (bool Ready, string? ErrorCode) CheckDirectory(string configuredPath)
    {
        string path;
        try
        {
            path = Path.GetFullPath(configuredPath);
        }
        catch
        {
            return (false, "required_directory_invalid");
        }

        if (!Directory.Exists(path))
        {
            return (false, "required_directory_missing");
        }

        var probe = Path.Combine(path, $".allstarr-write-probe-{Guid.NewGuid():N}");
        try
        {
            using (new FileStream(
                       probe,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 1,
                       FileOptions.DeleteOnClose))
            {
            }
        }
        catch
        {
            return (false, "required_directory_not_writable");
        }
        finally
        {
            try
            {
                File.Delete(probe);
            }
            catch
            {
            }
        }

        try
        {
            var drive = DriveInfo.GetDrives()
                .Where(item => path.StartsWith(item.RootDirectory.FullName, StringComparison.Ordinal))
                .OrderByDescending(item => item.RootDirectory.FullName.Length)
                .FirstOrDefault();
            if (drive is { IsReady: true } && drive.AvailableFreeSpace < _options.MinimumFreeBytes)
            {
                return (false, "required_directory_space_low");
            }
        }
        catch
        {
            return (false, "required_directory_space_unknown");
        }

        return (true, null);
    }
}

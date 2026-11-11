using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using allstarr.Core.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace allstarr.Core.ClientApi;

public sealed record KaraokeManifest(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes,
    [property: JsonPropertyName("computeUnits")] string ComputeUnits,
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("license")] string License,
    [property: JsonPropertyName("source")] string? Source = null);

public sealed record ValidatedKaraokeModel(
    string Version,
    string Sha256,
    long SizeBytes,
    string ComputeUnits,
    string Format,
    string License,
    string FileName,
    string PhysicalPath,
    DateTime ManifestLastWriteTimeUtc);

public interface IKaraokeModelService
{
    bool IsAvailable { get; }
    KaraokeModelMetadata? GetCurrentModelMetadata();
    ValidatedKaraokeModel? GetCurrentModel();
    ValidatedKaraokeModel? GetModel(string version);
    Task ScanAsync(CancellationToken cancellationToken = default);
}

public sealed class KaraokeModelService : IKaraokeModelService, IHostedService, IDisposable
{
    private readonly string _modelsDirectory;
    private readonly ILogger<KaraokeModelService> _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private FileSystemWatcher? _watcher;
    private long _lastMtimeFingerprint = -1;
    private string? _lastLoggedReason;
    private ValidatedKaraokeModel? _activeModel;
    private Dictionary<string, ValidatedKaraokeModel> _validatedModels = new(StringComparer.Ordinal);
    private bool _initialScanCompleted;
    private bool _disposed;
    private int _hashedFiles;

    /// <summary>How many model files have been hashed since startup; requests must never add to it.</summary>
    internal int HashedFileCount => Volatile.Read(ref _hashedFiles);

    public KaraokeModelService(StorageOptions storageOptions, ILogger<KaraokeModelService> logger)
    {
        _logger = logger;
        _modelsDirectory = Path.Combine(Path.GetFullPath(storageOptions.DataDirectory), "models", "karaoke");
    }

    public bool IsAvailable
    {
        get
        {
            CheckFolderMtime();
            return _activeModel != null;
        }
    }

    public KaraokeModelMetadata? GetCurrentModelMetadata()
    {
        CheckFolderMtime();
        var model = _activeModel;
        if (model == null) return null;
        return new KaraokeModelMetadata(
            model.Version,
            model.Sha256,
            model.SizeBytes,
            model.ComputeUnits,
            model.Format,
            $"/allstarr/v1/models/karaoke/{model.Version}/file",
            model.License);
    }

    public ValidatedKaraokeModel? GetCurrentModel()
    {
        CheckFolderMtime();
        return _activeModel;
    }

    public ValidatedKaraokeModel? GetModel(string version)
    {
        if (!IsSafePathSegment(version)) return null;
        CheckFolderMtime();
        if (_validatedModels.TryGetValue(version, out var model) && File.Exists(model.PhysicalPath))
        {
            return model;
        }
        return null;
    }

    public static bool IsSafePathSegment(string? segment)
    {
        if (string.IsNullOrWhiteSpace(segment)) return false;
        if (segment is "." or "..") return false;
        if (segment.Contains('/') || segment.Contains('\\')) return false;
        if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        if (segment.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;
        return true;
    }

    private void CheckFolderMtime()
    {
        if (!_initialScanCompleted || _disposed) return;
        var current = GetFolderMtimeFingerprint();
        if (current != _lastMtimeFingerprint)
        {
            _ = Task.Run(() =>
            {
                if (_disposed) return;
                try { _ = ScanAsync(_cts.Token); } catch { }
            });
        }
    }

    private long GetFolderMtimeFingerprint()
    {
        try
        {
            if (!Directory.Exists(_modelsDirectory)) return 0;
            var hash = new HashCode();
            hash.Add(Directory.GetLastWriteTimeUtc(_modelsDirectory).Ticks);
            foreach (var subDir in Directory.EnumerateDirectories(_modelsDirectory))
            {
                var dirInfo = new DirectoryInfo(subDir);
                hash.Add(dirInfo.Name);
                hash.Add(dirInfo.LastWriteTimeUtc.Ticks);
                var manifestPath = Path.Combine(subDir, "manifest.json");
                if (File.Exists(manifestPath))
                {
                    hash.Add(File.GetLastWriteTimeUtc(manifestPath).Ticks);
                    hash.Add(new FileInfo(manifestPath).Length);
                }
            }
            return hash.ToHashCode();
        }
        catch
        {
            return 0;
        }
    }

    public async Task ScanAsync(CancellationToken cancellationToken = default)
    {
        await _scanLock.WaitAsync(cancellationToken);
        try
        {
            EnsureWatcher();
            var newValidatedModels = new Dictionary<string, ValidatedKaraokeModel>(StringComparer.Ordinal);
            string? failureReason = null;

            if (!Directory.Exists(_modelsDirectory))
            {
                failureReason = "Karaoke models directory does not exist.";
            }
            else
            {
                var directories = Directory.EnumerateDirectories(_modelsDirectory).ToArray();
                if (directories.Length == 0)
                {
                    failureReason = "No karaoke model versions found.";
                }

                foreach (var dir in directories)
                {
                    var dirName = Path.GetFileName(dir);
                    if (!IsSafePathSegment(dirName))
                    {
                        failureReason = $"Directory '{dirName}' is not a safe version path segment.";
                        continue;
                    }

                    var manifestPath = Path.Combine(dir, "manifest.json");
                    if (!File.Exists(manifestPath))
                    {
                        failureReason = $"manifest.json missing in '{dirName}'.";
                        continue;
                    }

                    KaraokeManifest? manifest;
                    try
                    {
                        await using var stream = File.OpenRead(manifestPath);
                        manifest = await JsonSerializer.DeserializeAsync<KaraokeManifest>(stream, ClientApiContract.JsonOptions, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        failureReason = $"Failed to parse manifest.json in '{dirName}': {ex.Message}";
                        continue;
                    }

                    if (manifest == null ||
                        string.IsNullOrWhiteSpace(manifest.Version) ||
                        !IsSafePathSegment(manifest.Version) ||
                        !string.Equals(manifest.Version, dirName, StringComparison.Ordinal) ||
                        string.IsNullOrWhiteSpace(manifest.Sha256) ||
                        manifest.SizeBytes <= 0 ||
                        string.IsNullOrWhiteSpace(manifest.ComputeUnits) ||
                        string.IsNullOrWhiteSpace(manifest.File) ||
                        !IsSafePathSegment(manifest.File) ||
                        string.IsNullOrWhiteSpace(manifest.Format) ||
                        string.IsNullOrWhiteSpace(manifest.License))
                    {
                        failureReason = $"Invalid manifest.json content in '{dirName}'.";
                        continue;
                    }

                    var modelFilePath = Path.Combine(dir, manifest.File);
                    if (!File.Exists(modelFilePath))
                    {
                        failureReason = $"Model file '{manifest.File}' does not exist in '{dirName}'.";
                        continue;
                    }

                    var fileInfo = new FileInfo(modelFilePath);
                    if (fileInfo.Length != manifest.SizeBytes)
                    {
                        failureReason = $"Model file size mismatch in '{dirName}': expected {manifest.SizeBytes}, found {fileInfo.Length}.";
                        continue;
                    }

                    string actualSha256;
                    try
                    {
                        await using var fileStream = File.OpenRead(modelFilePath);
                        Interlocked.Increment(ref _hashedFiles);
                        var hashBytes = await SHA256.HashDataAsync(fileStream, cancellationToken);
                        actualSha256 = Convert.ToHexString(hashBytes).ToLowerInvariant();
                    }
                    catch (Exception ex)
                    {
                        failureReason = $"Failed to hash model file in '{dirName}': {ex.Message}";
                        continue;
                    }

                    if (!string.Equals(actualSha256, manifest.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        failureReason = $"Model file SHA-256 mismatch in '{dirName}': expected {manifest.Sha256}, calculated {actualSha256}.";
                        continue;
                    }

                    var manifestMtime = File.GetLastWriteTimeUtc(manifestPath);
                    newValidatedModels[manifest.Version] = new ValidatedKaraokeModel(
                        manifest.Version,
                        actualSha256,
                        manifest.SizeBytes,
                        manifest.ComputeUnits,
                        manifest.Format,
                        manifest.License,
                        manifest.File,
                        modelFilePath,
                        manifestMtime);
                }
            }

            _validatedModels = newValidatedModels;
            if (newValidatedModels.Count > 0)
            {
                _activeModel = newValidatedModels.Values.OrderByDescending(m => m.ManifestLastWriteTimeUtc).First();
                _lastLoggedReason = null;
                _logger.LogInformation("Karaoke model '{Version}' is available.", _activeModel.Version);
            }
            else
            {
                _activeModel = null;
                var reason = failureReason ?? "No valid karaoke model found.";
                if (!string.Equals(_lastLoggedReason, reason, StringComparison.Ordinal))
                {
                    _lastLoggedReason = reason;
                    _logger.LogInformation("Karaoke model is not available: {Reason}", reason);
                }
            }

            _lastMtimeFingerprint = GetFolderMtimeFingerprint();
            _initialScanCompleted = true;
        }
        finally
        {
            _scanLock.Release();
        }
    }

    private void EnsureWatcher()
    {
        if (_watcher != null) return;
        try
        {
            if (Directory.Exists(_modelsDirectory))
            {
                _watcher = new FileSystemWatcher(_modelsDirectory)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
                };
                _watcher.Created += OnFileSystemEvent;
                _watcher.Changed += OnFileSystemEvent;
                _watcher.Deleted += OnFileSystemEvent;
                _watcher.Renamed += OnFileSystemEvent;
                _watcher.EnableRaisingEvents = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not initialize FileSystemWatcher for karaoke models directory.");
        }
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
    {
        if (_disposed) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            if (_disposed) return;
            try { await ScanAsync(_cts.Token); }
            catch { }
        });
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(() => ScanAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_disposed)
        {
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        _cts.Dispose();
        _watcher?.Dispose();
        _scanLock.Dispose();
    }
}

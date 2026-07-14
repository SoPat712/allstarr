using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jint;
using Jint.Native;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using allstarr.Services.Admin;
using allstarr.Models.Domain;
using allstarr.Models.Search;
using allstarr.Models.Subsonic;
using allstarr.Core.Extensions;
using allstarr.Core.Storage;

namespace allstarr.Services.Common;

public class ExtensionManager
{
    private const string DisabledMarkerFile = ".disabled";
    private const int MaximumExtensionIdLength = 128;
    private const int MaximumRegistryBytes = 4 * 1024 * 1024;
    private static readonly Regex ExtensionIdPattern = new(
        "^[a-z0-9]+(?:-[a-z0-9]+)*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ExtensionManager> _logger;
    private readonly IConfiguration _configuration;
    private readonly AdminHelperService _adminHelperService;
    private readonly string _extensionsDir;
    private readonly ExtensionControlPlaneService? _controlPlane;

    private readonly ConcurrentDictionary<string, ExtensionSandbox> _activeExtensions = new();

    public ExtensionManager(
        IHttpClientFactory httpClientFactory,
        ILogger<ExtensionManager> logger,
        IConfiguration configuration,
        AdminHelperService adminHelperService,
        ExtensionControlPlaneService? controlPlane = null)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _configuration = configuration;
        _adminHelperService = adminHelperService;
        _controlPlane = controlPlane;
        _extensionsDir = Path.GetFullPath(
            _configuration["Extensions:Directory"] ??
            Path.Combine(Directory.GetCurrentDirectory(), "extensions"));

        if (!Directory.Exists(_extensionsDir))
        {
            Directory.CreateDirectory(_extensionsDir);
        }

    }

    public IReadOnlyCollection<ExtensionSandbox> GetActiveExtensions() => _activeExtensions.Values.ToList();

    public bool RemoteInstallEnabled =>
        _configuration.GetValue("Extensions:AllowRemoteInstall", false);

    public IReadOnlyCollection<InstalledExtensionInfo> GetInstalledExtensions()
    {
        if (!Directory.Exists(_extensionsDir))
        {
            return [];
        }

        return Directory.GetDirectories(_extensionsDir)
            .Select(ReadInstalledExtensionInfo)
            .Where(item => item != null)
            .Select(item => item!)
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public ExtensionSandbox? GetExtension(string id)
    {
        return TryValidateExtensionId(id, out var validId) &&
               _activeExtensions.TryGetValue(validId, out var sandbox)
            ? sandbox
            : null;
    }

    public List<string> GetConfiguredRepositories()
    {
        var repos = ReadExtensionRepositoriesFromEnvFile() ?? _configuration["EXTENSION_REPOSITORIES"];
        return ParseRepositoryList(repos);
    }

    public static List<string> ParseRepositoryList(string? repositories)
    {
        return string.IsNullOrWhiteSpace(repositories)
            ? []
            : repositories.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
    }

    private string? ReadExtensionRepositoriesFromEnvFile()
    {
        try
        {
            var envPath = _adminHelperService.GetEnvFilePath();
            if (!File.Exists(envPath))
            {
                return null;
            }

            foreach (var line in File.ReadLines(envPath))
            {
                if (AdminHelperService.ShouldSkipEnvLine(line))
                {
                    continue;
                }

                var (key, value) = AdminHelperService.ParseEnvLine(line);
                if (key.Equals("EXTENSION_REPOSITORIES", StringComparison.OrdinalIgnoreCase))
                {
                    return value;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read extension repositories from .env file");
        }

        return null;
    }

    public async Task<List<StoreExtensionItem>> FetchStoreExtensionsAsync(CancellationToken cancellationToken = default)
    {
        var catalog = await FetchStoreCatalogAsync(cancellationToken);
        return catalog.Items;
    }

    public async Task<ExtensionStoreResponse> FetchStoreCatalogAsync(CancellationToken cancellationToken = default)
    {
        var catalog = new ExtensionStoreResponse();
        var registries = _controlPlane == null
            ? Array.Empty<ExtensionRegistryRecord>()
            : (await _controlPlane.ListRegistriesAsync(cancellationToken)).Where(item => item.Enabled).ToArray();
        var packages = _controlPlane == null
            ? Array.Empty<ExtensionPackageRecord>()
            : (await _controlPlane.ListPackagesAsync(cancellationToken: cancellationToken)).ToArray();
        catalog.Repositories.AddRange(registries.Select(item => item.RegistryUrl));

        using var client = _httpClientFactory.CreateClient("ExtensionSdkV1");
        client.Timeout = TimeSpan.FromSeconds(5);

        foreach (var registry in registries)
        {
            var repo = registry.RegistryUrl;
            try
            {
                using var response = await client.GetAsync(repo, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidDataException(
                        $"Extension registry returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
                var json = await ReadRegistryJsonAsync(response.Content, cancellationToken);
                var parsedItems = ParseStoreRegistry(json, repo);
                foreach (var item in parsedItems)
                {
                    item.RegistryId = registry.Id;
                    item.IsInstalled = packages.Any(package => package.ExtensionId == item.Id &&
                                                               package.State != ExtensionPackageState.Uninstalled);
                    item.IsEnabled = packages.Any(package => package.ExtensionId == item.Id &&
                                                             package.State == ExtensionPackageState.Active);
                    catalog.Items.Add(item);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch extension store registry from {Repo}", repo);
                catalog.Errors.Add(new ExtensionStoreError
                {
                    Repository = repo,
                    Message = ex.Message
                });
            }
        }

        catalog.Items = catalog.Items
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return catalog;
    }

    public async Task<int> ValidateStoreRegistryAsync(
        string registryUrl,
        CancellationToken cancellationToken = default)
    {
        if (!OutboundRequestGuard.TryCreateSafeHttpUri(registryUrl, out var registryUri, out var reason) ||
            registryUri!.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(registryUri.Fragment))
        {
            throw new InvalidDataException(
                $"Registry URL must be a public HTTPS JSON document without credentials or a fragment: {reason}.");
        }

        if (registryUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
            registryUri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).Length == 2)
        {
            throw new InvalidDataException(
                "That URL is a GitHub repository page. Enter the direct raw URL to an Allstarr registry JSON document instead, such as https://raw.githubusercontent.com/owner/repository/main/registry.json.");
        }

        using var client = _httpClientFactory.CreateClient("ExtensionSdkV1");
        client.Timeout = TimeSpan.FromSeconds(5);
        using var response = await client.GetAsync(
            registryUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidDataException(
                $"Registry URL returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}). Enter a direct URL to the registry JSON document.");
        }

        var json = await ReadRegistryJsonAsync(response.Content, cancellationToken);
        List<StoreExtensionItem> items;
        try
        {
            items = ParseStoreRegistry(json, registryUri.AbsoluteUri);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Registry URL did not return JSON. Enter a direct URL to an Allstarr registry JSON document, not a repository or file-view page.",
                exception);
        }

        if (items.Count == 0)
        {
            throw new InvalidDataException(
                "Registry contains no installable Allstarr packages. Every entry needs a safe extension id, a direct HTTPS download URL, and a 64-character SHA-256 checksum. Registries made for another application are not compatible.");
        }

        return items.Count;
    }

    private static async Task<string> ReadRegistryJsonAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumRegistryBytes)
            throw new InvalidDataException("Extension registry exceeds 4 MiB.");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var buffer = new char[64 * 1024];
        var jsonBuilder = new StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            if (jsonBuilder.Length + read > MaximumRegistryBytes)
                throw new InvalidDataException("Extension registry exceeds 4 MiB.");
            jsonBuilder.Append(buffer, 0, read);
        }

        return jsonBuilder.ToString();
    }

    public async Task<bool> InstallExtensionAsync(string downloadUrl, CancellationToken cancellationToken = default)
    {
        _logger.LogWarning("Blocked extension install without a mandatory registry package checksum");
        await Task.CompletedTask;
        return false;
    }

    public async Task<ExtensionPackageRecord> StageExtensionAsync(
        string downloadUrl,
        string expectedSha256,
        Guid? registryId = null,
        CancellationToken cancellationToken = default)
    {
        if (!RemoteInstallEnabled)
        {
            throw new UnauthorizedAccessException(
                "Remote extension installation requires Extensions:AllowRemoteInstall=true.");
        }
        if (_controlPlane == null) throw new InvalidOperationException("The extension control plane is unavailable.");
        if (!OutboundRequestGuard.TryCreateSafeHttpUri(downloadUrl, out var packageUri, out _) ||
            packageUri!.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(packageUri.Fragment))
            throw new ArgumentException("An HTTPS package URL without credentials or a fragment is required.", nameof(downloadUrl));

        string? stagingDirectory = null;
        try
        {
            using var client = _httpClientFactory.CreateClient("ExtensionSdkV1");
            client.Timeout = TimeSpan.FromSeconds(15);
            var stagingName = $".install-{Guid.NewGuid():N}";
            stagingDirectory = ResolveContainedPath(stagingName);
            var archivePath = ResolveContainedPath(Path.Combine(stagingName, "package.zip"));
            var extractedDirectory = ResolveContainedPath(Path.Combine(stagingName, "extracted"));
            Directory.CreateDirectory(stagingDirectory);
            using (var response = await client.GetAsync(packageUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > ExtensionSdkV1.MaximumArchiveBytes)
                    throw new ExtensionSdkValidationException("Extension archive exceeds the SDK v1 size limit.");
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var target = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var buffer = new byte[64 * 1024];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > ExtensionSdkV1.MaximumArchiveBytes)
                        throw new ExtensionSdkValidationException("Extension archive exceeds the SDK v1 size limit.");
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }
            var verified = ExtensionSdkV1.VerifyArchive(archivePath, expectedSha256, extractedDirectory);
            var package = await _controlPlane.StageAsync(verified, registryId, cancellationToken);
            File.Delete(archivePath);
            stagingDirectory = null;
            return package;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stage verified extension package");
            throw;
        }
        finally
        {
            if (stagingDirectory != null && Directory.Exists(stagingDirectory))
            {
                try
                {
                    Directory.Delete(EnsureContainedPath(stagingDirectory), true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to clean extension staging directory {Path}", stagingDirectory);
                }
            }
        }
    }

    public bool UninstallExtension(string id)
    {
        if (!TryResolveExtensionDirectory(id, out var folder))
        {
            return false;
        }

        _activeExtensions.TryRemove(id, out _);
        if (Directory.Exists(folder))
        {
            try
            {
                Directory.Delete(folder, true);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete extension directory {Path}", folder);
            }
        }

        return false;
    }

    public bool DisableExtension(string id)
    {
        if (!TryResolveExtensionDirectory(id, out var folder) || !Directory.Exists(folder))
        {
            return false;
        }

        _activeExtensions.TryRemove(id, out _);
        File.WriteAllText(Path.Combine(folder, DisabledMarkerFile), DateTime.UtcNow.ToString("O"));
        _logger.LogInformation("Disabled extension {ExtensionId}", id);
        return true;
    }

    public async Task<bool> EnableExtensionAsync(string id)
    {
        _logger.LogWarning("Blocked legacy folder activation for extension {ExtensionId}; stage and review an SDK v1 package instead", id);
        await Task.CompletedTask;
        return false;
    }

    private async Task BootInstalledExtensions()
    {
        try
        {
            var dirs = Directory.GetDirectories(_extensionsDir);
            foreach (var dir in dirs)
            {
                if (IsExtensionDisabled(dir))
                {
                    _logger.LogInformation("Skipping disabled extension folder {Path}", dir);
                    continue;
                }

                await BootExtensionAsync(dir);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error booting installed extensions");
        }
    }

    private async Task BootExtensionAsync(string folderPath)
    {
        try
        {
            if (!TryResolveInstalledExtensionFolder(folderPath, out var extensionId, out var safeFolderPath))
            {
                _logger.LogWarning("Skipping extension directory with an unsafe or ambiguous path: {Path}", folderPath);
                return;
            }

            if (IsExtensionDisabled(safeFolderPath))
            {
                return;
            }

            var manifestPath = Path.Combine(safeFolderPath, "manifest.json");
            var indexJsPath = Path.Combine(safeFolderPath, "index.js");

            if (!File.Exists(manifestPath) || !File.Exists(indexJsPath)) return;

            var manifestJson = await File.ReadAllTextAsync(manifestPath);
            var indexJs = await File.ReadAllTextAsync(indexJsPath);
            using var manifest = JsonDocument.Parse(manifestJson);
            if (!TryValidateExtensionId(ReadString(manifest.RootElement, "id", "name"), out var manifestId) ||
                !manifestId.Equals(extensionId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Extension manifest id must be valid and match its installation directory.");
            }

            var sandbox = new ExtensionSandbox(
                safeFolderPath,
                manifestJson,
                indexJs,
                _httpClientFactory,
                _logger,
                permissions: ExtensionRuntimePermissionSet.None,
                runtimeStateDirectory: ResolveContainedPath(Path.Combine(".runtime", extensionId)));
            _activeExtensions[extensionId] = sandbox;
            _logger.LogInformation("Loaded extension successfully: {DisplayName} ({Id}) v{Version}", sandbox.DisplayName, sandbox.Id, sandbox.Version);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to boot extension in folder {Path}", folderPath);
        }
    }

    private bool IsExtensionInstalled(string id)
    {
        return TryResolveExtensionDirectory(id, out var folder) && Directory.Exists(folder);
    }

    private static bool IsExtensionDisabled(string folderPath)
    {
        return File.Exists(Path.Combine(folderPath, DisabledMarkerFile));
    }

    private InstalledExtensionInfo? ReadInstalledExtensionInfo(string folderPath)
    {
        try
        {
            if (!TryResolveInstalledExtensionFolder(folderPath, out var folderId, out var safeFolderPath))
            {
                return null;
            }

            var manifestPath = Path.Combine(safeFolderPath, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                return null;
            }

            var manifestJson = File.ReadAllText(manifestPath);
            using var doc = JsonDocument.Parse(manifestJson);
            var root = doc.RootElement;
            if (!TryValidateExtensionId(ReadString(root, "id", "name"), out var id) ||
                !id.Equals(folderId, StringComparison.Ordinal))
            {
                return null;
            }

            var active = _activeExtensions.TryGetValue(id, out var sandbox);
            var displayName = sandbox?.DisplayName ?? ReadString(root, "displayName", "display_name", "title", "name");
            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = id;
            }

            var version = sandbox?.Version ?? ReadString(root, "version");
            return new InstalledExtensionInfo
            {
                Id = id,
                Name = sandbox?.Name ?? ReadString(root, "name", "id"),
                DisplayName = displayName,
                Description = sandbox?.Description ?? ReadString(root, "description", "summary"),
                Version = string.IsNullOrWhiteSpace(version) ? "1.0.0" : version,
                Types = sandbox?.Types.ToList() ?? ReadStringList(root, "types", "type", "capabilities", "capability"),
                Enabled = active && !IsExtensionDisabled(safeFolderPath)
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read installed extension manifest from {Path}", folderPath);
            return null;
        }
    }

    public static List<StoreExtensionItem> ParseStoreRegistry(string json, string repoUrl = "")
    {
        var items = new List<StoreExtensionItem>();
        using var doc = JsonDocument.Parse(json);

        if (!TryGetRegistryItems(doc.RootElement, out var registryItems))
        {
            return items;
        }

        foreach (var ext in registryItems.EnumerateArray())
        {
            if (ext.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!TryValidateExtensionId(ReadString(ext, "id", "name", "slug"), out var id))
            {
                continue;
            }

            var name = ReadString(ext, "name", "id", "slug");
            var displayName = ReadString(ext, "displayName", "display_name", "title", "label", "name");
            var downloadUrl = ReadString(ext, "downloadUrl", "download_url", "zipUrl", "zip_url", "archiveUrl", "archive_url", "packageUrl", "package_url", "url");
            var sha256 = ReadString(ext, "sha256", "checksum", "packageSha256", "package_sha256");

            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(downloadUrl) ||
                !Regex.IsMatch(sha256, "^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant) ||
                !OutboundRequestGuard.TryCreateSafeHttpUri(downloadUrl, out var packageUri, out _) ||
                packageUri!.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(packageUri.Fragment))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                name = id;
            }

            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = name;
            }

            var version = ReadString(ext, "version");
            items.Add(new StoreExtensionItem
            {
                Id = id,
                Name = name,
                DisplayName = displayName,
                Description = ReadString(ext, "description", "summary"),
                DownloadUrl = downloadUrl,
                Sha256 = sha256.ToLowerInvariant(),
                Version = string.IsNullOrWhiteSpace(version) ? "1.0.0" : version,
                RepoUrl = repoUrl,
                HomepageUrl = ReadString(ext, "homepage", "homepageUrl", "homepage_url", "repository", "repoUrl", "repo_url"),
                Types = ReadStringList(ext, "types", "type", "capabilities", "capability")
            });
        }

        return items
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static bool TryGetRegistryItems(JsonElement root, out JsonElement items)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            items = root;
            return true;
        }

        foreach (var key in new[] { "extensions", "items", "plugins", "packages" })
        {
            if (root.TryGetProperty(key, out items) && items.ValueKind == JsonValueKind.Array)
            {
                return true;
            }
        }

        items = default;
        return false;
    }

    private static string ReadString(JsonElement element, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (element.TryGetProperty(key, out var value))
            {
                if (value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString() ?? string.Empty;
                }

                if (value.ValueKind == JsonValueKind.Number ||
                    value.ValueKind == JsonValueKind.True ||
                    value.ValueKind == JsonValueKind.False)
                {
                    return value.ToString();
                }
            }
        }

        return string.Empty;
    }

    private static List<string> ReadStringList(JsonElement element, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!element.TryGetProperty(key, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Array)
            {
                return value.EnumerateArray()
                    .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(item => item!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                return value.GetString()!
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        return [];
    }

    private static bool TryValidateExtensionId(string? raw, out string id)
    {
        id = raw ?? string.Empty;
        if (id.Length == 0 || id.Length > MaximumExtensionIdLength)
        {
            return false;
        }

        return ExtensionIdPattern.IsMatch(id) &&
               !Path.IsPathRooted(id) &&
               id.IndexOf(Path.DirectorySeparatorChar) < 0 &&
               id.IndexOf(Path.AltDirectorySeparatorChar) < 0;
    }

    private bool TryResolveExtensionDirectory(string id, out string folderPath)
    {
        folderPath = string.Empty;
        if (!TryValidateExtensionId(id, out var validId))
        {
            return false;
        }

        folderPath = ResolveExtensionDirectory(validId);
        return true;
    }

    private string ResolveExtensionDirectory(string id)
    {
        if (!TryValidateExtensionId(id, out var validId))
        {
            throw new InvalidDataException("Extension id must be a lowercase kebab-case identifier.");
        }

        return ResolveContainedPath(validId);
    }

    private bool TryResolveInstalledExtensionFolder(
        string folderPath,
        out string extensionId,
        out string safeFolderPath)
    {
        extensionId = string.Empty;
        safeFolderPath = string.Empty;

        try
        {
            var candidate = EnsureContainedPath(folderPath);
            var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(candidate));
            if (!TryValidateExtensionId(folderName, out extensionId))
            {
                return false;
            }

            var expected = ResolveExtensionDirectory(extensionId);
            if (!PathsEqual(candidate, expected))
            {
                return false;
            }

            safeFolderPath = expected;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or NotSupportedException)
        {
            return false;
        }
    }

    private string ResolveContainedPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("Extension path must be relative to the extension root.");
        }

        return EnsureContainedPath(Path.Combine(_extensionsDir, relativePath));
    }

    private string EnsureContainedPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var relativePath = Path.GetRelativePath(_extensionsDir, fullPath);
        if (relativePath.Equals(".", StringComparison.Ordinal) ||
            Path.IsPathRooted(relativePath) ||
            relativePath.Equals("..", StringComparison.Ordinal) ||
            relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Extension path escapes the configured extension root.");
        }

        return fullPath;
    }

    private static bool PathsEqual(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return Path.GetFullPath(left).Equals(Path.GetFullPath(right), comparison);
    }

    private static string ResolveExtensionPackageRoot(string extractedDirectory)
    {
        if (File.Exists(Path.Combine(extractedDirectory, "manifest.json")) &&
            File.Exists(Path.Combine(extractedDirectory, "index.js")))
        {
            return extractedDirectory;
        }

        var childDirectories = Directory.GetDirectories(extractedDirectory);
        foreach (var childDirectory in childDirectories)
        {
            if (File.Exists(Path.Combine(childDirectory, "manifest.json")) &&
                File.Exists(Path.Combine(childDirectory, "index.js")))
            {
                return childDirectory;
            }
        }

        return extractedDirectory;
    }

    private void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        var safeSourceDirectory = EnsureContainedPath(sourceDirectory);
        var safeTargetDirectory = EnsureContainedPath(targetDirectory);

        foreach (var directory in Directory.GetDirectories(safeSourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(safeSourceDirectory, directory);
            var targetPath = EnsureContainedPath(Path.Combine(safeTargetDirectory, relativePath));
            Directory.CreateDirectory(targetPath);
        }

        foreach (var file in Directory.GetFiles(safeSourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(safeSourceDirectory, file);
            var targetFile = EnsureContainedPath(Path.Combine(safeTargetDirectory, relativePath));
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            File.Copy(file, targetFile, overwrite: true);
        }
    }
}

public class ExtensionStoreResponse
{
    public List<string> Repositories { get; set; } = [];
    public List<StoreExtensionItem> Items { get; set; } = [];
    public List<ExtensionStoreError> Errors { get; set; } = [];
}

public class ExtensionStoreError
{
    public string Repository { get; set; } = "";
    public string Message { get; set; } = "";
}

public class StoreExtensionItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string Version { get; set; } = "";
    public Guid? RegistryId { get; set; }
    public bool IsInstalled { get; set; }
    public bool IsEnabled { get; set; }
    public string RepoUrl { get; set; } = "";
    public string HomepageUrl { get; set; } = "";
    public List<string> Types { get; set; } = [];
}

public class InstalledExtensionInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public string Version { get; set; } = "";
    public bool Enabled { get; set; }
    public List<string> Types { get; set; } = [];
}

public sealed record ExtensionRuntimePermissionSet(
    IReadOnlySet<string> NetworkOrigins,
    IReadOnlySet<string> CacheKeys,
    IReadOnlySet<string> SecretKeys,
    Func<string, string?>? SecretResolver = null,
    Action<string, string>? LogSink = null)
{
    public static ExtensionRuntimePermissionSet None { get; } = new(
        new HashSet<string>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal));
}

public class ExtensionSandbox
{
    private const int MaximumHookResultCharacters = 4 * 1024 * 1024;
    public string Id { get; }
    public string Name { get; }
    public string DisplayName { get; }
    public string Description { get; }
    public string Version { get; }
    public List<string> Types { get; } = new();

    private readonly Engine _engine;
    private readonly JsValue _extensionObj;
    private readonly ILogger _logger;
    private readonly object _engineLock = new();

    public ExtensionSandbox(
        string folderPath,
        string manifestJson,
        string indexJs,
        IHttpClientFactory httpClientFactory,
        ILogger logger,
        ExtensionRuntimePermissionSet? permissions = null,
        string? runtimeStateDirectory = null)
    {
        _logger = logger;

        using var doc = JsonDocument.Parse(manifestJson);
        var root = doc.RootElement;
        Id = ReadManifestString(root, "id", "name");
        Name = Id;
        DisplayName = ReadManifestString(root, "displayName", "display_name", "title", "name");
        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            DisplayName = Id;
        }
        Description = ReadManifestString(root, "description", "summary");
        Version = ReadManifestString(root, "version");
        if (string.IsNullOrWhiteSpace(Version))
        {
            Version = "1.0.0";
        }

        if (TryGetManifestProperty(root, out var typeEl, "types", "type", "capabilities", "capability"))
        {
            if (typeEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in typeEl.EnumerateArray())
                {
                    var type = t.ValueKind == JsonValueKind.String ? t.GetString() : t.ToString();
                    if (!string.IsNullOrWhiteSpace(type))
                    {
                        Types.Add(type);
                    }
                }
            }
            else if (typeEl.ValueKind == JsonValueKind.String)
            {
                Types.AddRange(typeEl.GetString()!
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
        }

        _engine = new Engine(options =>
        {
            options.LimitRecursion(150);
            options.LimitMemory(32L * 1024 * 1024);
            options.MaxStatements(1_000_000);
            options.TimeoutInterval(TimeSpan.FromSeconds(12));
        });

        var hostBridge = new ExtensionHostBridge(
            runtimeStateDirectory ?? Path.Combine(Path.GetTempPath(), "allstarr-extension-runtime", Id),
            httpClientFactory,
            logger,
            permissions ?? ExtensionRuntimePermissionSet.None);
        _engine.SetValue("host", hostBridge);

        _engine.Execute("var _registeredExtension = null; function registerExtension(obj) { _registeredExtension = obj; }");
        _engine.Execute("const log = { info: function(...args) { host.Log('info', args.join(' ')); }, warn: function(...args) { host.Log('warn', args.join(' ')); }, error: function(...args) { host.Log('error', args.join(' ')); }, debug: function(...args) { host.Log('debug', args.join(' ')); } };");
        _engine.Execute("const storage = { get: function(key) { return host.StorageGet(key); }, set: function(key, val) { host.StorageSet(key, val); } };");
        _engine.Execute("const secrets = { get: function(key) { return host.SecretGet(key); } };");
        _engine.Execute("const http = { get: function(url, headers) { return host.HttpGet(url, headers); }, post: function(url, body, headers) { return host.HttpPost(url, body, headers); } };");
        _engine.Execute("const utils = { randomUserAgent: function() { return host.RandomUserAgent(); }, hmacSHA1: function(key, data) { return host.HmacSHA1(key, data); }, hmacSHA1Secret: function(key, data) { return host.HmacSHA1Secret(key, data); } };");

        _engine.Execute(indexJs);

        _extensionObj = _engine.GetValue("_registeredExtension");
        if (_extensionObj.IsUndefined() || _extensionObj.IsNull())
        {
            throw new InvalidOperationException($"Extension {Id} did not register itself using registerExtension()");
        }

        var initFn = _extensionObj.Get("initialize");
        if (initFn.IsCallable())
        {
            var configObj = _engine.Evaluate("({})");
            _engine.Invoke(initFn, configObj);
        }
    }

    public SearchResult Search(string query, int limit)
    {
        var searchFn = _extensionObj.Get("customSearch");
        if (!searchFn.IsCallable())
        {
            searchFn = _extensionObj.Get("searchTracks");
        }

        if (!searchFn.IsCallable()) return new SearchResult();

        try
        {
            var jsResult = _engine.Invoke(searchFn, query, _engine.Evaluate($"({{ limit: {limit} }})"));
            return MapJsResultToSearchResult(jsResult);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JS customSearch invocation failed on extension {Id}", Id);
            return new SearchResult();
        }
    }

    public Song? GetSong(string id)
    {
        var fn = _extensionObj.Get("getTrack");
        if (!fn.IsCallable()) return null;

        try
        {
            var jsResult = _engine.Invoke(fn, id);
            if (!jsResult.IsObject()) return null;

            var item = jsResult.AsObject();
            var song = new Song
            {
                Id = $"ext-{Id}-song-{item.Get("id").ToString()}",
                Title = item.Get("name").ToString(),
                Artist = ParseJsArtists(item.Get("artists")),
                Album = item.Get("album").ToString(),
                ExternalProvider = Id,
                ExternalId = item.Get("id").ToString(),
                Duration = ParseJsDuration(item.Get("duration_ms")),
                Isrc = item.Get("isrc")?.ToString() ?? "",
                Genre = item.Get("genre")?.ToString() ?? "",
                IsLocal = false
            };
            song.Artists.Add(song.Artist);
            return song;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JS getTrack failed on extension {Id}", Id);
            return null;
        }
    }

    /// <summary>Invokes an SDK v1 hook through a JSON-only boundary.</summary>
    public string? InvokeJson(string hook, string requestJson)
    {
        if (string.IsNullOrWhiteSpace(hook)) throw new ArgumentException("A hook name is required.", nameof(hook));
        if (requestJson.Length > 1024 * 1024) throw new InvalidOperationException("Extension request is too large.");
        lock (_engineLock)
        {
            var function = _extensionObj.Get(hook);
            if (!function.IsCallable()) return null;

            var request = _engine.Invoke(_engine.GetValue("JSON").AsObject().Get("parse"), requestJson);
            var result = _engine.Invoke(function, request);
            if (result.IsUndefined()) return null;
            var serialized = _engine.Invoke(_engine.GetValue("JSON").AsObject().Get("stringify"), result).ToString();
            if (serialized.Length > MaximumHookResultCharacters)
                throw new InvalidOperationException("Extension response is too large.");
            return serialized;
        }
    }

    public bool HasCallableHook(string hook) =>
        !string.IsNullOrWhiteSpace(hook) && IsCallable(hook);

    private bool IsCallable(string hook)
    {
        lock (_engineLock) return _extensionObj.Get(hook).IsCallable();
    }

    public Album? GetAlbum(string id)
    {
        var fn = _extensionObj.Get("getAlbum");
        if (!fn.IsCallable()) return null;

        try
        {
            var jsResult = _engine.Invoke(fn, id);
            if (!jsResult.IsObject()) return null;

            var item = jsResult.AsObject();
            var album = new Album
            {
                Id = $"ext-{Id}-album-{item.Get("id").ToString()}",
                Title = item.Get("name").ToString(),
                Artist = ParseJsArtists(item.Get("artists")),
                Year = ParseJsYear(item.Get("release_date")),
                ExternalProvider = Id,
                ExternalId = item.Get("id").ToString()
            };

            var tracksVal = item.Get("tracks");
            if (tracksVal.IsArray())
            {
                var arr = tracksVal.AsArray();
                foreach (var index in arr.GetOwnPropertyKeys())
                {
                    var idxStr = index.ToString();
                    if (idxStr == "length" || !int.TryParse(idxStr, out _)) continue;

                    var trackVal = arr.Get(index);
                    if (trackVal.IsObject())
                    {
                        var track = trackVal.AsObject();
                        var song = new Song
                        {
                            Id = $"ext-{Id}-song-{track.Get("id").ToString()}",
                            Title = track.Get("name").ToString(),
                            Artist = ParseJsArtists(track.Get("artists") ?? item.Get("artists")),
                            Album = album.Title,
                            ExternalProvider = Id,
                            ExternalId = track.Get("id").ToString(),
                            Duration = ParseJsDuration(track.Get("duration_ms")),
                            Track = track.Get("track_number")?.IsNumber() == true ? (int)track.Get("track_number").AsNumber() : 1,
                            IsLocal = false
                        };
                        song.Artists.Add(song.Artist);
                        album.Songs.Add(song);
                    }
                }
            }

            return album;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JS getAlbum failed on extension {Id}", Id);
            return null;
        }
    }

    public Artist? GetArtist(string id)
    {
        var fn = _extensionObj.Get("getArtist");
        if (!fn.IsCallable()) return null;

        try
        {
            var jsResult = _engine.Invoke(fn, id);
            if (!jsResult.IsObject()) return null;

            var item = jsResult.AsObject();
            return new Artist
            {
                Id = $"ext-{Id}-artist-{item.Get("id").ToString()}",
                Name = item.Get("name").ToString(),
                ExternalProvider = Id,
                ExternalId = item.Get("id").ToString()
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JS getArtist failed on extension {Id}", Id);
            return null;
        }
    }

    private SearchResult MapJsResultToSearchResult(JsValue jsVal)
    {
        var result = new SearchResult();
        if (jsVal.IsArray())
        {
            var arr = jsVal.AsArray();
            foreach (var index in arr.GetOwnPropertyKeys())
            {
                var idxStr = index.ToString();
                if (idxStr == "length" || !int.TryParse(idxStr, out _)) continue;

                var itemVal = arr.Get(index);
                if (!itemVal.IsObject()) continue;

                var item = itemVal.AsObject();
                var itemType = item.Get("item_type").ToString();

                if (itemType == "track" || itemType == "song")
                {
                    var song = new Song
                    {
                        Id = $"ext-{Id}-song-{item.Get("id").ToString()}",
                        Title = item.Get("name").ToString(),
                        Artist = ParseJsArtists(item.Get("artists")),
                        Album = item.Get("album").ToString(),
                        ExternalProvider = Id,
                        ExternalId = item.Get("id").ToString(),
                        Duration = ParseJsDuration(item.Get("duration_ms")),
                        Isrc = item.Get("isrc")?.ToString() ?? "",
                        Genre = item.Get("genre")?.ToString() ?? "",
                        IsLocal = false
                    };
                    song.Artists.Add(song.Artist);
                    result.Songs.Add(song);
                }
                else if (itemType == "album")
                {
                    var album = new Album
                    {
                        Id = $"ext-{Id}-album-{item.Get("id").ToString()}",
                        Title = item.Get("name").ToString(),
                        Artist = ParseJsArtists(item.Get("artists")),
                        Year = ParseJsYear(item.Get("release_date")),
                        ExternalProvider = Id,
                        ExternalId = item.Get("id").ToString()
                    };
                    result.Albums.Add(album);
                }
                else if (itemType == "artist")
                {
                    var artist = new Artist
                    {
                        Id = $"ext-{Id}-artist-{item.Get("id").ToString()}",
                        Name = item.Get("name").ToString(),
                        ExternalProvider = Id,
                        ExternalId = item.Get("id").ToString()
                    };
                    result.Artists.Add(artist);
                }
            }
        }
        return result;
    }

    private string ParseJsArtists(JsValue val)
    {
        if (val.IsArray())
        {
            var arr = val.AsArray();
            var artistsList = new List<string>();
            foreach (var key in arr.GetOwnPropertyKeys())
            {
                var idxStr = key.ToString();
                if (idxStr == "length" || !int.TryParse(idxStr, out _)) continue;

                var artistVal = arr.Get(key);
                if (artistVal.IsString()) artistsList.Add(artistVal.AsString());
            }
            if (artistsList.Count > 0) return string.Join(", ", artistsList);
        }
        return val.ToString();
    }

    private int? ParseJsDuration(JsValue val)
    {
        if (val.IsNumber()) return (int)(val.AsNumber() / 1000);
        if (double.TryParse(val.ToString(), out var ms)) return (int)(ms / 1000);
        return null;
    }

    private int? ParseJsYear(JsValue val)
    {
        var str = val.ToString();
        if (string.IsNullOrEmpty(str)) return null;
        if (str.Length >= 4 && int.TryParse(str.Substring(0, 4), out var yr)) return yr;
        return null;
    }

    private static string ReadManifestString(JsonElement element, params string[] keys)
    {
        if (!TryGetManifestProperty(element, out var value, keys))
        {
            return string.Empty;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString();
    }

    private static bool TryGetManifestProperty(JsonElement element, out JsonElement value, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (element.TryGetProperty(key, out value))
            {
                return true;
            }
        }

        value = default;
        return false;
    }
}

public class ExtensionHostBridge
{
    private const int MaximumCacheBytes = 4 * 1024 * 1024;
    private const int MaximumCacheKeys = 256;
    private const int MaximumLogEvents = 1_000;
    private static readonly Regex SensitiveLogPattern = new(
        "(?i)(authorization|password|secret|token|cookie|api[-_]?key)\\s*[=:]\\s*[^\\s,;]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly string _folderPath;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;
    private readonly Dictionary<string, string> _storage = new();
    private readonly string _storageFile;
    private readonly ExtensionRuntimePermissionSet _permissions;
    private int _logEvents;

    public ExtensionHostBridge(
        string runtimeStateDirectory,
        IHttpClientFactory httpClientFactory,
        ILogger logger,
        ExtensionRuntimePermissionSet permissions)
    {
        _folderPath = Path.GetFullPath(runtimeStateDirectory);
        Directory.CreateDirectory(_folderPath);
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _permissions = permissions;
        _storageFile = Path.Combine(_folderPath, "storage.json");
        LoadStorage();
    }

    public void Log(string level, string message)
    {
        if (Interlocked.Increment(ref _logEvents) > MaximumLogEvents) return;
        message = SensitiveLogPattern.Replace(message ?? string.Empty, "$1=[redacted]");
        if (message.Length > 2_000) message = message[..2_000];
        _permissions.LogSink?.Invoke(level, message);
        switch (level.ToLowerInvariant())
        {
            case "error": _logger.LogError("[JS EXT] {Message}", message); break;
            case "warn": _logger.LogWarning("[JS EXT] {Message}", message); break;
            case "debug": _logger.LogDebug("[JS EXT] {Message}", message); break;
            default: _logger.LogInformation("[JS EXT] {Message}", message); break;
        }
    }

    public string? StorageGet(string key)
    {
        EnsureCachePermission(key);
        return _storage.TryGetValue(key, out var val) ? val : null;
    }

    public void StorageSet(string key, string value)
    {
        EnsureCachePermission(key);
        if (value.Length > 256 * 1024) throw new InvalidOperationException("Extension cache values are limited to 256 KiB.");
        if (!_storage.ContainsKey(key) && _storage.Count >= MaximumCacheKeys)
            throw new InvalidOperationException("Extension cache key limit reached.");
        var projectedBytes = _storage.Where(item => item.Key != key)
            .Sum(item => Encoding.UTF8.GetByteCount(item.Key) + Encoding.UTF8.GetByteCount(item.Value)) +
            Encoding.UTF8.GetByteCount(key) + Encoding.UTF8.GetByteCount(value);
        if (projectedBytes > MaximumCacheBytes)
            throw new InvalidOperationException("Extension cache is limited to 4 MiB.");
        _storage[key] = value;
        SaveStorage();
    }

    public string? SecretGet(string key)
    {
        if (!_permissions.SecretKeys.Contains(key) || _permissions.SecretResolver == null)
            throw new UnauthorizedAccessException("Extension secret permission is not approved.");
        return $"{{{{allstarr-secret:{key}}}}}";
    }

    public object HttpGet(string url, object? headers) => HttpCall("GET", url, null, headers);

    public object HttpPost(string url, string? body, object? headers) => HttpCall("POST", url, body, headers);

    public string RandomUserAgent() => "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    public byte[] HmacSHA1(JsValue keyVal, JsValue dataVal)
    {
        byte[] key = ConvertToByteArray(keyVal);
        byte[] data = ConvertToByteArray(dataVal);
        using var hmac = new HMACSHA1(key);
        return hmac.ComputeHash(data);
    }

    public byte[] HmacSHA1Secret(string key, JsValue dataVal)
    {
        if (!_permissions.SecretKeys.Contains(key) || _permissions.SecretResolver?.Invoke(key) is not { } secret)
            throw new UnauthorizedAccessException("Extension secret permission is not approved.");
        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(secret));
        return hmac.ComputeHash(ConvertToByteArray(dataVal));
    }

    private byte[] ConvertToByteArray(JsValue val)
    {
        if (val.IsArray())
        {
            var arr = val.AsArray();
            var bytes = new List<byte>();
            foreach (var key in arr.GetOwnPropertyKeys())
            {
                var idxStr = key.ToString();
                if (idxStr == "length" || !int.TryParse(idxStr, out _)) continue;

                var bVal = arr.Get(key);
                if (bVal.IsNumber()) bytes.Add((byte)bVal.AsNumber());
            }
            return bytes.ToArray();
        }
        if (val.IsString())
        {
            return Encoding.UTF8.GetBytes(val.AsString());
        }
        return Array.Empty<byte>();
    }

    private object HttpCall(string method, string url, string? body, object? headersObj)
    {
        try
        {
            if (!OutboundRequestGuard.TryCreateSafeHttpUri(url, out var safeUri, out _) ||
                safeUri!.Scheme != Uri.UriSchemeHttps ||
                !_permissions.NetworkOrigins.Contains(safeUri.GetLeftPart(UriPartial.Authority) + "/"))
                throw new UnauthorizedAccessException("Extension network origin is not approved.");
            using var client = _httpClientFactory.CreateClient("ExtensionSdkV1");
            client.Timeout = TimeSpan.FromSeconds(15);

            var request = new HttpRequestMessage(new HttpMethod(method), safeUri);

            if (body != null)
            {
                request.Content = new StringContent(ResolveSecretMarkers(body), Encoding.UTF8, "application/json");
            }

            if (headersObj is Jint.Native.Object.ObjectInstance obj)
            {
                foreach (var prop in obj.GetOwnProperties())
                {
                    var headerKey = prop.Key.ToString();
                    var headerVal = ResolveSecretMarkers(obj.Get(prop.Key).ToString());

                    if (!string.IsNullOrEmpty(headerVal))
                    {
                        if (headerKey.Contains('\r') || headerKey.Contains('\n') || headerVal.Contains('\r') || headerVal.Contains('\n') ||
                            headerKey.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                            headerKey.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (headerKey.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) && request.Content != null)
                        {
                            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(headerVal);
                        }
                        else
                        {
                            request.Headers.TryAddWithoutValidation(headerKey, headerVal);
                        }
                    }
                }
            }

            using var response = client.SendAsync(request).GetAwaiter().GetResult();
            if (response.RequestMessage?.RequestUri is { } finalUri &&
                !_permissions.NetworkOrigins.Contains(finalUri.GetLeftPart(UriPartial.Authority) + "/"))
                throw new UnauthorizedAccessException("Extension redirect left its approved origin.");
            if (response.Content.Headers.ContentLength > 4 * 1024 * 1024)
                throw new InvalidOperationException("Extension response exceeds 4 MiB.");
            using var reader = new StreamReader(response.Content.ReadAsStream());
            var chars = new char[64 * 1024];
            var bodyBuilder = new StringBuilder();
            int charsRead;
            while ((charsRead = reader.Read(chars, 0, chars.Length)) > 0)
            {
                if (bodyBuilder.Length + charsRead > 4 * 1024 * 1024)
                    throw new InvalidOperationException("Extension response exceeds 4 MiB.");
                bodyBuilder.Append(chars, 0, charsRead);
            }
            var bodyText = bodyBuilder.ToString();

            var respHeaders = response.Headers.ToDictionary(k => k.Key, v => (object)string.Join(", ", v.Value));

            return new
            {
                statusCode = (int)response.StatusCode,
                body = bodyText,
                headers = respHeaders
            };
        }
        catch (Exception ex)
        {
            return new
            {
                statusCode = 500,
                body = "",
                error = ex is UnauthorizedAccessException ? "permission_denied" : "extension_request_failed"
            };
        }
    }

    private void EnsureCachePermission(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || !_permissions.CacheKeys.Contains(key))
            throw new UnauthorizedAccessException("Extension cache permission is not approved.");
    }

    private string ResolveSecretMarkers(string value)
    {
        foreach (var key in _permissions.SecretKeys)
        {
            var marker = $"{{{{allstarr-secret:{key}}}}}";
            if (!value.Contains(marker, StringComparison.Ordinal)) continue;
            var secret = _permissions.SecretResolver?.Invoke(key) ??
                         throw new UnauthorizedAccessException("Extension account secret is unavailable.");
            value = value.Replace(marker, secret, StringComparison.Ordinal);
        }
        return value;
    }

    private void LoadStorage()
    {
        if (File.Exists(_storageFile))
        {
            try
            {
                var txt = File.ReadAllText(_storageFile);
                var data = JsonSerializer.Deserialize<Dictionary<string, string>>(txt);
                if (data != null)
                {
                    var approved = data.Where(item => _permissions.CacheKeys.Contains(item.Key) &&
                                                       item.Value.Length <= 256 * 1024)
                        .Take(MaximumCacheKeys).ToArray();
                    var bytes = approved.Sum(item => Encoding.UTF8.GetByteCount(item.Key) +
                                                     Encoding.UTF8.GetByteCount(item.Value));
                    if (bytes <= MaximumCacheBytes)
                        foreach (var item in approved) _storage[item.Key] = item.Value;
                }
            }
            catch { }
        }
    }

    private void SaveStorage()
    {
        try
        {
            var txt = JsonSerializer.Serialize(_storage);
            var temporary = _storageFile + ".tmp";
            File.WriteAllText(temporary, txt);
            File.Move(temporary, _storageFile, true);
        }
        catch
        {
            try { File.Delete(_storageFile + ".tmp"); } catch { }
            throw;
        }
    }
}

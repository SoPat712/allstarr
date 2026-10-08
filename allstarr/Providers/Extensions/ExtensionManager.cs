using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jint;
using Jint.Native;
using allstarr.Core.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using allstarr.Models.Domain;
using allstarr.Models.Search;
using allstarr.Models.Subsonic;
using allstarr.Core.Storage;
using Microsoft.AspNetCore.DataProtection;

namespace allstarr.Services.Common;

public class ExtensionManager : IDisposable
{
    private const int MaximumExtensionIdLength = 128;
    private const int MaximumRegistryBytes = 4 * 1024 * 1024;
    private static readonly Regex ExtensionIdPattern = new(
        "^[a-z0-9]+(?:-[a-z0-9]+)*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ExtensionManager> _logger;
    private readonly IConfiguration _configuration;
    private readonly string _extensionsDir;
    private readonly ExtensionControlPlaneService? _controlPlane;

    private readonly ConcurrentDictionary<string, ExtensionSandbox> _activeExtensions = new();
    private readonly Microsoft.Extensions.Caching.Memory.MemoryCache _packageChecksumCache = new(
        new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions
        {
            SizeLimit = 256
        });

    public ExtensionManager(
        IHttpClientFactory httpClientFactory,
        ILogger<ExtensionManager> logger,
        IConfiguration configuration,
        ExtensionControlPlaneService? controlPlane = null)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _configuration = configuration;
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

    public bool IsTrustedInstallClient(IPAddress? remoteIp)
    {
        var normalized = remoteIp == null ? null : AdminNetworkBindingPolicy.NormalizeAddress(remoteIp);
        return AdminNetworkBindingPolicy.IsRemoteIpAllowed(
                   remoteIp, AdminNetworkBindingPolicy.ParseTrustedSubnets(_configuration)) ||
               normalized != null && AdminNetworkBindingPolicy.ResolveContainerGateways(_configuration).Contains(normalized);
    }

    public ExtensionSandbox? GetExtension(string id)
    {
        return TryValidateExtensionId(id, out var validId) &&
               _activeExtensions.TryGetValue(validId, out var sandbox)
            ? sandbox
            : null;
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
        client.Timeout = TimeSpan.FromSeconds(15);

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
                    if (item.PackageFormat == SpotiFlacExtensionCompatibility.Marker && string.IsNullOrEmpty(item.Sha256))
                        item.Sha256 = await ResolveRemotePackageSha256Async(client, item.DownloadUrl, cancellationToken);
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
                "That is a GitHub project page, not its extension catalog. Use the project's raw registry.json URL. Allstarr supports both native Allstarr catalogs and SpotiFLAC catalogs.");
        }

        if (registryUri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase) &&
            registryUri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).Length < 4)
        {
            throw new InvalidDataException(
                "That raw GitHub URL points to a project folder, and folders return 404. Use the complete registry.json URL, including the branch name—for this store: https://raw.githubusercontent.com/spotiflacapp/SpotiFLAC-Extension/main/registry.json");
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
                $"Catalog URL returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}). Check that the complete URL ends with the catalog JSON filename.");
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
                "This catalog does not contain Allstarr or SpotiFLAC extension packages.");
        }

        var compatible = items.FirstOrDefault(item => item.PackageFormat == SpotiFlacExtensionCompatibility.Marker && string.IsNullOrEmpty(item.Sha256));
        if (compatible != null)
            compatible.Sha256 = await ResolveRemotePackageSha256Async(client, compatible.DownloadUrl, cancellationToken);

        return items.Count;
    }

    private async Task<string> ResolveRemotePackageSha256Async(
        HttpClient client,
        string downloadUrl,
        CancellationToken cancellationToken)
    {
        if (_packageChecksumCache.TryGetValue(downloadUrl, out var cachedValue) &&
            cachedValue is string cached)
        {
            return cached;
        }
        using var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > ExtensionSdkV1.MaximumArchiveBytes)
            throw new InvalidDataException("SpotiFLAC extension package exceeds the supported size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > ExtensionSdkV1.MaximumArchiveBytes)
                throw new InvalidDataException("SpotiFLAC extension package exceeds the supported size limit.");
            hash.AppendData(buffer, 0, read);
        }
        if (total == 0) throw new InvalidDataException("SpotiFLAC extension package is empty.");
        var checksum = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        Microsoft.Extensions.Caching.Memory.CacheExtensions.Set(
            _packageChecksumCache,
            downloadUrl,
            checksum,
            new Microsoft.Extensions.Caching.Memory.MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30),
                Size = 1
            });
        return checksum;
    }

    public void Dispose() => _packageChecksumCache.Dispose();

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

    public async Task<ExtensionPackageRecord> StageExtensionAsync(
        string downloadUrl,
        string expectedSha256,
        Guid? registryId = null,
        bool trustedAdminRequest = false,
        CancellationToken cancellationToken = default)
    {
        if (!RemoteInstallEnabled && !trustedAdminRequest)
        {
            throw new UnauthorizedAccessException(
                "Package installation is limited to this server and trusted admin networks unless Extensions:AllowRemoteInstall=true.");
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
            var isSpotiFlacPackage = downloadUrl.EndsWith(".sflx", StringComparison.OrdinalIgnoreCase) ||
                downloadUrl.EndsWith(".spotiflac-ext", StringComparison.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(downloadUrl) ||
                (!isSpotiFlacPackage && !Regex.IsMatch(sha256, "^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)) ||
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
            var iconUrl = ReadString(ext, "iconUrl", "icon_url", "icon");
            if (!string.IsNullOrWhiteSpace(iconUrl) &&
                (!OutboundRequestGuard.TryCreateSafeHttpUri(iconUrl, out var parsedIcon, out _) ||
                 parsedIcon!.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(parsedIcon.Fragment)))
                iconUrl = string.Empty;
            items.Add(new StoreExtensionItem
            {
                Id = isSpotiFlacPackage && !id.StartsWith("spotiflac-", StringComparison.OrdinalIgnoreCase)
                    ? $"spotiflac-{id}"
                    : id,
                Name = name,
                DisplayName = displayName,
                Description = ReadString(ext, "description", "summary"),
                DownloadUrl = downloadUrl,
                Sha256 = sha256.ToLowerInvariant(),
                PackageFormat = isSpotiFlacPackage ? SpotiFlacExtensionCompatibility.Marker : "allstarr-v1",
                Version = string.IsNullOrWhiteSpace(version) ? "1.0.0" : version,
                RepoUrl = repoUrl,
                HomepageUrl = ReadString(ext, "homepage", "homepageUrl", "homepage_url", "repository", "repoUrl", "repo_url"),
                IconUrl = iconUrl,
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
    public string PackageFormat { get; set; } = "allstarr-v1";
    public Guid? RegistryId { get; set; }
    public bool IsInstalled { get; set; }
    public bool IsEnabled { get; set; }
    public string RepoUrl { get; set; } = "";
    public string HomepageUrl { get; set; } = "";
    public string IconUrl { get; set; } = "";
    public List<string> Types { get; set; } = [];
}

public sealed record ExtensionRuntimePermissionSet(
    IReadOnlySet<string> NetworkOrigins,
    IReadOnlySet<string> CacheKeys,
    IReadOnlySet<string> SecretKeys,
    Func<string, string?>? SecretResolver = null,
    Action<string, string>? LogSink = null,
    IReadOnlySet<string>? SettingKeys = null)
{
    public static ExtensionRuntimePermissionSet None { get; } = new(
        new HashSet<string>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal));
}

internal sealed record ExtensionBoundedHttpPayload(
    byte[] Bytes,
    string? ContentType);

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
    private readonly ExtensionHostBridge _hostBridge;

    public ExtensionSandbox(
        string folderPath,
        string manifestJson,
        string indexJs,
        IHttpClientFactory httpClientFactory,
        ILogger logger,
        ExtensionRuntimePermissionSet? permissions = null,
        string? runtimeStateDirectory = null,
        IDataProtector? sessionProtector = null)
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
            options.LimitMemory(64L * 1024 * 1024);
            options.MaxStatements(1_000_000);
            options.TimeoutInterval(TimeSpan.FromSeconds(12));
        });

        var isSpotiFlac = SpotiFlacExtensionCompatibility.IsNormalizedManifest(manifestJson);
        var manifest = root.TryGetProperty("sdkVersion", out _) ? ExtensionSdkV1.ParseManifest(manifestJson) : null;
        _hostBridge = new ExtensionHostBridge(
            runtimeStateDirectory ?? Path.Combine(Path.GetTempPath(), "allstarr-extension-runtime", Id),
            httpClientFactory,
            logger,
            permissions ?? ExtensionRuntimePermissionSet.None,
            manifest?.SignedSession,
            sessionProtector,
            Id);
        _engine.SetValue("host", _hostBridge);

        var settingsJson = isSpotiFlac ? SpotiFlacExtensionCompatibility.SettingsJson(manifestJson) : "{}";
        _engine.SetValue("_allstarrSettingsJson", settingsJson);
        _engine.SetValue("_allstarrSettingKeysJson", JsonSerializer.Serialize(
            manifest?.Settings?.Select(item => item.Key) ?? []));
        _engine.Execute("var _allstarrDefaultSettings = JSON.parse(_allstarrSettingsJson); var _allstarrSettingKeys = JSON.parse(_allstarrSettingKeysJson);");

        _engine.Execute("var _registeredExtension = null; function registerExtension(obj) { _registeredExtension = obj; }");
        _engine.Execute("const log = { info: function(...args) { host.Log('info', args.join(' ')); }, warn: function(...args) { host.Log('warn', args.join(' ')); }, error: function(...args) { host.Log('error', args.join(' ')); }, debug: function(...args) { host.Log('debug', args.join(' ')); } };");
        _engine.Execute("const storage = { get: function(key) { return host.StorageGet(key); }, set: function(key, val) { host.StorageSet(key, String(val)); }, remove: function(key) { return host.StorageRemove(key); } };");
        _engine.Execute("const secrets = { get: function(key) { return host.SecretGet(key); } };");
        _engine.Execute("const session = { signedFetch: function(method, path, body, headers) { return host.SessionSignedFetch(String(method || 'GET'), String(path || ''), body == null ? null : (typeof body === 'string' ? body : JSON.stringify(body)), headers || {}); }, completeGrant: function(grant) { return host.SessionCompleteGrant(grant == null ? null : String(grant)); }, status: function() { return host.SessionStatus(); }, clear: function() { return host.SessionClear(); } };");
        _engine.Execute("""
            function _allstarrResponse(raw) {
              raw = raw || {};
              var headerValues = raw.headers || {};
              var headers = {
                get: function(name) { var wanted = String(name || '').toLowerCase(); for (var key in headerValues) if (String(key).toLowerCase() === wanted) return String(headerValues[key]); return null; },
                has: function(name) { return this.get(name) !== null; },
                forEach: function(callback) { for (var key in headerValues) callback(String(headerValues[key]), key, this); }
              };
              return {
                ok: Number(raw.statusCode || 0) >= 200 && Number(raw.statusCode || 0) < 300,
                status: Number(raw.statusCode || 0), statusCode: Number(raw.statusCode || 0),
                statusText: String(raw.statusText || ''), url: String(raw.url || ''),
                headers: headers, body: String(raw.body || ''), error: raw.error || null,
                text: function() { return String(raw.body || ''); },
                json: function() { return JSON.parse(String(raw.body || 'null')); },
                arrayBuffer: function() { return host.Base64Decode(raw.bodyBase64 || ''); }
              };
            }
            const http = {
              get: function(url, headers) { return host.HttpGet(url, headers); },
              post: function(url, body, headers) { return host.HttpPost(url, body, headers); },
              put: function(url, body, headers) { return host.HttpPut(url, body, headers); },
              delete: function(url, headers) { return host.HttpDelete(url, headers); },
              patch: function(url, body, headers) { return host.HttpPatch(url, body, headers); },
              request: function(method, url, body, headers) { return host.HttpRequest(String(method || 'GET'), url, body == null ? null : String(body), headers); }
            };
            function fetch(url, options) {
              options = options || {};
              var raw = host.HttpRequest(String(options.method || 'GET'), String(url), options.body == null ? null : String(options.body), options.headers || {});
              return _allstarrResponse(raw);
            }
            function atob(value) { return host.Base64DecodeString(String(value || '')); }
            function btoa(value) { return host.Base64EncodeString(String(value || '')); }
            class TextEncoder { encode(value) { return host.Utf8Encode(String(value == null ? '' : value)); } }
            class TextDecoder { decode(value) { return host.Utf8Decode(value); } }
            class URLSearchParams {
              constructor(value, owner) { this._owner = owner || null; this._pairs = []; var raw = String(value || '').replace(/^\?/, ''); if (raw) { var parts = raw.split('&'); for (var i = 0; i < parts.length; i++) { var pair = parts[i].split('='); this._pairs.push([decodeURIComponent(pair.shift() || ''), decodeURIComponent(pair.join('=') || '')]); } } }
              _changed() { if (this._owner) this._owner.search = this.toString() ? '?' + this.toString() : ''; }
              append(key, value) { this._pairs.push([String(key), String(value)]); this._changed(); }
              set(key, value) { this.delete(key); this._pairs.push([String(key), String(value)]); this._changed(); }
              get(key) { key = String(key); for (var i = 0; i < this._pairs.length; i++) if (this._pairs[i][0] === key) return this._pairs[i][1]; return null; }
              getAll(key) { key = String(key); return this._pairs.filter(function(x) { return x[0] === key; }).map(function(x) { return x[1]; }); }
              has(key) { return this.get(key) !== null; }
              delete(key) { key = String(key); this._pairs = this._pairs.filter(function(x) { return x[0] !== key; }); this._changed(); }
              toString() { return this._pairs.map(function(x) { return encodeURIComponent(x[0]) + '=' + encodeURIComponent(x[1]); }).join('&'); }
            }
            class URL {
              constructor(value, base) { var parsed = host.ParseUrl(String(value), base == null ? null : String(base)); if (!parsed || !parsed.href) throw new TypeError('Invalid URL'); this.protocol = parsed.protocol; this.hostname = parsed.hostname; this.host = parsed.host; this.port = parsed.port; this.pathname = parsed.pathname; this.search = parsed.search; this.hash = parsed.hash; this.searchParams = new URLSearchParams(this.search, this); }
              toString() { return host.ComposeUrl(this.protocol, this.hostname, this.port, this.pathname, this.search, this.hash); }
              get href() { return this.toString(); }
              set href(value) { var next = new URL(value); this.protocol = next.protocol; this.hostname = next.hostname; this.host = next.host; this.port = next.port; this.pathname = next.pathname; this.search = next.search; this.hash = next.hash; this.searchParams = next.searchParams; this.searchParams._owner = this; }
            }
            """);
        _engine.Execute("const artifacts = { download: function(url, artifactId, headers) { return host.ArtifactDownload(url, artifactId, headers); } };");
        _engine.Execute("const file = { download: function(url, path, options) { return host.FileDownload(url, path, options); }, exists: function(path) { return host.FileExists(path); }, delete: function(path) { return host.FileDelete(path); }, getSize: function(path) { return host.FileSize(path); }, readBytes: function(path, options) { return host.FileReadBytes(path, options); }, writeBytes: function(path, data, options) { return host.FileWriteBytes(path, data, options); } };");
        _engine.Execute("const utils = { randomUserAgent: function() { return host.RandomUserAgent(); }, appUserAgent: function() { return host.AppUserAgent(); }, appVersion: function() { return '4.7.0'; }, sleep: function(ms) { host.Sleep(ms); }, sha256: function(value) { return host.Sha256(value); }, md5: function(value) { return host.Md5(value); }, base64Decode: function(value) { return host.Base64Decode(value); }, isDownloadCancelled: function() { return false; }, isRequestCancelled: function() { return false; }, hmacSHA1: function(key, data) { return host.HmacSHA1(key, data); }, hmacSHA1Secret: function(key, data) { return host.HmacSHA1Secret(key, data); } };");

        _engine.Execute(indexJs);

        if (isSpotiFlac) _engine.Execute(SpotiFlacExtensionCompatibility.RuntimeAdapterScript);

        _extensionObj = _engine.GetValue("_registeredExtension");
        if (_extensionObj.IsUndefined() || _extensionObj.IsNull())
        {
            throw new InvalidOperationException($"Extension {Id} did not register itself using registerExtension()");
        }

        var initFn = _extensionObj.Get("initialize");
        if (initFn.IsCallable())
        {
            var configObj = _engine.Invoke(_engine.GetValue("JSON").AsObject().Get("parse"), settingsJson);
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
                Bitrate = ParseJsPositiveInt(item.Get("bitrate")),
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

            var prepare = _engine.GetValue("_allstarrPrepareInvocation");
            if (prepare.IsCallable()) _engine.Invoke(prepare);

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

    internal bool IsNetworkAllowed(Uri uri) => _hostBridge.IsNetworkAllowed(uri);

    public bool HasSignedSession => _hostBridge.HasSignedSession;
    internal Task<ExtensionBoundedHttpPayload> FetchBytesAsync(
        Uri uri,
        int maximumBytes,
        CancellationToken cancellationToken) =>
        _hostBridge.FetchBytesAsync(uri, maximumBytes, cancellationToken);

    public (int EntryCount, long PayloadBytes) StorageUsage()
    {
        lock (_engineLock)
        {
            return _hostBridge.StorageUsage();
        }
    }
    public object SignedSessionStatus() => _hostBridge.SessionStatus();
    public object StartSignedSessionVerification() => _hostBridge.SessionStartVerification();
    public object CompleteSignedSessionGrant(string grant) => _hostBridge.SessionCompleteGrant(grant);
    public object ClearSignedSession() => _hostBridge.SessionClear();

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
                            Bitrate = ParseJsPositiveInt(track.Get("bitrate")),
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
                        Bitrate = ParseJsPositiveInt(item.Get("bitrate")),
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

    private static int? ParseJsPositiveInt(JsValue value)
    {
        if (value.IsNumber())
        {
            var number = value.AsNumber();
            return number is > 0 and <= int.MaxValue ? (int)number : null;
        }

        return int.TryParse(value.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) &&
               parsed > 0
            ? parsed
            : null;
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
    private const int MaximumRuntimeLogFingerprints = 4_096;
    private const int HttpFailureThreshold = 2;
    private static readonly TimeSpan HttpFailureWindow = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan HttpInitialCooldown = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan HttpMaximumCooldown = TimeSpan.FromHours(1);
    private static readonly TimeSpan RuntimeLogDeduplicationWindow = TimeSpan.FromMinutes(5);
    private static readonly Regex SensitiveLogPattern = new(
        "(?i)(authorization|password|secret|token|cookie|api[-_]?key)\\s*[=:]\\s*[^\\s,;]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Dictionary<string, ExtensionRuntimeLogState> RuntimeLogStates =
        new(StringComparer.Ordinal);
    private static readonly object RuntimeLogLock = new();
    private readonly string _folderPath;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;
    private readonly Dictionary<string, string> _storage = new();
    private readonly string _storageFile;
    private readonly ExtensionRuntimePermissionSet _permissions;
    private readonly ExtensionSignedSessionClient? _signedSession;
    private readonly string _extensionId;
    private readonly Dictionary<string, ExtensionHttpFailureState> _httpFailureStates =
        new(StringComparer.Ordinal);
    private readonly object _httpFailureLock = new();
    private DateTimeOffset _lastSyntheticCooldownResponseAt;
    private int _logEvents;

    public ExtensionHostBridge(
        string runtimeStateDirectory,
        IHttpClientFactory httpClientFactory,
        ILogger logger,
        ExtensionRuntimePermissionSet permissions,
        ExtensionSignedSessionConfig? signedSession = null,
        IDataProtector? sessionProtector = null,
        string? extensionId = null)
    {
        _folderPath = Path.GetFullPath(runtimeStateDirectory);
        Directory.CreateDirectory(_folderPath);
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _permissions = permissions;
        _extensionId = string.IsNullOrWhiteSpace(extensionId) ? "unknown" : extensionId;
        _storageFile = Path.Combine(_folderPath, "storage.json");
        if (signedSession != null)
        {
            if (sessionProtector == null)
                throw new InvalidOperationException("Signed-session extensions require protected runtime storage.");
            _signedSession = new ExtensionSignedSessionClient(
                signedSession, httpClientFactory, sessionProtector, permissions.NetworkOrigins, _folderPath,
                extensionId ?? signedSession.Namespace);
        }
        LoadStorage();
    }

    public bool HasSignedSession => _signedSession != null;
    public object SessionStatus() => RequireSignedSession().Status();
    public object SessionStartVerification() => RequireSignedSession().StartVerification();
    public object SessionCompleteGrant(string? grant) => RequireSignedSession().CompleteGrant(grant);
    public object SessionClear() => RequireSignedSession().Clear();
    public object SessionSignedFetch(string method, string path, string? body, object? headers) =>
        RequireSignedSession().SignedFetch(method, path, body, headers);

    private ExtensionSignedSessionClient RequireSignedSession() => _signedSession ??
        throw new InvalidOperationException("This extension does not declare a signed session.");

    public void Log(string level, string message)
    {
        message = SensitiveLogPattern.Replace(message ?? string.Empty, "$1=[redacted]").Trim();
        if (level.Equals("error", StringComparison.OrdinalIgnoreCase) &&
            DateTimeOffset.UtcNow - _lastSyntheticCooldownResponseAt < TimeSpan.FromSeconds(5) &&
            message.Contains("503", StringComparison.Ordinal))
            return;
        if (Interlocked.Increment(ref _logEvents) > MaximumLogEvents) return;
        if (message.Length > 2_000) message = message[..2_000];
        if ((message.Length <= 128 &&
             message.Contains("redacted", StringComparison.OrdinalIgnoreCase)) ||
            string.IsNullOrWhiteSpace(message))
            message = "Provider operation failed without a safe diagnostic.";
        if (!ShouldEmitRuntimeLog(level, message, out var suppressedCount)) return;
        if (suppressedCount > 0)
            message = $"{message} ({suppressedCount} equivalent events suppressed.)";
        _permissions.LogSink?.Invoke(level, message);
        switch (level.ToLowerInvariant())
        {
            case "error":
                _logger.LogError(
                    "Extension runtime event {EventCode} from {ExtensionId}: {Diagnostic}",
                    "extension.runtime.error",
                    _extensionId,
                    message);
                break;
            case "warn":
                _logger.LogWarning(
                    "Extension runtime event {EventCode} from {ExtensionId}: {Diagnostic}",
                    "extension.runtime.warning",
                    _extensionId,
                    message);
                break;
            case "debug":
                _logger.LogDebug(
                    "Extension runtime event {EventCode} from {ExtensionId}: {Diagnostic}",
                    "extension.runtime.debug",
                    _extensionId,
                    message);
                break;
            default:
                _logger.LogDebug(
                    "Extension runtime event {EventCode} from {ExtensionId}: {Diagnostic}",
                    "extension.runtime.info",
                    _extensionId,
                    message);
                break;
        }
    }

    private bool ShouldEmitRuntimeLog(
        string level,
        string message,
        out int suppressedCount)
    {
        var key = $"{_extensionId}:{level.Trim().ToLowerInvariant()}:{message}";
        var now = DateTimeOffset.UtcNow;
        lock (RuntimeLogLock)
        {
            if (RuntimeLogStates.TryGetValue(key, out var state) &&
                now - state.LastEmittedAt < RuntimeLogDeduplicationWindow)
            {
                RuntimeLogStates[key] = state with { SuppressedCount = state.SuppressedCount + 1 };
                suppressedCount = 0;
                return false;
            }

            suppressedCount = state?.SuppressedCount ?? 0;
            if (state == null && RuntimeLogStates.Count >= MaximumRuntimeLogFingerprints)
            {
                foreach (var expired in RuntimeLogStates
                             .Where(item => now - item.Value.LastEmittedAt >= RuntimeLogDeduplicationWindow)
                             .Select(item => item.Key)
                             .ToArray())
                    RuntimeLogStates.Remove(expired);
                if (RuntimeLogStates.Count >= MaximumRuntimeLogFingerprints)
                    RuntimeLogStates.Remove(RuntimeLogStates.MinBy(item => item.Value.LastEmittedAt).Key);
            }
            RuntimeLogStates[key] = new ExtensionRuntimeLogState(now, 0);
            return true;
        }
    }

    public string? StorageGet(string key)
    {
        EnsureCachePermission(key);
        return _storage.TryGetValue(key, out var val) ? val : null;
    }

    public (int EntryCount, long PayloadBytes) StorageUsage() => (
        _storage.Count,
        _storage.Sum(item =>
            (long)Encoding.UTF8.GetByteCount(item.Key) +
            Encoding.UTF8.GetByteCount(item.Value)));

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

    public bool StorageRemove(string key)
    {
        EnsureCachePermission(key);
        var removed = _storage.Remove(key);
        if (removed) SaveStorage();
        return removed;
    }

    public string? SecretGet(string key)
    {
        if (!_permissions.SecretKeys.Contains(key) || _permissions.SecretResolver == null)
            throw new UnauthorizedAccessException("Extension secret permission is not approved.");
        return $"{{{{allstarr-secret:{key}}}}}";
    }

    public string? SettingGet(string key)
    {
        if (_permissions.SettingKeys?.Contains(key) != true)
            throw new UnauthorizedAccessException("Extension setting is not declared by the package.");
        return ExtensionInvocationSecretScope.Resolve(key);
    }

    public object HttpGet(string url, object? headers) => HttpCall("GET", url, null, headers);

    public object HttpPost(string url, string? body, object? headers) => HttpCall("POST", url, body, headers);

    public object HttpPut(string url, string? body, object? headers) => HttpCall("PUT", url, body, headers);

    public object HttpPatch(string url, string? body, object? headers) => HttpCall("PATCH", url, body, headers);

    public object HttpDelete(string url, object? headers) => HttpCall("DELETE", url, null, headers);

    public object HttpRequest(string method, string url, string? body, object? headers) =>
        HttpCall(method, url, body, headers);

    internal async Task<ExtensionBoundedHttpPayload> FetchBytesAsync(
        Uri uri,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (maximumBytes is < 1 or > 16 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (uri.Scheme != Uri.UriSchemeHttps || !IsNetworkAllowed(uri))
            throw new UnauthorizedAccessException("Extension artwork origin is not approved.");

        using var client = _httpClientFactory.CreateClient("ExtensionSdkV1");
        client.Timeout = TimeSpan.FromSeconds(15);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.RequestMessage?.RequestUri is not { } finalUri || !IsNetworkAllowed(finalUri))
            throw new UnauthorizedAccessException("Extension artwork redirect left its approved origin.");
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("Extension artwork request failed.", null, response.StatusCode);
        if (response.Content.Headers.ContentLength > maximumBytes)
            throw new InvalidDataException("Extension artwork exceeds the size limit.");

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var destination = new MemoryStream(Math.Min(maximumBytes, 256 * 1024));
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (destination.Length + read > maximumBytes)
                throw new InvalidDataException("Extension artwork exceeds the size limit.");
            destination.Write(buffer, 0, read);
        }
        return new(
            destination.ToArray(),
            response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant());
    }

    public object ArtifactDownload(string url, string artifactId, object? headersObj)
    {
        var scope = ExtensionArtifactInvocationScope.Current ??
                    throw new UnauthorizedAccessException("The artifact broker is available only during a download invocation.");
        if (!OutboundRequestGuard.TryCreateSafeHttpUri(url, out var safeUri, out _) ||
            safeUri!.Scheme != Uri.UriSchemeHttps ||
            !IsNetworkAllowed(safeUri))
            throw new UnauthorizedAccessException("Extension artifact origin is not approved.");

        using var client = _httpClientFactory.CreateClient("ExtensionSdkV1");
        using var request = new HttpRequestMessage(HttpMethod.Get, safeUri);
        AddHeaders(request, headersObj);
        using var response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, scope.CancellationToken)
            .GetAwaiter().GetResult();
        if (response.RequestMessage?.RequestUri is not { } finalUri ||
            !IsNetworkAllowed(finalUri))
            throw new UnauthorizedAccessException("Extension artifact redirect left its approved origin.");
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("Extension artifact request failed.", null, response.StatusCode);
        using var content = response.Content.ReadAsStream();
        var written = scope.Write(artifactId, content, response.Content.Headers.ContentLength);
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["artifactId"] = written.ArtifactId,
            ["sha256"] = written.Sha256,
            ["sizeBytes"] = written.SizeBytes,
            ["verified"] = true
        };
    }

    public object FileDownload(string url, string outputPath, object? options)
    {
        var scope = ExtensionArtifactInvocationScope.Current ??
                    throw new UnauthorizedAccessException("File access is available only during a download invocation.");
        if (!OutboundRequestGuard.TryCreateSafeHttpUri(url, out var safeUri, out _) ||
            safeUri!.Scheme != Uri.UriSchemeHttps || !IsNetworkAllowed(safeUri))
            return new { success = false, path = "", error = "permission_denied" };
        var target = scope.ResolveTemporaryPath(outputPath);
        try
        {
            using var client = _httpClientFactory.CreateClient("ExtensionSdkV1");
            using var request = new HttpRequestMessage(HttpMethod.Get, safeUri);
            AddHeaders(request, HeaderObject(options));
            using var response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, scope.CancellationToken)
                .GetAwaiter().GetResult();
            if (response.RequestMessage?.RequestUri is not { } finalUri || !IsNetworkAllowed(finalUri))
                throw new UnauthorizedAccessException("Extension download redirect left its approved origin.");
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > scope.MaximumBytes)
                throw new InvalidDataException("Extension download exceeds the managed artifact size limit.");
            using var source = response.Content.ReadAsStream(scope.CancellationToken);
            using var destination = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[128 * 1024];
            long written = 0;
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                written += read;
                if (written > scope.MaximumBytes)
                    throw new InvalidDataException("Extension download exceeds the managed artifact size limit.");
                destination.Write(buffer, 0, read);
            }
            return new { success = true, path = outputPath, size = written };
        }
        catch (Exception exception)
        {
            if (File.Exists(target)) File.Delete(target);
            return new { success = false, path = "", error = exception is UnauthorizedAccessException ? "permission_denied" : "download_failed" };
        }
    }

    public bool FileExists(string path) => File.Exists(RequireFileScope().ResolveTemporaryPath(path));

    public bool FileDelete(string path)
    {
        var resolved = RequireFileScope().ResolveTemporaryPath(path);
        if (File.Exists(resolved)) File.Delete(resolved);
        return true;
    }

    public long FileSize(string path)
    {
        var resolved = RequireFileScope().ResolveTemporaryPath(path);
        return File.Exists(resolved) ? new FileInfo(resolved).Length : 0;
    }

    public object FileReadBytes(string path, object? options)
    {
        var scope = RequireFileScope();
        var resolved = scope.ResolveTemporaryPath(path);
        if (!File.Exists(resolved)) return new { success = false, data = "", size = 0L, error = "not_found" };
        var bytes = File.ReadAllBytes(resolved);
        if (bytes.LongLength > scope.MaximumBytes)
            throw new InvalidDataException("Extension file exceeds the managed artifact size limit.");
        return new { success = true, data = Convert.ToBase64String(bytes), size = bytes.LongLength };
    }

    public object FileWriteBytes(string path, string base64Data, object? options)
    {
        var scope = RequireFileScope();
        var resolved = scope.ResolveTemporaryPath(path);
        var bytes = Convert.FromBase64String(base64Data ?? string.Empty);
        if (bytes.LongLength > scope.MaximumBytes)
            throw new InvalidDataException("Extension file exceeds the managed artifact size limit.");
        Directory.CreateDirectory(Path.GetDirectoryName(resolved)!);
        File.WriteAllBytes(resolved, bytes);
        return new { success = true, path, size = bytes.LongLength };
    }

    public object CommitFile(string path, string artifactId)
    {
        var scope = RequireFileScope();
        var resolved = scope.ResolveTemporaryPath(path);
        using var stream = new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.Read);
        var result = scope.Write(artifactId, stream, stream.Length);
        return new { artifactId = result.ArtifactId, sha256 = result.Sha256, sizeBytes = result.SizeBytes, verified = true };
    }

    public string RandomUserAgent() => "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    public string AppUserAgent() => "Allstarr/3.0 (SpotiFLAC compatibility runtime)";

    public void Sleep(int milliseconds)
    {
        if (milliseconds is < 0 or > 30_000) throw new ArgumentOutOfRangeException(nameof(milliseconds));
        Thread.Sleep(milliseconds);
    }

    public string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? ""))).ToLowerInvariant();

    public string Md5(string value) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(value ?? ""))).ToLowerInvariant();

    public byte[] Base64Decode(string value) => Convert.FromBase64String(value ?? "");

    public string Base64DecodeString(string value) =>
        Encoding.Latin1.GetString(Convert.FromBase64String(value ?? string.Empty));

    public string Base64EncodeString(string value) =>
        Convert.ToBase64String(Encoding.Latin1.GetBytes(value ?? string.Empty));

    public byte[] Utf8Encode(string value) => Encoding.UTF8.GetBytes(value ?? string.Empty);

    public string Utf8Decode(JsValue value) => Encoding.UTF8.GetString(ConvertToByteArray(value));

    public object ParseUrl(string value, string? baseValue)
    {
        Uri? uri;
        if (baseValue != null && Uri.TryCreate(baseValue, UriKind.Absolute, out var baseUri))
            Uri.TryCreate(baseUri, value, out uri);
        else
            Uri.TryCreate(value, UriKind.Absolute, out uri);
        if (uri == null) return new { };
        return new
        {
            href = uri.AbsoluteUri,
            protocol = uri.Scheme + ":",
            hostname = uri.Host,
            host = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}",
            port = uri.IsDefaultPort ? "" : uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            pathname = uri.AbsolutePath,
            search = uri.Query,
            hash = uri.Fragment
        };
    }

    public string ComposeUrl(string protocol, string hostname, string port, string pathname, string search, string hash)
    {
        var builder = new UriBuilder(protocol.TrimEnd(':'), hostname)
        {
            Path = string.IsNullOrEmpty(pathname) ? "/" : pathname,
            Query = (search ?? string.Empty).TrimStart('?'),
            Fragment = (hash ?? string.Empty).TrimStart('#')
        };
        if (int.TryParse(port, out var parsedPort)) builder.Port = parsedPort;
        return builder.Uri.AbsoluteUri;
    }

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
                !IsNetworkAllowed(safeUri))
                throw new UnauthorizedAccessException("Extension network origin is not approved.");
            var normalizedMethod = method.Trim().ToUpperInvariant();
            var routeKey = $"{normalizedMethod} {safeUri.GetLeftPart(UriPartial.Authority)}{safeUri.AbsolutePath}";
            if (TryGetHttpCooldown(routeKey, out var retryAfterSeconds, out var lastStatusCode))
            {
                _lastSyntheticCooldownResponseAt = DateTimeOffset.UtcNow;
                return new
                {
                    statusCode = 503,
                    statusText = "Temporarily unavailable",
                    url = safeUri.AbsoluteUri,
                    body = "",
                    bodyBase64 = "",
                    headers = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase),
                    error = "provider_temporarily_unavailable",
                    retryAfterSeconds,
                    upstreamStatusCode = lastStatusCode
                };
            }

            using var client = _httpClientFactory.CreateClient("ExtensionSdkV1");
            client.Timeout = TimeSpan.FromSeconds(15);

            var request = new HttpRequestMessage(new HttpMethod(normalizedMethod), safeUri);

            if (body != null)
            {
                request.Content = new StringContent(ResolveSecretMarkers(body), Encoding.UTF8, "application/json");
            }

            AddHeaders(request, headersObj);

            using var response = client.SendAsync(request).GetAwaiter().GetResult();
            if (response.RequestMessage?.RequestUri is { } finalUri &&
                !IsNetworkAllowed(finalUri))
                throw new UnauthorizedAccessException("Extension redirect left its approved origin.");
            if (response.IsSuccessStatusCode)
                RecordHttpSuccess(routeKey);
            else if (ShouldCoolDown(response.StatusCode))
                RecordHttpFailure(routeKey, normalizedMethod, safeUri, (int)response.StatusCode);
            if (response.Content.Headers.ContentLength > 4 * 1024 * 1024)
                throw new InvalidOperationException("Extension response exceeds 4 MiB.");
            using var responseStream = response.Content.ReadAsStream();
            using var output = new MemoryStream();
            var buffer = new byte[64 * 1024];
            int bytesRead;
            while ((bytesRead = responseStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (output.Length + bytesRead > 4 * 1024 * 1024)
                    throw new InvalidOperationException("Extension response exceeds 4 MiB.");
                output.Write(buffer, 0, bytesRead);
            }
            var bytes = output.ToArray();
            var charset = response.Content.Headers.ContentType?.CharSet;
            Encoding bodyEncoding;
            try { bodyEncoding = string.IsNullOrWhiteSpace(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset); }
            catch { bodyEncoding = Encoding.UTF8; }
            var bodyText = bodyEncoding.GetString(bytes);

            var respHeaders = response.Headers.Concat(response.Content.Headers)
                .GroupBy(header => header.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => (object)string.Join(", ", group.SelectMany(item => item.Value)), StringComparer.OrdinalIgnoreCase);

            return new
            {
                statusCode = (int)response.StatusCode,
                statusText = response.ReasonPhrase ?? string.Empty,
                url = response.RequestMessage?.RequestUri?.AbsoluteUri ?? safeUri.AbsoluteUri,
                body = bodyText,
                bodyBase64 = Convert.ToBase64String(bytes),
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

    private bool TryGetHttpCooldown(
        string routeKey,
        out int retryAfterSeconds,
        out int lastStatusCode)
    {
        lock (_httpFailureLock)
        {
            if (_httpFailureStates.TryGetValue(routeKey, out var state) &&
                state.BlockedUntil > DateTimeOffset.UtcNow)
            {
                retryAfterSeconds = Math.Max(
                    1,
                    (int)Math.Ceiling((state.BlockedUntil - DateTimeOffset.UtcNow).TotalSeconds));
                lastStatusCode = state.LastStatusCode;
                return true;
            }
        }

        retryAfterSeconds = 0;
        lastStatusCode = 0;
        return false;
    }

    private void RecordHttpSuccess(string routeKey)
    {
        ExtensionHttpFailureState? recovered = null;
        lock (_httpFailureLock)
        {
            if (_httpFailureStates.Remove(routeKey, out var state) &&
                state.ConsecutiveFailures >= HttpFailureThreshold)
                recovered = state;
        }

        if (recovered != null)
        {
            _logger.LogInformation(
                "Extension provider route recovered {EventCode} for {ExtensionId} after HTTP {StatusCode}",
                "extension.http.recovered",
                _extensionId,
                recovered.LastStatusCode);
        }
    }

    private void RecordHttpFailure(
        string routeKey,
        string method,
        Uri uri,
        int statusCode)
    {
        var now = DateTimeOffset.UtcNow;
        TimeSpan? openedCooldown = null;
        lock (_httpFailureLock)
        {
            _httpFailureStates.TryGetValue(routeKey, out var previous);
            var failures = previous == null || now - previous.LastFailureAt > HttpFailureWindow
                ? 1
                : previous.ConsecutiveFailures + 1;
            var blockedUntil = previous?.BlockedUntil ?? DateTimeOffset.MinValue;
            if (failures >= HttpFailureThreshold)
            {
                var multiplier = 1 << Math.Min(failures - HttpFailureThreshold, 4);
                var cooldown = TimeSpan.FromTicks(Math.Min(
                    HttpMaximumCooldown.Ticks,
                    HttpInitialCooldown.Ticks * multiplier));
                blockedUntil = now + cooldown;
                if (previous == null || previous.BlockedUntil <= now)
                    openedCooldown = cooldown;
            }

            _httpFailureStates[routeKey] = new ExtensionHttpFailureState(
                failures,
                now,
                blockedUntil,
                statusCode);
        }

        if (openedCooldown is { } duration)
        {
            _logger.LogWarning(
                "Extension provider route paused {EventCode} for {ExtensionId}: {Method} {Host}{Path} returned HTTP {StatusCode}; retrying after {RetryAfterSeconds}s",
                "extension.http.cooldown",
                _extensionId,
                method,
                uri.Host,
                uri.AbsolutePath,
                statusCode,
                (int)duration.TotalSeconds);
        }
    }

    private static bool ShouldCoolDown(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.BadRequest
            or HttpStatusCode.Unauthorized
            or HttpStatusCode.Forbidden
            or HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;

    private sealed record ExtensionHttpFailureState(
        int ConsecutiveFailures,
        DateTimeOffset LastFailureAt,
        DateTimeOffset BlockedUntil,
        int LastStatusCode);

    private sealed record ExtensionRuntimeLogState(
        DateTimeOffset LastEmittedAt,
        int SuppressedCount);

    private void AddHeaders(HttpRequestMessage request, object? headersObj)
    {
        var headers = headersObj switch
        {
            Jint.Native.Object.ObjectInstance obj => obj.GetOwnProperties()
                .Select(prop => new KeyValuePair<string, string>(
                    prop.Key.ToString(), obj.Get(prop.Key).ToString())),
            IDictionary<string, object?> values => values.Select(item =>
                new KeyValuePair<string, string>(item.Key, item.Value?.ToString() ?? string.Empty)),
            _ => []
        };
        foreach (var header in headers)
        {
            var headerKey = header.Key;
            var headerVal = ResolveSecretMarkers(header.Value);
            if (string.IsNullOrEmpty(headerVal) || headerKey.Contains('\r') || headerKey.Contains('\n') ||
                headerVal.Contains('\r') || headerVal.Contains('\n') ||
                headerKey.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                headerKey.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                continue;
            if (headerKey.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) && request.Content != null)
                request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(headerVal);
            else
                request.Headers.TryAddWithoutValidation(headerKey, headerVal);
        }
    }

    private static object? HeaderObject(object? options)
    {
        if (options is not Jint.Native.Object.ObjectInstance value) return options;
        var headers = value.Get("headers");
        return headers.IsObject() ? headers.AsObject() : options;
    }

    private static ExtensionArtifactInvocationScope RequireFileScope() =>
        ExtensionArtifactInvocationScope.Current ??
        throw new UnauthorizedAccessException("File access is available only during a download invocation.");

    private void EnsureCachePermission(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || (!_permissions.CacheKeys.Contains(key) && !_permissions.CacheKeys.Contains("*")))
            throw new UnauthorizedAccessException("Extension cache permission is not approved.");
    }

    internal bool IsNetworkAllowed(Uri uri)
    {
        var origin = uri.GetLeftPart(UriPartial.Authority) + "/";
        if (_permissions.NetworkOrigins.Contains(origin)) return true;
        foreach (var permission in _permissions.NetworkOrigins)
        {
            if (!permission.StartsWith("https://*.", StringComparison.OrdinalIgnoreCase)) continue;
            var suffix = permission["https://*".Length..].TrimEnd('/');
            if (uri.Scheme == Uri.UriSchemeHttps &&
                uri.Host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                uri.Host.Length > suffix.Length) return true;
        }
        return false;
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
                    var approved = data.Where(item => (_permissions.CacheKeys.Contains(item.Key) || _permissions.CacheKeys.Contains("*")) &&
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

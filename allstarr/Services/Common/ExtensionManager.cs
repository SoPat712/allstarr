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

namespace allstarr.Services.Common;

public class ExtensionManager
{
    private const string DisabledMarkerFile = ".disabled";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ExtensionManager> _logger;
    private readonly IConfiguration _configuration;
    private readonly AdminHelperService _adminHelperService;
    private readonly string _extensionsDir;

    private readonly ConcurrentDictionary<string, ExtensionSandbox> _activeExtensions = new();

    public ExtensionManager(
        IHttpClientFactory httpClientFactory,
        ILogger<ExtensionManager> logger,
        IConfiguration configuration,
        AdminHelperService adminHelperService)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _configuration = configuration;
        _adminHelperService = adminHelperService;
        _extensionsDir = Path.Combine(Directory.GetCurrentDirectory(), "extensions");
        
        if (!Directory.Exists(_extensionsDir))
        {
            Directory.CreateDirectory(_extensionsDir);
        }

        // Boot installed extensions in background
        Task.Run(BootInstalledExtensions);
    }

    public IReadOnlyCollection<ExtensionSandbox> GetActiveExtensions() => _activeExtensions.Values.ToList();

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
        return _activeExtensions.TryGetValue(NormalizeExtensionId(id), out var sandbox) ? sandbox : null;
    }

    public List<string> GetConfiguredRepositories()
    {
        var repos = ReadExtensionRepositoriesFromEnvFile() ?? _configuration["EXTENSION_REPOSITORIES"];
        if (string.IsNullOrWhiteSpace(repos))
        {
            repos = "https://raw.githubusercontent.com/spotiflacapp/SpotiFLAC-Extension/main/registry.json";
        }
        return repos.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
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
        var repos = GetConfiguredRepositories();
        catalog.Repositories.AddRange(repos);

        using var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(5);

        foreach (var repo in repos)
        {
            try
            {
                var response = await client.GetAsync(repo, cancellationToken);
                if (!response.IsSuccessStatusCode) continue;

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var parsedItems = ParseStoreRegistry(json, repo);
                foreach (var item in parsedItems)
                {
                    item.IsInstalled = IsExtensionInstalled(item.Id);
                    item.IsEnabled = _activeExtensions.ContainsKey(item.Id.ToLowerInvariant());
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

    public async Task<bool> InstallExtensionAsync(string downloadUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Downloading extension from {Url}...", downloadUrl);
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);

            var zipBytes = await client.GetByteArrayAsync(downloadUrl, cancellationToken);
            var tempFile = Path.GetTempFileName();
            await File.WriteAllBytesAsync(tempFile, zipBytes, cancellationToken);

            var tempDir = Path.Combine(Path.GetTempPath(), "allstarr-ext-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            ZipFile.ExtractToDirectory(tempFile, tempDir, overwriteFiles: true);

            var packageRoot = ResolveExtensionPackageRoot(tempDir);
            var manifestPath = Path.Combine(packageRoot, "manifest.json");
            var indexJsPath = Path.Combine(packageRoot, "index.js");

            if (!File.Exists(manifestPath) || !File.Exists(indexJsPath))
            {
                throw new InvalidDataException("Missing manifest.json or index.js in package.");
            }

            var manifestJson = await File.ReadAllTextAsync(manifestPath, cancellationToken);
            using var doc = JsonDocument.Parse(manifestJson);
            var id = NormalizeExtensionId(ReadString(doc.RootElement, "id", "name"));

            if (string.IsNullOrEmpty(id))
            {
                throw new InvalidDataException("Invalid id/name in manifest.json.");
            }

            var targetFolder = Path.Combine(_extensionsDir, id);
            if (Directory.Exists(targetFolder))
            {
                Directory.Delete(targetFolder, true);
            }
            Directory.CreateDirectory(targetFolder);

            CopyDirectory(packageRoot, targetFolder);

            File.Delete(tempFile);
            Directory.Delete(tempDir, true);

            // Load and initialize
            await BootExtensionAsync(targetFolder);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to install extension from {Url}", downloadUrl);
            return false;
        }
    }

    public bool UninstallExtension(string id)
    {
        var normId = id.ToLowerInvariant();
        _activeExtensions.TryRemove(normId, out _);

        var folder = Path.Combine(_extensionsDir, normId);
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
        var normId = id.ToLowerInvariant();
        var folder = Path.Combine(_extensionsDir, normId);
        if (!Directory.Exists(folder))
        {
            return false;
        }

        _activeExtensions.TryRemove(normId, out _);
        File.WriteAllText(Path.Combine(folder, DisabledMarkerFile), DateTime.UtcNow.ToString("O"));
        _logger.LogInformation("Disabled extension {ExtensionId}", normId);
        return true;
    }

    public async Task<bool> EnableExtensionAsync(string id)
    {
        var normId = id.ToLowerInvariant();
        var folder = Path.Combine(_extensionsDir, normId);
        if (!Directory.Exists(folder))
        {
            return false;
        }

        var disabledMarker = Path.Combine(folder, DisabledMarkerFile);
        if (File.Exists(disabledMarker))
        {
            File.Delete(disabledMarker);
        }

        await BootExtensionAsync(folder);
        return _activeExtensions.ContainsKey(normId);
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
            if (IsExtensionDisabled(folderPath))
            {
                return;
            }

            var manifestPath = Path.Combine(folderPath, "manifest.json");
            var indexJsPath = Path.Combine(folderPath, "index.js");

            if (!File.Exists(manifestPath) || !File.Exists(indexJsPath)) return;

            var manifestJson = await File.ReadAllTextAsync(manifestPath);
            var indexJs = await File.ReadAllTextAsync(indexJsPath);

            var sandbox = new ExtensionSandbox(folderPath, manifestJson, indexJs, _httpClientFactory, _logger);
            _activeExtensions[NormalizeExtensionId(sandbox.Id)] = sandbox;
            _logger.LogInformation("Loaded extension successfully: {DisplayName} ({Id}) v{Version}", sandbox.DisplayName, sandbox.Id, sandbox.Version);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to boot extension in folder {Path}", folderPath);
        }
    }

    private bool IsExtensionInstalled(string id)
    {
        var normId = id.ToLowerInvariant();
        return Directory.Exists(Path.Combine(_extensionsDir, normId));
    }

    private static bool IsExtensionDisabled(string folderPath)
    {
        return File.Exists(Path.Combine(folderPath, DisabledMarkerFile));
    }

    private InstalledExtensionInfo? ReadInstalledExtensionInfo(string folderPath)
    {
        try
        {
            var manifestPath = Path.Combine(folderPath, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                return null;
            }

            var manifestJson = File.ReadAllText(manifestPath);
            using var doc = JsonDocument.Parse(manifestJson);
            var root = doc.RootElement;
            var id = NormalizeExtensionId(ReadString(root, "id", "name"));
            if (string.IsNullOrWhiteSpace(id))
            {
                id = Path.GetFileName(folderPath).ToLowerInvariant();
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
                Enabled = active && !IsExtensionDisabled(folderPath)
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

            var id = NormalizeExtensionId(ReadString(ext, "id", "name", "slug"));
            var name = ReadString(ext, "name", "id", "slug");
            var displayName = ReadString(ext, "displayName", "display_name", "title", "label", "name");
            var downloadUrl = ReadString(ext, "downloadUrl", "download_url", "zipUrl", "zip_url", "archiveUrl", "archive_url", "packageUrl", "package_url", "url");

            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(downloadUrl))
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

    private static string NormalizeExtensionId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        return Regex.Replace(raw.Trim().ToLowerInvariant(), "[^a-z0-9._-]", "-");
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

    private static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        foreach (var directory in Directory.GetDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(directory.Replace(sourceDirectory, targetDirectory));
        }

        foreach (var file in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var targetFile = file.Replace(sourceDirectory, targetDirectory);
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
    public string Version { get; set; } = "";
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

public class ExtensionSandbox
{
    public string Id { get; }
    public string Name { get; }
    public string DisplayName { get; }
    public string Description { get; }
    public string Version { get; }
    public List<string> Types { get; } = new();

    private readonly Engine _engine;
    private readonly JsValue _extensionObj;
    private readonly ILogger _logger;

    public ExtensionSandbox(string folderPath, string manifestJson, string indexJs, IHttpClientFactory httpClientFactory, ILogger logger)
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
            options.TimeoutInterval(TimeSpan.FromSeconds(12));
        });

        var hostBridge = new ExtensionHostBridge(folderPath, httpClientFactory, logger);
        _engine.SetValue("host", hostBridge);

        _engine.Execute("let _registeredExtension = null; function registerExtension(obj) { _registeredExtension = obj; }");
        _engine.Execute("const log = { info: function(...args) { host.Log('info', args.join(' ')); }, warn: function(...args) { host.Log('warn', args.join(' ')); }, error: function(...args) { host.Log('error', args.join(' ')); }, debug: function(...args) { host.Log('debug', args.join(' ')); } };");
        _engine.Execute("const storage = { get: function(key) { return host.StorageGet(key); }, set: function(key, val) { host.StorageSet(key, val); } };");
        _engine.Execute("const http = { get: function(url, headers) { return host.HttpGet(url, headers); }, post: function(url, body, headers) { return host.HttpPost(url, body, headers); } };");
        _engine.Execute("const utils = { randomUserAgent: function() { return host.RandomUserAgent(); }, hmacSHA1: function(key, data) { return host.HmacSHA1(key, data); } };");

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
    private readonly string _folderPath;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;
    private readonly Dictionary<string, string> _storage = new();
    private readonly string _storageFile;

    public ExtensionHostBridge(string folderPath, IHttpClientFactory httpClientFactory, ILogger logger)
    {
        _folderPath = folderPath;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _storageFile = Path.Combine(folderPath, "storage.json");
        LoadStorage();
    }

    public void Log(string level, string message)
    {
        switch (level.ToLowerInvariant())
        {
            case "error": _logger.LogError("[JS EXT] {Message}", message); break;
            case "warn": _logger.LogWarning("[JS EXT] {Message}", message); break;
            case "debug": _logger.LogDebug("[JS EXT] {Message}", message); break;
            default: _logger.LogInformation("[JS EXT] {Message}", message); break;
        }
    }

    public string? StorageGet(string key) => _storage.TryGetValue(key, out var val) ? val : null;

    public void StorageSet(string key, string value)
    {
        _storage[key] = value;
        SaveStorage();
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
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            
            var request = new HttpRequestMessage(new HttpMethod(method), url);

            if (body != null)
            {
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }

            if (headersObj is Jint.Native.Object.ObjectInstance obj)
            {
                foreach (var prop in obj.GetOwnProperties())
                {
                    var headerKey = prop.Key.ToString();
                    var headerVal = obj.Get(prop.Key).ToString();
                    
                    if (!string.IsNullOrEmpty(headerVal))
                    {
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

            using var response = client.Send(request);
            var bodyText = new StreamReader(response.Content.ReadAsStream()).ReadToEnd();

            var respHeaders = response.Headers.ToDictionary(k => k.Key, v => (object)string.Join(", ", v.Value));

            return new {
                statusCode = (int)response.StatusCode,
                body = bodyText,
                headers = respHeaders
            };
        }
        catch (Exception ex)
        {
            return new {
                statusCode = 500,
                body = "",
                error = ex.Message
            };
        }
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
                    foreach (var kp in data) _storage[kp.Key] = kp.Value;
                }
            }
            catch {}
        }
    }

    private void SaveStorage()
    {
        try
        {
            var txt = JsonSerializer.Serialize(_storage);
            File.WriteAllText(_storageFile, txt);
        }
        catch {}
    }
}

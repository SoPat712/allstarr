using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jint;
using Jint.Native;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using allstarr.Models.Domain;
using allstarr.Models.Search;
using allstarr.Models.Subsonic;

namespace allstarr.Services.Common;

public class ExtensionManager
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ExtensionManager> _logger;
    private readonly IConfiguration _configuration;
    private readonly string _extensionsDir;

    private readonly ConcurrentDictionary<string, ExtensionSandbox> _activeExtensions = new();

    public ExtensionManager(
        IHttpClientFactory httpClientFactory,
        ILogger<ExtensionManager> logger,
        IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _configuration = configuration;
        _extensionsDir = Path.Combine(Directory.GetCurrentDirectory(), "extensions");
        
        if (!Directory.Exists(_extensionsDir))
        {
            Directory.CreateDirectory(_extensionsDir);
        }

        // Boot installed extensions in background
        Task.Run(BootInstalledExtensions);
    }

    public IReadOnlyCollection<ExtensionSandbox> GetActiveExtensions() => _activeExtensions.Values.ToList();

    public ExtensionSandbox? GetExtension(string id)
    {
        return _activeExtensions.TryGetValue(id.ToLowerInvariant(), out var sandbox) ? sandbox : null;
    }

    public List<string> GetConfiguredRepositories()
    {
        var repos = _configuration["EXTENSION_REPOSITORIES"];
        if (string.IsNullOrWhiteSpace(repos))
        {
            repos = "https://raw.githubusercontent.com/spotiflacapp/SpotiFLAC-Extension/main/registry.json";
        }
        return repos.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    public async Task<List<StoreExtensionItem>> FetchStoreExtensionsAsync(CancellationToken cancellationToken = default)
    {
        var items = new List<StoreExtensionItem>();
        var repos = GetConfiguredRepositories();

        using var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(5);

        foreach (var repo in repos)
        {
            try
            {
                var response = await client.GetAsync(repo, cancellationToken);
                if (!response.IsSuccessStatusCode) continue;

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("extensions", out var exts) && exts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var ext in exts.EnumerateArray())
                    {
                        var id = ext.GetProperty("id").GetString() ?? "";
                        var name = ext.GetProperty("name").GetString() ?? "";
                        var displayName = ext.TryGetProperty("display_name", out var dn) ? dn.GetString() ?? name : name;
                        var desc = ext.TryGetProperty("description", out var ds) ? ds.GetString() ?? "" : "";
                        var downloadUrl = ext.GetProperty("download_url").GetString() ?? "";
                        var version = ext.TryGetProperty("version", out var v) ? v.GetString() ?? "1.0.0" : "1.0.0";

                        var isInstalled = _activeExtensions.ContainsKey(id.ToLowerInvariant());

                        items.Add(new StoreExtensionItem
                        {
                            Id = id,
                            Name = name,
                            DisplayName = displayName,
                            Description = desc,
                            DownloadUrl = downloadUrl,
                            Version = version,
                            IsInstalled = isInstalled,
                            RepoUrl = repo
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch extension store registry from {Repo}", repo);
            }
        }

        return items;
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

            var manifestPath = Path.Combine(tempDir, "manifest.json");
            var indexJsPath = Path.Combine(tempDir, "index.js");

            if (!File.Exists(manifestPath) || !File.Exists(indexJsPath))
            {
                throw new InvalidDataException("Missing manifest.json or index.js in package.");
            }

            var manifestJson = await File.ReadAllTextAsync(manifestPath, cancellationToken);
            using var doc = JsonDocument.Parse(manifestJson);
            var id = doc.RootElement.GetProperty("name").GetString()?.ToLowerInvariant() ?? "";

            if (string.IsNullOrEmpty(id))
            {
                throw new InvalidDataException("Invalid name in manifest.json.");
            }

            var targetFolder = Path.Combine(_extensionsDir, id);
            if (Directory.Exists(targetFolder))
            {
                Directory.Delete(targetFolder, true);
            }
            Directory.CreateDirectory(targetFolder);

            ZipFile.ExtractToDirectory(tempFile, targetFolder, overwriteFiles: true);

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
        if (_activeExtensions.TryRemove(normId, out _))
        {
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
        }
        return false;
    }

    private async Task BootInstalledExtensions()
    {
        try
        {
            var dirs = Directory.GetDirectories(_extensionsDir);
            foreach (var dir in dirs)
            {
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
            var manifestPath = Path.Combine(folderPath, "manifest.json");
            var indexJsPath = Path.Combine(folderPath, "index.js");

            if (!File.Exists(manifestPath) || !File.Exists(indexJsPath)) return;

            var manifestJson = await File.ReadAllTextAsync(manifestPath);
            var indexJs = await File.ReadAllTextAsync(indexJsPath);

            var sandbox = new ExtensionSandbox(folderPath, manifestJson, indexJs, _httpClientFactory, _logger);
            _activeExtensions[sandbox.Id.ToLowerInvariant()] = sandbox;
            _logger.LogInformation("Loaded extension successfully: {DisplayName} ({Id}) v{Version}", sandbox.DisplayName, sandbox.Id, sandbox.Version);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to boot extension in folder {Path}", folderPath);
        }
    }
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
    public string RepoUrl { get; set; } = "";
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
        Id = root.GetProperty("name").GetString() ?? "";
        Name = Id;
        DisplayName = root.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? Id : Id;
        Description = root.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : "";
        Version = root.TryGetProperty("version", out var v) ? v.GetString() ?? "1.0.0" : "1.0.0";

        if (root.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in typeEl.EnumerateArray())
            {
                Types.Add(t.GetString() ?? "");
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

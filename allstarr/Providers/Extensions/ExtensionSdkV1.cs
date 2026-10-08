using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using allstarr.Core.Capabilities;
using allstarr.Core.Storage;

namespace allstarr.Core.Extensions;

public enum ExtensionPermissionKind
{
    Network,
    Cache,
    Secret
}

public sealed record ExtensionPermissionRequest(
    ExtensionPermissionKind Kind,
    string Value,
    bool Required);

public sealed record ExtensionSdkCapability(
    ProviderCapabilityKind Kind,
    IReadOnlyList<string> Hooks,
    IReadOnlyList<ProviderAccountScope> AccountScopes,
    bool AccountRequired = true);

public sealed record ExtensionSdkSetting(
    string Key,
    string Label,
    string InputType,
    string? Description,
    bool Required,
    bool Sensitive,
    string? DefaultJson,
    IReadOnlyList<string>? Choices = null);

public sealed record ExtensionSdkQualityOption(
    string Id,
    string Label,
    string? Description);

public sealed record ExtensionSignedSessionEndpoints(
    string Bootstrap,
    string Challenge,
    string Exchange,
    string? Refresh);

public sealed record ExtensionSignedSessionConfig(
    string Namespace,
    Uri BaseUrl,
    string AppVersion,
    string Platform,
    string CallbackUrl,
    string SchemeLabel,
    string HeaderPrefix,
    int TimeWindowSeconds,
    ExtensionSignedSessionEndpoints Endpoints);

public sealed record ExtensionSdkManifest(
    string Id,
    string DisplayName,
    string Version,
    string SdkVersion,
    string EntryPoint,
    IReadOnlyList<ExtensionSdkCapability> Capabilities,
    IReadOnlyList<ExtensionPermissionRequest> Permissions,
    string? Description = null,
    string? Author = null,
    string? IconPath = null,
    IReadOnlyList<ExtensionSdkSetting>? Settings = null,
    IReadOnlyList<ExtensionSdkQualityOption>? QualityOptions = null,
    IReadOnlyList<string>? RequiredRuntimeFeatures = null,
    string? Compatibility = null,
    ExtensionSignedSessionConfig? SignedSession = null);

public sealed record VerifiedExtensionPackage(
    ExtensionSdkManifest Manifest,
    string Sha256,
    long ArchiveBytes,
    long ExpandedBytes,
    int FileCount,
    string PackageRoot,
    string ContentSha256);

public sealed class ExtensionSdkValidationException(string message) : Exception(message);

public static partial class ExtensionSdkV1
{
    public const string Version = "1";
    public const long MaximumArchiveBytes = 32L * 1024 * 1024;
    public const long MaximumExpandedBytes = 128L * 1024 * 1024;
    public const int MaximumFiles = 2_000;
    private static readonly HashSet<string> AllowedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "manifest.json", "index.js", "README.md", "LICENSE", "LICENSE.md", "icon.png", "icon.jpg", "icon.jpeg", "icon.webp"
    };

    public static ExtensionSdkManifest ParseManifest(string json)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException exception) { throw new ExtensionSdkValidationException($"Extension manifest is invalid JSON: {exception.Message}"); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new ExtensionSdkValidationException("Extension manifest must be an object.");
            var id = Required(root, "id");
            if (id.Length > 128 || !ExtensionIdPattern().IsMatch(id)) throw new ExtensionSdkValidationException("Extension id must be lowercase kebab-case and at most 128 characters.");
            var sdk = Required(root, "sdkVersion");
            if (sdk != Version) throw new ExtensionSdkValidationException($"Extension sdkVersion must be {Version}.");
            var version = Required(root, "version");
            if (version.Length > 100 || !SemanticVersionPattern().IsMatch(version)) throw new ExtensionSdkValidationException("Extension version must be semantic version text of at most 100 characters.");
            var entryPoint = Required(root, "entryPoint");
            if (entryPoint != "index.js") throw new ExtensionSdkValidationException("SDK v1 entryPoint must be index.js.");
            var displayName = Optional(root, "displayName") ?? id;
            if (displayName.Length > 100) throw new ExtensionSdkValidationException("Extension displayName is limited to 100 characters.");
            var capabilities = ParseCapabilities(root);
            if (capabilities.Count == 0) throw new ExtensionSdkValidationException("At least one typed capability is required.");
            var permissions = ParsePermissions(root);
            var description = Optional(root, "description");
            if (description?.Length > 500) throw new ExtensionSdkValidationException("Extension description is limited to 500 characters.");
            var author = Optional(root, "author");
            if (author?.Length > 200) throw new ExtensionSdkValidationException("Extension author is limited to 200 characters.");
            var iconPath = ParseIconPath(root);
            var settings = ParseSettings(root);
            var qualityOptions = ParseQualityOptions(root);
            var runtimeFeatures = OptionalStringArray(root, "requiredRuntimeFeatures", 64, 100);
            return new(id, displayName, version, sdk, entryPoint, capabilities, permissions,
                description, author, iconPath, settings, qualityOptions, runtimeFeatures,
                Optional(root, "compatibility"), ParseSignedSession(root));
        }
    }

    private static ExtensionSignedSessionConfig? ParseSignedSession(JsonElement root)
    {
        if ((!root.TryGetProperty("signedSession", out var value) || value.ValueKind == JsonValueKind.Null) &&
            root.TryGetProperty("spotiflacManifest", out var original) && original.ValueKind == JsonValueKind.Object)
            original.TryGetProperty("signedSession", out value);
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Object)
            throw new ExtensionSdkValidationException("signedSession must be an object.");
        var ns = Required(value, "namespace");
        if (!Regex.IsMatch(ns, "^[a-zA-Z0-9._-]{1,100}$", RegexOptions.CultureInvariant))
            throw new ExtensionSdkValidationException("signedSession namespace is invalid.");
        var baseUrlText = Required(value, "baseUrl");
        if (!Uri.TryCreate(baseUrlText, UriKind.Absolute, out var baseUrl) ||
            baseUrl.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(baseUrl.Host))
            throw new ExtensionSdkValidationException("signedSession baseUrl must be an absolute HTTPS URL.");
        var appVersion = Optional(value, "appVersion") ?? "ext-1.0";
        var platform = Optional(value, "platform") ?? "extension";
        var callbackUrl = Optional(value, "callbackUrl") ?? "spotiflac://session-grant";
        var schemeLabel = Optional(value, "schemeLabel") ?? "SPOTIFLAC-HMAC-V1";
        var headerPrefix = Optional(value, "headerPrefix") ?? "X-Sig-";
        var window = value.TryGetProperty("timeWindowSeconds", out var windowValue) && windowValue.TryGetInt32(out var parsedWindow)
            ? parsedWindow : 300;
        if (window is < 30 or > 3600)
            throw new ExtensionSdkValidationException("signedSession timeWindowSeconds must be between 30 and 3600.");
        var endpointsValue = value.TryGetProperty("endpoints", out var endpoints) && endpoints.ValueKind == JsonValueKind.Object
            ? endpoints : default;
        string Endpoint(string name, string fallback) => endpointsValue.ValueKind == JsonValueKind.Object
            ? Optional(endpointsValue, name) ?? fallback : fallback;
        var resolvedEndpoints = new ExtensionSignedSessionEndpoints(
            Endpoint("bootstrap", "/bootstrap"),
            Endpoint("challenge", "/challenge"),
            Endpoint("exchange", "/session/exchange"),
            Endpoint("refresh", string.Empty) is { Length: > 0 } refresh ? refresh : null);
        foreach (var endpoint in new[] { resolvedEndpoints.Bootstrap, resolvedEndpoints.Challenge, resolvedEndpoints.Exchange, resolvedEndpoints.Refresh })
        {
            if (endpoint == null) continue;
            if (endpoint.Contains("://", StringComparison.Ordinal) &&
                Uri.TryCreate(endpoint, UriKind.Absolute, out var absolute) && absolute.Scheme != Uri.UriSchemeHttps)
                throw new ExtensionSdkValidationException("signedSession endpoints must use HTTPS.");
        }
        return new(ns, baseUrl, appVersion, platform, callbackUrl, schemeLabel, headerPrefix, window, resolvedEndpoints);
    }

    public static VerifiedExtensionPackage VerifyArchive(
        string archivePath,
        string expectedSha256,
        string extractionRoot)
    {
        if (!File.Exists(archivePath)) throw new FileNotFoundException("Extension package was not found.", archivePath);
        var archiveLength = new FileInfo(archivePath).Length;
        if (archiveLength is <= 0 or > MaximumArchiveBytes) throw new ExtensionSdkValidationException("Extension archive size is outside SDK v1 limits.");
        using var archiveHashStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var actualSha256 = Convert.ToHexString(SHA256.HashData(archiveHashStream)).ToLowerInvariant();
        if (!Sha256Pattern().IsMatch(expectedSha256) || !CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actualSha256), Convert.FromHexString(expectedSha256)))
            throw new ExtensionSdkValidationException("Extension package checksum does not match the registry entry.");
        Directory.CreateDirectory(extractionRoot);
        var extractionFull = Path.GetFullPath(extractionRoot);
        long expanded = 0;
        var count = 0;
        using (var archive = ZipFile.OpenRead(archivePath))
        {
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                count++;
                expanded = checked(expanded + entry.Length);
                if (count > MaximumFiles || expanded > MaximumExpandedBytes)
                    throw new ExtensionSdkValidationException("Extension package exceeds expanded size or file-count limits.");
                var relative = entry.FullName.Replace('\\', '/');
                if (relative.StartsWith('/') || relative.Split('/').Any(segment => segment is "" or "." or ".."))
                    throw new ExtensionSdkValidationException("Extension package contains an unsafe path.");
                if (!AllowedFiles.Contains(relative) && !relative.StartsWith("assets/", StringComparison.Ordinal))
                    throw new ExtensionSdkValidationException("Extension package contains a file outside the SDK v1 package layout.");
                var target = Path.GetFullPath(Path.Combine(extractionFull, relative));
                if (!target.StartsWith(extractionFull + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw new ExtensionSdkValidationException("Extension package path escapes staging.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: false);
            }
        }
        var manifestPath = Path.Combine(extractionFull, "manifest.json");
        var entryPointPath = Path.Combine(extractionFull, "index.js");
        if (!File.Exists(manifestPath) || !File.Exists(entryPointPath))
            throw new ExtensionSdkValidationException("Extension package requires manifest.json and index.js at its root.");
        var manifestJson = File.ReadAllText(manifestPath);
        if (SpotiFlacExtensionCompatibility.IsManifest(manifestJson))
        {
            manifestJson = SpotiFlacExtensionCompatibility.NormalizeManifest(manifestJson, File.ReadAllText(entryPointPath));
            File.WriteAllText(manifestPath, manifestJson);
        }
        var manifest = ParseManifest(manifestJson);
        return new(manifest, actualSha256, archiveLength, expanded, count, extractionFull,
            ComputePackageContentSha256(extractionFull));
    }

    public static string ComputePackageContentSha256(string packageRoot)
    {
        var root = Path.GetFullPath(packageRoot);
        if (!Directory.Exists(root))
            throw new ExtensionSdkValidationException("Extension package contents are unavailable.");

        var files = new List<string>();
        var directories = new Stack<string>();
        directories.Push(root);
        long expandedBytes = 0;
        while (directories.Count != 0)
        {
            var directory = directories.Pop();
            foreach (var child in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new ExtensionSdkValidationException("Extension package contains a symbolic link.");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    directories.Push(child);
                    continue;
                }
                files.Add(child);
                expandedBytes = checked(expandedBytes + new FileInfo(child).Length);
                if (files.Count > MaximumFiles || expandedBytes > MaximumExpandedBytes)
                    throw new ExtensionSdkValidationException("Extension package contents exceed SDK v1 limits.");
            }
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in files
                     .OrderBy(item => Path.GetRelativePath(root, item).Replace('\\', '/'), StringComparer.Ordinal))
        {
            var info = new FileInfo(path);
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (!AllowedFiles.Contains(relative) && !relative.StartsWith("assets/", StringComparison.Ordinal))
                throw new ExtensionSdkValidationException("Extension package contents no longer match the SDK v1 layout.");
            var name = System.Text.Encoding.UTF8.GetBytes(relative);
            hash.AppendData(BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(name.Length)));
            hash.AppendData(name);
            hash.AppendData(BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(info.Length)));
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static IReadOnlyList<ExtensionSdkCapability> ParseCapabilities(JsonElement root)
    {
        if (!root.TryGetProperty("capabilities", out var values) || values.ValueKind != JsonValueKind.Array)
            throw new ExtensionSdkValidationException("capabilities must be an array.");
        var result = new List<ExtensionSdkCapability>();
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Object ||
                !Enum.TryParse<ProviderCapabilityKind>(Required(value, "kind"), true, out var kind) ||
                !Enum.IsDefined(kind))
                throw new ExtensionSdkValidationException("Capability kind is unsupported.");
            var hooks = StringArray(value, "hooks").Select(hook => ProviderContractValidation.HookName(hook, "hooks")).ToArray();
            var allowed = ProviderManifestValidator.GetAllowedHooks(kind);
            if (hooks.Length == 0 || hooks.Distinct(StringComparer.Ordinal).Count() != hooks.Length ||
                hooks.Any(hook => !allowed.Contains(hook)))
                throw new ExtensionSdkValidationException("Capability hooks do not match the declared kind.");
            var scopes = StringArray(value, "accountScopes").Select(scope =>
                Enum.TryParse<ProviderAccountScope>(scope, true, out var parsed) && Enum.IsDefined(parsed)
                    ? parsed
                    : throw new ExtensionSdkValidationException("Capability account scope is unsupported.")).ToArray();
            var accountRequired = !value.TryGetProperty("accountRequired", out var required) || required.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new ExtensionSdkValidationException("Capability accountRequired must be a boolean.")
            };
            if (scopes.Distinct().Count() != scopes.Length || accountRequired && scopes.Length == 0)
                throw new ExtensionSdkValidationException(
                    "Capability accountScopes must be unique and non-empty when an account is required.");
            result.Add(new(kind, hooks, scopes, accountRequired));
        }
        if (result.Select(item => item.Kind).Distinct().Count() != result.Count)
            throw new ExtensionSdkValidationException("Each capability kind may be declared once.");
        return result;
    }

    private static string? ParseIconPath(JsonElement root)
    {
        var icon = Optional(root, "icon");
        if (icon == null) return null;
        var normalized = icon.Replace('\\', '/');
        if (normalized.Length > 300 || Path.IsPathRooted(normalized) || normalized.Contains(':') ||
            normalized.Split('/').Any(segment => segment is "" or "." or "..") ||
            !new[] { ".png", ".jpg", ".jpeg", ".webp" }.Contains(
                Path.GetExtension(normalized), StringComparer.OrdinalIgnoreCase))
            throw new ExtensionSdkValidationException("Extension icon must be a safe PNG, JPEG, or WebP package path.");
        return normalized;
    }

    private static IReadOnlyList<ExtensionSdkSetting> ParseSettings(JsonElement root)
    {
        if (!root.TryGetProperty("settings", out var values)) return [];
        if (values.ValueKind != JsonValueKind.Array)
            throw new ExtensionSdkValidationException("Extension settings must be an array.");
        var result = new List<ExtensionSdkSetting>();
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Object)
                throw new ExtensionSdkValidationException("Extension settings must contain objects.");
            var key = Required(value, "key");
            if (!SettingKeyPattern().IsMatch(key))
                throw new ExtensionSdkValidationException("Extension setting keys must use lower camel-case.");
            var inputType = Optional(value, "type")?.ToLowerInvariant() ?? "text";
            if (inputType.Length > 50)
                throw new ExtensionSdkValidationException("Extension setting type is too long.");
            var label = Optional(value, "label") ?? key;
            var description = Optional(value, "description") ?? Optional(value, "helpText");
            var required = value.TryGetProperty("required", out var requiredValue) && requiredValue.ValueKind == JsonValueKind.True;
            var sensitive = value.TryGetProperty("secret", out var secretValue) && secretValue.ValueKind == JsonValueKind.True ||
                            inputType is "password" or "secret" or "token" ||
                            SensitiveSettingKeyPattern().IsMatch(key);
            var defaultJson = value.TryGetProperty("default", out var defaultValue)
                ? defaultValue.GetRawText()
                : null;
            var choices = OptionalStringArray(value, "options", 64, 100);
            result.Add(new(key, label, inputType, description, required, sensitive, defaultJson, choices));
        }
        if (result.Count > 64 || result.Select(item => item.Key).Distinct(StringComparer.Ordinal).Count() != result.Count)
            throw new ExtensionSdkValidationException("Extension settings must be unique and are limited to 64 entries.");
        return result;
    }

    private static IReadOnlyList<ExtensionSdkQualityOption> ParseQualityOptions(JsonElement root)
    {
        if (!root.TryGetProperty("qualityOptions", out var values)) return [];
        if (values.ValueKind != JsonValueKind.Array)
            throw new ExtensionSdkValidationException("Extension qualityOptions must be an array.");
        var result = new List<ExtensionSdkQualityOption>();
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind == JsonValueKind.String)
            {
                var id = value.GetString()!;
                result.Add(new(id, id, null));
                continue;
            }
            if (value.ValueKind != JsonValueKind.Object)
                throw new ExtensionSdkValidationException("Extension quality options must be strings or objects.");
            var idValue = Optional(value, "id") ?? Optional(value, "value") ?? Required(value, "name");
            result.Add(new(idValue, Optional(value, "label") ?? Optional(value, "displayName") ?? idValue,
                Optional(value, "description")));
        }
        if (result.Count > 32 || result.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != result.Count)
            throw new ExtensionSdkValidationException("Extension quality options must be unique and are limited to 32 entries.");
        return result;
    }

    private static IReadOnlyList<string> OptionalStringArray(JsonElement root, string name, int maximumItems, int maximumLength)
    {
        if (!root.TryGetProperty(name, out var value)) return [];
        if (value.ValueKind != JsonValueKind.Array)
            throw new ExtensionSdkValidationException($"Manifest field '{name}' must be an array of strings.");
        var result = value.EnumerateArray().Select(item =>
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                throw new ExtensionSdkValidationException($"Manifest field '{name}' must contain non-empty strings.");
            var text = item.GetString()!.Trim();
            if (text.Length > maximumLength)
                throw new ExtensionSdkValidationException($"Manifest field '{name}' contains an overlong value.");
            return text;
        }).ToArray();
        if (result.Length > maximumItems || result.Distinct(StringComparer.Ordinal).Count() != result.Length)
            throw new ExtensionSdkValidationException($"Manifest field '{name}' contains too many or duplicate values.");
        return result;
    }

    private static IReadOnlyList<ExtensionPermissionRequest> ParsePermissions(JsonElement root)
    {
        if (!root.TryGetProperty("permissions", out var values) || values.ValueKind != JsonValueKind.Array)
            throw new ExtensionSdkValidationException("permissions must be an array, even when empty.");
        var result = new List<ExtensionPermissionRequest>();
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Object ||
                !Enum.TryParse<ExtensionPermissionKind>(Required(value, "kind"), true, out var kind) ||
                !Enum.IsDefined(kind))
                throw new ExtensionSdkValidationException("Permission kind is unsupported.");
            var permissionValue = Required(value, "value");
            if (kind == ExtensionPermissionKind.Network)
            {
                if (WildcardOriginPattern().IsMatch(permissionValue))
                {
                    permissionValue = permissionValue.ToLowerInvariant();
                }
                else
                {
                    if (!Uri.TryCreate(permissionValue, UriKind.Absolute, out var origin) || origin.Scheme != Uri.UriSchemeHttps ||
                        origin.PathAndQuery != "/" || !string.IsNullOrEmpty(origin.Fragment) || !string.IsNullOrEmpty(origin.UserInfo))
                        throw new ExtensionSdkValidationException("Network permissions must be HTTPS origins without paths or credentials.");
                    permissionValue = origin.GetLeftPart(UriPartial.Authority) + "/";
                }
            }
            else if (permissionValue != "*" && !SettingKeyPattern().IsMatch(permissionValue))
                throw new ExtensionSdkValidationException("Cache and secret permissions must use lower camel-case setting keys.");
            var required = value.TryGetProperty("required", out var requiredValue) && requiredValue.ValueKind == JsonValueKind.True;
            result.Add(new(kind, permissionValue, required));
        }
        if (result.Select(item => (item.Kind, item.Value)).Distinct().Count() != result.Count)
            throw new ExtensionSdkValidationException("Duplicate permissions are not allowed.");
        if (result.Count(item => item.Kind == ExtensionPermissionKind.Network) > 32 ||
            result.Count(item => item.Kind == ExtensionPermissionKind.Cache) > 64 ||
            result.Count(item => item.Kind == ExtensionPermissionKind.Secret) > 64)
            throw new ExtensionSdkValidationException("Extension permission count exceeds SDK v1 limits.");
        return result;
    }

    private static string Required(JsonElement root, string name) =>
        Optional(root, name) ?? throw new ExtensionSdkValidationException($"Manifest field '{name}' is required.");
    private static string? Optional(JsonElement root, string name) => root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()!.Trim() : null;
    private static IReadOnlyList<string> StringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            throw new ExtensionSdkValidationException($"Manifest field '{name}' must be an array of strings.");
        var result = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                throw new ExtensionSdkValidationException($"Manifest field '{name}' must contain non-empty strings.");
            result.Add(item.GetString()!.Trim());
        }
        return result;
    }

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex ExtensionIdPattern();
    [GeneratedRegex("^[0-9]+\\.[0-9]+\\.[0-9]+(?:-[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SemanticVersionPattern();
    [GeneratedRegex("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();
    [GeneratedRegex("^https://\\*\\.[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?/$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex WildcardOriginPattern();
    [GeneratedRegex("^[a-z][A-Za-z0-9]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SettingKeyPattern();
    [GeneratedRegex("(?i)(password|secret|token|cookie|apiKey|mediaUserToken)$", RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveSettingKeyPattern();
}

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

public sealed record ExtensionSdkManifest(
    string Id,
    string DisplayName,
    string Version,
    string SdkVersion,
    string EntryPoint,
    IReadOnlyList<ExtensionSdkCapability> Capabilities,
    IReadOnlyList<ExtensionPermissionRequest> Permissions);

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
            return new(id, displayName, version, sdk, entryPoint, capabilities, permissions);
        }
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
            if (scopes.Length == 0 || scopes.Distinct().Count() != scopes.Length)
                throw new ExtensionSdkValidationException("Capability accountScopes cannot be empty or contain duplicates.");
            var accountRequired = !value.TryGetProperty("accountRequired", out var required) || required.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new ExtensionSdkValidationException("Capability accountRequired must be a boolean.")
            };
            result.Add(new(kind, hooks, scopes, accountRequired));
        }
        if (result.Select(item => item.Kind).Distinct().Count() != result.Count)
            throw new ExtensionSdkValidationException("Each capability kind may be declared once.");
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
}

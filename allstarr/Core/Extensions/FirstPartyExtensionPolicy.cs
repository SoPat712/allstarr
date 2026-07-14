using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using allstarr.Core.Capabilities;
using allstarr.Core.Storage;

namespace allstarr.Core.Extensions;

public sealed record FirstPartyLockedPackage(
    string Id, string Version, string ArchiveFile, string ArchiveSha256, string ContentSha256);

public sealed partial class FirstPartyExtensionPolicy
{
    private readonly string? _lockPath;

    public FirstPartyExtensionPolicy(IConfiguration configuration)
    {
        var configured = configuration["Extensions:FirstPartyBundleLockPath"];
        _lockPath = string.IsNullOrWhiteSpace(configured) ? null : Path.GetFullPath(configured);
    }

    public bool IsApprovedReplacement(ExtensionPackageRecord package)
    {
        return ReadPackages(includeRollback: true).Any(candidate =>
            candidate.Id == package.ExtensionId && candidate.Version == package.Version &&
            FixedHash(candidate.ArchiveSha256, package.Sha256) &&
            FixedHash(candidate.ContentSha256, package.ContentSha256));
    }

    public IReadOnlyList<FirstPartyLockedPackage> ReadyPackages() => ReadPackages(includeRollback: false);

    private IReadOnlyList<FirstPartyLockedPackage> ReadPackages(bool includeRollback)
    {
        if (_lockPath == null || !File.Exists(_lockPath)) return [];
        using var stream = File.OpenRead(_lockPath);
        using var document = JsonDocument.Parse(stream);
        if (!document.RootElement.TryGetProperty("schemaVersion", out var schema) || schema.GetInt32() != 1 ||
            !document.RootElement.TryGetProperty("sdkVersion", out var sdk) || sdk.GetString() != "1" ||
            !document.RootElement.TryGetProperty("packages", out var packages) || packages.ValueKind != JsonValueKind.Array)
            throw new ExtensionSdkValidationException("The first-party bundle lock is not a supported v1 lock.");
        var results = new List<FirstPartyLockedPackage>();
        foreach (var candidate in packages.EnumerateArray())
        {
            var activation = Required(candidate, "activation");
            if (activation != "ready" && !(includeRollback && activation == "rollback")) continue;
            var id = ProviderContractValidation.ProviderId(Required(candidate, "id"), "first-party bundle provider ID");
            var version = Required(candidate, "version");
            if (!VersionPattern().IsMatch(version))
                throw new ExtensionSdkValidationException("A first-party bundle version is invalid.");
            var archiveFile = Required(candidate, "archiveFile");
            if (archiveFile != Path.GetFileName(archiveFile))
                throw new ExtensionSdkValidationException("A first-party archive path is not a safe file name.");
            var archiveHash = ValidHash(Required(candidate, "archiveSha256"));
            var contentHash = ValidHash(Required(candidate, "contentSha256"));
            results.Add(new(id, version, archiveFile, archiveHash, contentHash));
        }
        return results;
    }

    public string ResolveArchivePath(FirstPartyLockedPackage package) => Path.Combine(
        Path.GetDirectoryName(_lockPath ?? throw new InvalidOperationException("A first-party bundle lock is not configured."))!,
        package.ArchiveFile);

    private static bool Equals(JsonElement value, string property, string expected) =>
        value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String &&
        string.Equals(item.GetString(), expected, StringComparison.Ordinal);

    private static string Required(JsonElement value, string property) =>
        value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString())
            ? item.GetString()!
            : throw new ExtensionSdkValidationException($"The first-party bundle lock is missing '{property}'.");

    private static bool FixedHash(string expected, string actual)
    {
        if (expected?.Length != 64 || actual.Length != 64) return false;
        try
        {
            return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(actual));
        }
        catch (FormatException) { return false; }
    }

    private static string ValidHash(string value)
    {
        if (value.Length != 64) throw new ExtensionSdkValidationException("A first-party bundle hash is invalid.");
        try { _ = Convert.FromHexString(value); }
        catch (FormatException) { throw new ExtensionSdkValidationException("A first-party bundle hash is invalid."); }
        return value.ToLowerInvariant();
    }

    [GeneratedRegex("^[0-9A-Za-z][0-9A-Za-z.+-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}

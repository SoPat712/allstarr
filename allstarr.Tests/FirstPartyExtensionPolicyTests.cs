using System.Text.Json;
using allstarr.Core.Extensions;
using allstarr.Core.Storage;
using Microsoft.Extensions.Configuration;

namespace allstarr.Tests;

public sealed class FirstPartyExtensionPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "allstarr-first-party-policy", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ReplacementRequiresReadyStateAndBothImmutableHashes()
    {
        Directory.CreateDirectory(_root);
        var archive = new string('a', 64);
        var content = new string('b', 64);
        var lockPath = Path.Combine(_root, "bundle.lock.json");
        WriteLock(lockPath, "ready", archive, content);
        var policy = Policy(lockPath);
        var package = Package(archive, content);

        Assert.True(policy.IsApprovedReplacement(package));
        package.ContentSha256 = new string('c', 64);
        Assert.False(policy.IsApprovedReplacement(package));
        WriteLock(lockPath, "blocked-built-in-switchover-required", archive, content);
        Assert.False(policy.IsApprovedReplacement(Package(archive, content)));
    }

    [Fact]
    public void MissingOrMalformedLockNeverApprovesReplacement()
    {
        Directory.CreateDirectory(_root);
        var missing = Policy(Path.Combine(_root, "missing.json"));
        Assert.False(missing.IsApprovedReplacement(Package(new string('a', 64), new string('b', 64))));
        var malformedPath = Path.Combine(_root, "malformed.json");
        File.WriteAllText(malformedPath, "{}");
        Assert.Throws<ExtensionSdkValidationException>(() => Policy(malformedPath)
            .IsApprovedReplacement(Package(new string('a', 64), new string('b', 64))));
    }

    [Fact]
    public void RollbackEntriesMayRestoreButAreNotBootstrapped()
    {
        Directory.CreateDirectory(_root);
        var archive = new string('a', 64);
        var content = new string('b', 64);
        var lockPath = Path.Combine(_root, "bundle.lock.json");
        WriteLock(lockPath, "rollback", archive, content);
        var policy = Policy(lockPath);
        Assert.True(policy.IsApprovedReplacement(Package(archive, content)));
        Assert.Empty(policy.ReadyPackages());
    }

    [Fact]
    public void InvalidLockedHashFailsBeforeBootstrapUsesIt()
    {
        Directory.CreateDirectory(_root);
        var lockPath = Path.Combine(_root, "bundle.lock.json");
        WriteLock(lockPath, "ready", "not-a-hash", new string('b', 64));
        Assert.Throws<ExtensionSdkValidationException>(() => Policy(lockPath).ReadyPackages());
    }

    private FirstPartyExtensionPolicy Policy(string path) => new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Extensions:FirstPartyBundleLockPath"] = path }).Build());

    private static ExtensionPackageRecord Package(string archive, string content) => new()
    {
        ExtensionId = "deezer", Version = "1.0.0", Sha256 = archive, ContentSha256 = content
    };

    private static void WriteLock(string path, string activation, string archive, string content) =>
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            schemaVersion = 1, sdkVersion = "1",
            packages = new[] { new { id = "deezer", version = "1.0.0", activation, archiveFile = "deezer-1.0.0.zip", archiveSha256 = archive, contentSha256 = content } }
        }));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}

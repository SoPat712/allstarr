using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Extensions;
using allstarr.Core.Storage;

namespace allstarr.Tests;

public sealed class ExtensionSdkV1Tests : IDisposable
{
    [Fact]
    public void ParseManifest_ValidatesSignedSessionConfiguration()
    {
        var manifest = ExtensionSdkV1.ParseManifest("""
            {
              "id":"signed-demo","displayName":"Signed demo","version":"1.0.0","sdkVersion":"1","entryPoint":"index.js",
              "capabilities":[{"kind":"Metadata","hooks":["searchTracks"],"accountScopes":[],"accountRequired":false}],
              "permissions":[{"kind":"Network","value":"https://api.example.test/","required":true}],
              "requiredRuntimeFeatures":["signedSession@1","sessionGrant@1"],
              "signedSession":{"namespace":"demo-v1","baseUrl":"https://api.example.test/v1","appVersion":"demo@1.0.0","headerPrefix":"X-Demo-","endpoints":{"refresh":"/session/refresh"}}
            }
            """);

        Assert.NotNull(manifest.SignedSession);
        Assert.Equal("demo-v1", manifest.SignedSession.Namespace);
        Assert.Equal("X-Demo-", manifest.SignedSession.HeaderPrefix);
        Assert.Equal("/session/refresh", manifest.SignedSession.Endpoints.Refresh);
        Assert.Equal(300, manifest.SignedSession.TimeWindowSeconds);
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "allstarr-extension-sdk", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Manifest_RequiresTypedHooksScopesAndReviewablePermissions()
    {
        var manifest = ExtensionSdkV1.ParseManifest(Manifest());

        Assert.Equal("fixture-provider", manifest.Id);
        Assert.Equal("1", manifest.SdkVersion);
        Assert.Single(manifest.Capabilities);
        Assert.Equal("https://api.example.test/", manifest.Permissions.Single(item => item.Kind == ExtensionPermissionKind.Network).Value);
        Assert.Contains(manifest.Permissions, item => item is { Kind: ExtensionPermissionKind.Secret, Value: "accountToken", Required: true });
    }

    [Fact]
    public void Manifest_AllowsPublicCapabilityToOptOutOfAccountRequirement()
    {
        var manifest = ExtensionSdkV1.ParseManifest(Manifest().Replace(
            "\"accountScopes\":[\"user\"]", "\"accountScopes\":[],\"accountRequired\":false"));

        Assert.False(manifest.Capabilities.Single().AccountRequired);
        Assert.Empty(manifest.Capabilities.Single().AccountScopes);
    }

    [Theory]
    [InlineData("../index.js")]
    [InlineData("https://api.example.test/v1")]
    [InlineData("http://api.example.test/")]
    public void Manifest_RejectsUnsafeEntryPointOrNetworkPermission(string value)
    {
        var json = value.EndsWith("index.js", StringComparison.Ordinal)
            ? Manifest().Replace("\"entryPoint\":\"index.js\"", $"\"entryPoint\":\"{value}\"")
            : Manifest().Replace("https://api.example.test/", value);

        Assert.Throws<ExtensionSdkValidationException>(() => ExtensionSdkV1.ParseManifest(json));
    }

    [Fact]
    public void Manifest_RejectsUnboundedDisplayNamesAndPermissionSets()
    {
        Assert.Throws<ExtensionSdkValidationException>(() => ExtensionSdkV1.ParseManifest(
            Manifest().Replace("Fixture", new string('x', 101), StringComparison.Ordinal)));
        var permissions = string.Join(',', Enumerable.Range(0, 65)
            .Select(index => $$"""{"kind":"cache","value":"cacheKey{{index}}","required":false}"""));
        var json = Manifest().Replace(
            "[{\"kind\":\"network\",\"value\":\"https://api.example.test/\",\"required\":true},{\"kind\":\"secret\",\"value\":\"accountToken\",\"required\":true},{\"kind\":\"cache\",\"value\":\"metadataCache\",\"required\":false}]",
            $"[{permissions}]", StringComparison.Ordinal);
        Assert.Throws<ExtensionSdkValidationException>(() => ExtensionSdkV1.ParseManifest(json));
    }

    [Fact]
    public void VerifyArchive_RequiresExactChecksumAndSafeBoundedLayout()
    {
        Directory.CreateDirectory(_root);
        var archivePath = Path.Combine(_root, "package.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            Write(archive, "manifest.json", Manifest());
            Write(archive, "index.js", "registerExtension({});");
            Write(archive, "assets/icon.svg", "<svg/>");
        }
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archivePath))).ToLowerInvariant();

        var verified = ExtensionSdkV1.VerifyArchive(archivePath, hash, Path.Combine(_root, "staged"));

        Assert.Equal(hash, verified.Sha256);
        Assert.Equal(64, verified.ContentSha256.Length);
        File.AppendAllText(Path.Combine(verified.PackageRoot, "index.js"), "// tampered");
        Assert.NotEqual(verified.ContentSha256,
            ExtensionSdkV1.ComputePackageContentSha256(verified.PackageRoot));
        Assert.Equal(3, verified.FileCount);
        Assert.Throws<ExtensionSdkValidationException>(() =>
            ExtensionSdkV1.VerifyArchive(archivePath, new string('0', 64), Path.Combine(_root, "bad")));
    }

    [Fact]
    public void VerifyArchive_RejectsTraversalAndUndeclaredExecutableLayout()
    {
        Directory.CreateDirectory(_root);
        foreach (var name in new[] { "../escape.js", "scripts/install.sh" })
        {
            var archivePath = Path.Combine(_root, $"{Guid.NewGuid():N}.zip");
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                Write(archive, "manifest.json", Manifest());
                Write(archive, "index.js", "registerExtension({});");
                Write(archive, name, "bad");
            }
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archivePath))).ToLowerInvariant();
            Assert.Throws<ExtensionSdkValidationException>(() =>
                ExtensionSdkV1.VerifyArchive(archivePath, hash, Path.Combine(_root, Guid.NewGuid().ToString("N"))));
        }
    }

    [Fact]
    public void VerifyArchive_AdaptsSpotiFlacManifestAndIconLayout()
    {
        Directory.CreateDirectory(_root);
        var archivePath = Path.Combine(_root, "spotiflac.sflx");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            Write(archive, "manifest.json", """
                {"name":"demo","displayName":"Demo","version":"1.2.3","description":"Fixture",
                 "author":"Example","icon":"icon.jpg","type":["metadata_provider"],
                 "settings":[{"key":"mediaUserToken","label":"Media user token","type":"password","required":true},
                             {"key":"storefront","label":"Storefront","description":"Apple Music storefront code (e.g. us, gb, jp, id).","type":"select","options":["us","ca"],"default":"us"}],
                 "qualityOptions":[{"id":"lossless","label":"Lossless"}],
                 "permissions":{"network":["api.example.test","*.example.test"],"storage":true}}
                """);
            Write(archive, "index.js", "registerExtension({customSearch:function(){return [];},getTrack:function(id){return {id:id,name:'Demo',artists:['Artist']};}});");
            Write(archive, "icon.jpg", "image");
        }
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archivePath))).ToLowerInvariant();

        var verified = ExtensionSdkV1.VerifyArchive(archivePath, hash, Path.Combine(_root, "spotiflac-staged"));

        Assert.Equal("spotiflac-demo", verified.Manifest.Id);
        Assert.All(verified.Manifest.Capabilities, capability =>
        {
            Assert.True(capability.AccountRequired);
            Assert.Contains(ProviderAccountScope.User, capability.AccountScopes);
        });
        Assert.Equal("icon.jpg", verified.Manifest.IconPath);
        Assert.Equal("Example", verified.Manifest.Author);
        Assert.Equal(2, verified.Manifest.Settings!.Count);
        Assert.True(verified.Manifest.Settings.Single(item => item.Key == "mediaUserToken").Sensitive);
        Assert.Equal(["us", "ca"], verified.Manifest.Settings.Single(item => item.Key == "storefront").Choices);
        Assert.Equal("Apple Music storefront code (e.g. us, gb, jp, id).",
            verified.Manifest.Settings.Single(item => item.Key == "storefront").Description);
        Assert.Equal("lossless", Assert.Single(verified.Manifest.QualityOptions!).Id);
        Assert.Contains(verified.Manifest.Permissions, item => item is
        { Kind: ExtensionPermissionKind.Secret, Value: "mediaUserToken", Required: true });
        Assert.Contains(verified.Manifest.Permissions, item => item is { Kind: ExtensionPermissionKind.Cache, Value: "*" });
        Assert.Contains(verified.Manifest.Permissions, item => item.Value == "https://*.example.test/");
        Assert.True(SpotiFlacExtensionCompatibility.IsNormalizedManifest(
            File.ReadAllText(Path.Combine(verified.PackageRoot, "manifest.json"))));
    }

    private static string Manifest() => """
        {"id":"fixture-provider","displayName":"Fixture","version":"1.2.3","sdkVersion":"1","entryPoint":"index.js",
         "capabilities":[{"kind":"metadata","hooks":["searchTracks","getTrack"],"accountScopes":["user"]}],
         "permissions":[{"kind":"network","value":"https://api.example.test/","required":true},{"kind":"secret","value":"accountToken","required":true},{"kind":"cache","value":"metadataCache","required":false}]}
        """;

    private static void Write(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}

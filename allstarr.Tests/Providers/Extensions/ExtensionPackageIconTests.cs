using System.Text.Json;
using allstarr.Core.Storage;
using allstarr.Services.Common;

namespace allstarr.Tests;

public sealed class ExtensionPackageIconTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "allstarr-package-icon", Guid.NewGuid().ToString("N"));
    private static readonly byte[] PngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aH3cAAAAASUVORK5CYII=");

    [Fact]
    public void Resolve_PrefersTheManifestIconOverDefaultNames()
    {
        var package = Package("assets/custom.JPG");
        Write("icon.png", PngBytes);
        var expected = Write("assets/custom.JPG", [1, 2, 3]);

        var icon = ExtensionPackageIcon.Resolve(package);

        Assert.NotNull(icon);
        Assert.Equal(expected, icon.Value.Path);
        Assert.Equal("image/jpeg", icon.Value.ContentType);
    }

    [Theory]
    [InlineData("icon.png", "image/png")]
    [InlineData("icon.jpg", "image/jpeg")]
    [InlineData("icon.jpeg", "image/jpeg")]
    [InlineData("icon.webp", "image/webp")]
    public void Resolve_UsesTheFirstAvailableDefaultName(string name, string contentType)
    {
        var package = Package();
        var expected = Write(name, [1]);
        if (name != "icon.webp") Write("icon.webp", [2]);

        var icon = ExtensionPackageIcon.Resolve(package);

        Assert.NotNull(icon);
        Assert.Equal(expected, icon.Value.Path);
        Assert.Equal(contentType, icon.Value.ContentType);
    }

    [Theory]
    [InlineData("../outside.png")]
    [InlineData("assets/../../outside.png")]
    [InlineData("assets\\..\\outside.png")]
    [InlineData("/outside.png")]
    public async Task Resolve_RejectsTraversalAndRootedManifestPaths(string path)
    {
        var package = Package(path);
        Write("icon.png", PngBytes);

        Assert.Null(ExtensionPackageIcon.Resolve(package));
        Assert.Null(await ExtensionPackageIcon.ReadAsync(package, default));
    }

    [Fact]
    public async Task ReadAsync_ReturnsThePngBytesAndContentType()
    {
        var package = Package();
        Write("icon.png", PngBytes);

        var icon = await ExtensionPackageIcon.ReadAsync(package, default);

        Assert.NotNull(icon);
        Assert.Equal(PngBytes, icon.Bytes);
        Assert.Equal("image/png", icon.ContentType);
    }

    [Fact]
    public async Task ReadAsync_RejectsAnOversizeIcon()
    {
        var package = Package();
        var path = Write("icon.png", PngBytes);
        using (var stream = File.OpenWrite(path)) stream.SetLength(2 * 1024 * 1024 + 1);

        Assert.Null(await ExtensionPackageIcon.ReadAsync(package, default));
    }

    [Fact]
    public async Task ReadAsync_ReturnsNullForAMissingOrEmptyIcon()
    {
        var package = Package();
        Assert.Null(await ExtensionPackageIcon.ReadAsync(package, default));
        Write("icon.png", []);
        Assert.Null(await ExtensionPackageIcon.ReadAsync(package, default));
    }

    private ExtensionPackageRecord Package(string? icon = null)
    {
        Directory.CreateDirectory(_root);
        return new()
        {
            PackagePath = _root,
            ManifestJson = JsonSerializer.Serialize(new
            {
                id = "fixture-extension",
                version = "1.0.0",
                sdkVersion = "1",
                entryPoint = "index.js",
                icon,
                capabilities = new[] { new { kind = "Metadata", hooks = new[] { "getTrack" }, accountScopes = Array.Empty<string>(), accountRequired = false } },
                permissions = Array.Empty<object>()
            })
        };
    }

    private string Write(string relative, byte[] bytes)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}

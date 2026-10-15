using allstarr.Core.Configuration;
using Microsoft.Extensions.Hosting;
using Moq;

namespace allstarr.Tests;

public sealed class MountedLegacyEnvironmentTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "allstarr-mounted-env", Guid.NewGuid().ToString("N"));
    private string PathToFile => Path.Combine(root, ".env");
    private MountedLegacyEnvironment Source() => new(Mock.Of<IHostEnvironment>(environment =>
        environment.EnvironmentName == "Development" && environment.ContentRootPath == Path.Combine(root, "app")));

    public MountedLegacyEnvironmentTests() => Directory.CreateDirectory(Path.Combine(root, "app"));

    [Fact]
    public async Task Read_IsBoundedAndPreservesTheMountedInput()
    {
        const string value = "CACHE_LYRICS_DAYS=30\nDEEZER_ARL=private-fixture";
        await File.WriteAllTextAsync(PathToFile, value);
        var modified = File.GetLastWriteTimeUtc(PathToFile);
        Assert.True(Source().IsAvailable);
        var bytes = await Source().ReadAsync();
        Assert.Equal(value, System.Text.Encoding.UTF8.GetString(bytes));
        Assert.Equal(value, await File.ReadAllTextAsync(PathToFile));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(PathToFile));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(LegacyEnvParser.MaxBytes + 1)]
    public async Task Read_RejectsEmptyAndOversizedInputs(int length)
    {
        await File.WriteAllBytesAsync(PathToFile, new byte[length]);
        Assert.False(Source().IsAvailable);
        var error = await Assert.ThrowsAsync<LegacyEnvMigrationException>(() => Source().ReadAsync());
        Assert.Equal("mounted_env_unavailable", error.Code);
        Assert.Equal(length, new FileInfo(PathToFile).Length);
    }

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task Read_NeverFollowsAFileOrAncestorLink()
    {
        if (OperatingSystem.IsWindows()) return;
        await File.WriteAllTextAsync(Path.Combine(root, "original"), "private-fixture");
        File.CreateSymbolicLink(PathToFile, Path.Combine(root, "original"));
        Assert.False(Source().IsAvailable);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Source().ReadAsync());
        File.Delete(PathToFile);
        await File.WriteAllTextAsync(PathToFile, "private-fixture");
        Directory.Move(root, root + ".original");
        Directory.CreateSymbolicLink(root, root + ".original");
        Assert.False(Source().IsAvailable);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Source().ReadAsync());
        Assert.Equal("private-fixture", await File.ReadAllTextAsync(Path.Combine(root + ".original", ".env")));
    }

    public void Dispose()
    {
        Directory.Delete(root, true);
        if (Directory.Exists(root + ".original")) Directory.Delete(root + ".original", true);
    }
}

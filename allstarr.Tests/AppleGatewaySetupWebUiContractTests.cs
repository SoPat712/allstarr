namespace allstarr.Tests;

public sealed class AppleGatewaySetupWebUiContractTests
{
    private readonly string _script = File.ReadAllText(
        FindRepositoryFile("allstarr", "wwwroot", "js", "webui.js"));
    private readonly string _css = File.ReadAllText(
        FindRepositoryFile("allstarr", "wwwroot", "css", "base.css"));

    [Fact]
    public void Setup_UsesTruthfulPackageGatewayAndSessionStages()
    {
        Assert.Contains("apple-setup-progress", _script, StringComparison.Ordinal);
        Assert.Contains("label: \"Package\"", _script, StringComparison.Ordinal);
        Assert.Contains("label: \"Gateway\"", _script, StringComparison.Ordinal);
        Assert.Contains("label: \"Session\"", _script, StringComparison.Ordinal);
        Assert.Contains("status.daemon_running && status.wrapper_healthy", _script, StringComparison.Ordinal);
        Assert.Contains("Boolean(status.logged_in)", _script, StringComparison.Ordinal);
        Assert.DoesNotContain("upload progress", _script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PackagePicker_ShowsSelectionAndKeepsHostActionCompact()
    {
        Assert.Contains("@change=${this.selectApplePackage}", _script, StringComparison.Ordinal);
        Assert.Contains("this.applePackageFileSize", _script, StringComparison.Ordinal);
        Assert.Contains("this.appleUploadProgress", _script, StringComparison.Ordinal);
        Assert.Contains("role=\"progressbar\"", _script, StringComparison.Ordinal);
        Assert.Contains("APK or APKM, up to 512 MB", _script, StringComparison.Ordinal);
        Assert.Contains("./allstarr.sh install-apple</code>", _script, StringComparison.Ordinal);
        Assert.DoesNotContain("./allstarr.sh install-apple x86_64", _script, StringComparison.Ordinal);
        Assert.Contains("Architecture detected on the Docker host", _script, StringComparison.Ordinal);
        Assert.Contains("Install command copied", _script, StringComparison.Ordinal);
        Assert.Contains(".apple-package-form {", _css, StringComparison.Ordinal);
        Assert.Contains(".apple-upload-progress {", _css, StringComparison.Ordinal);
        Assert.Contains(".apple-host-action {", _css, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 760px)", _css, StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "allstarr.sln")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root."), Path.Combine(parts));
    }
}

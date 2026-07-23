namespace allstarr.Tests;

public sealed class SignalBootWebUiContractTests
{
    private readonly string _script = ReadRepositoryFile("allstarr", "wwwroot", "js", "webui.js");
    private readonly string _styles = ReadRepositoryFile("allstarr", "wwwroot", "css", "shell.css");
    private readonly string _index = ReadRepositoryFile("allstarr", "wwwroot", "index.html");

    [Fact]
    public void SignalBootAppearsBeforeAndDuringRealBootstrap()
    {
        Assert.Contains("<section class=\"signal-boot\"", _index, StringComparison.Ordinal);
        Assert.Contains("return this.renderSignalBoot()", _script, StringComparison.Ordinal);
        Assert.Contains("this.startSignalBoot()", _script, StringComparison.Ordinal);
        Assert.Contains("this.stopSignalBoot()", _script, StringComparison.Ordinal);
        Assert.Contains("BOOT_MESSAGES[this.bootMessageIndex]", _script, StringComparison.Ordinal);
    }

    [Fact]
    public void SignalBootHasQuirkyMusicTechCopyWithoutFakeProgress()
    {
        Assert.Contains("Tuning provider routes", _script, StringComparison.Ordinal);
        Assert.Contains("Aligning playlist constellations", _script, StringComparison.Ordinal);
        Assert.Contains("Negotiating with the aux cable", _script, StringComparison.Ordinal);
        Assert.DoesNotContain("signal-boot-meter\" role=\"progressbar\"", _script, StringComparison.Ordinal);
        Assert.DoesNotContain("signal-boot-meter\" role=\"progressbar\"", _index, StringComparison.Ordinal);
        Assert.DoesNotContain("% complete", _script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SignalBootRespectsReducedMotionAndHasStableAccessibleStatus()
    {
        Assert.Contains("@media (prefers-reduced-motion: reduce)", _styles, StringComparison.Ordinal);
        Assert.Contains(".signal-boot-meter span", _styles, StringComparison.Ordinal);
        Assert.Contains("class=\"signal-boot-accessible\" role=\"status\"", _index, StringComparison.Ordinal);
        Assert.Contains("aria-hidden=\"true\">${BOOT_MESSAGES", _script, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(params string[] segments)
    {
        var relativePath = Path.Combine(segments);
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", relativePath));
        return File.ReadAllText(path);
    }
}

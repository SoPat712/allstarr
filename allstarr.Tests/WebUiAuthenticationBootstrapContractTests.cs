namespace allstarr.Tests;

public class WebUiAuthenticationBootstrapContractTests
{
    private readonly string _script = File.ReadAllText(Path.Combine(
        FindRepositoryRoot(),
        "allstarr",
        "wwwroot",
        "js",
        "webui.js"));

    [Fact]
    public void ConfirmedSessionSurvivesAuxiliaryBootstrapFailures()
    {
        Assert.Contains("this.authenticated = true;", _script, StringComparison.Ordinal);
        Assert.Contains("Promise.allSettled([", _script, StringComparison.Ordinal);
        Assert.Contains("configResult.status === \"rejected\"", _script, StringComparison.Ordinal);
        Assert.Contains("statusResult.status === \"rejected\"", _script, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "catch (error) {\n      this.authenticated = false;\n      this.session = null;",
            _script,
            StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "allstarr.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}

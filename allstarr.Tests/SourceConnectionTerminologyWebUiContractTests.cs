namespace allstarr.Tests;

public sealed class SourceConnectionTerminologyWebUiContractTests
{
    private readonly string _script = File.ReadAllText(
        FindRepositoryFile("allstarr", "wwwroot", "js", "webui.js"));

    [Fact]
    public void Accounts_ArePresentedAsConnectionsToSources()
    {
        Assert.Contains("Source connections", _script, StringComparison.Ordinal);
        Assert.Contains("Connect a source account", _script, StringComparison.Ordinal);
        Assert.Contains("<label>Source</label>", _script, StringComparison.Ordinal);
        Assert.Contains("sourceDisplayName", _script, StringComparison.Ordinal);
        Assert.Contains("Source connection", _script, StringComparison.Ordinal);
        Assert.DoesNotContain("<h3>Connected accounts</h3>", _script, StringComparison.Ordinal);
        Assert.Contains("Source connections moved to Sources", _script, StringComparison.Ordinal);
        Assert.Contains("this.openProviderAccountModal(providerId)", _script, StringComparison.Ordinal);
        Assert.Contains("sourceAccounts.map((account) => this.renderProviderAccountCard", _script, StringComparison.Ordinal);
        Assert.Contains("Credentials, audience, tests, CTS, and lifecycle controls", _script, StringComparison.Ordinal);
        Assert.DoesNotContain("#/settings/accounts", _script, StringComparison.Ordinal);
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

namespace allstarr.Tests;

public sealed class SettingsNavigationWebUiContractTests
{
    private readonly string _script = File.ReadAllText(
        FindRepositoryFile("allstarr", "wwwroot", "js", "webui.js"));

    [Fact]
    public void Settings_UsesOneFourTabAdministratorWorkspace()
    {
        Assert.Contains(
            "const allowedTabs = [\"general\", \"routing\", \"extensions\", \"maintenance\"]",
            _script,
            StringComparison.Ordinal);
        Assert.Contains(
            "[[\"general\", \"General\", \"settings\"], [\"routing\", \"Source priority\", \"sources\"], [\"extensions\", \"Extensions\", \"extensions\"], [\"maintenance\", \"Maintenance\", \"tasks\"]]",
            _script,
            StringComparison.Ordinal);
        Assert.DoesNotContain("[\"accounts\", \"Accounts\", \"user\"]", _script, StringComparison.Ordinal);
        Assert.Contains("sub === \"extensions\" ? this.renderExtensions()", _script, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Settings sections\"", _script, StringComparison.Ordinal);
        Assert.DoesNotContain(">Manage extensions<", _script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Settings_DirectsNonAdministratorsToSourcesForConnections()
    {
        Assert.Contains("Source connections moved to Sources", _script, StringComparison.Ordinal);
        Assert.Contains("this.navigate(\"/sources\")", _script, StringComparison.Ordinal);
        Assert.DoesNotContain("Manage your connected provider accounts.", _script, StringComparison.Ordinal);
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

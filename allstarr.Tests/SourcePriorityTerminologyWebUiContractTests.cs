namespace allstarr.Tests;

public sealed class SourcePriorityTerminologyWebUiContractTests
{
    private readonly string _script = File.ReadAllText(
        FindRepositoryFile("allstarr", "wwwroot", "js", "webui.js"));

    [Fact]
    public void Settings_UsesOneUserFacingSourcePriorityName()
    {
        Assert.True(CountOccurrences(_script, "Source priority") >= 3);
        Assert.DoesNotContain(">Provider priority<", _script, StringComparison.Ordinal);
        Assert.DoesNotContain(">Provider routing<", _script, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Provider routing\"", _script, StringComparison.Ordinal);
        Assert.Contains("[\"routing\", \"Source priority\", \"sources\"]", _script, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string value, string fragment)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(fragment, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += fragment.Length;
        }

        return count;
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

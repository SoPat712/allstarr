namespace allstarr.Tests;

public sealed class OperationalLogRegressionContractTests
{
    [Fact]
    public void AdminSessions_UsePostgreSqlWithoutProcessOrFileAuthority()
    {
        var service = File.ReadAllText(FindRepositoryFile(
            "allstarr", "Services", "Admin", "AdminAuthSessionService.cs"));
        var context = File.ReadAllText(FindRepositoryFile(
            "allstarr", "Core", "Storage", "AllstarrDbContext.cs"));

        Assert.Contains("EfAdminAuthSessionStore", service, StringComparison.Ordinal);
        Assert.Contains("AdminAuthSessions", context, StringComparison.Ordinal);
        Assert.DoesNotContain("sessions.protected", service, StringComparison.Ordinal);
        Assert.DoesNotContain("ConcurrentDictionary", service, StringComparison.Ordinal);
        Assert.DoesNotContain("File.", service, StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(params string[] parts)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine([current.FullName, .. parts]);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }

        throw new FileNotFoundException($"Could not find repository file: {Path.Combine(parts)}");
    }
}

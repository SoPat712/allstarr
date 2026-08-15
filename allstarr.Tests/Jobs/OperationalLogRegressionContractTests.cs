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

    [Fact]
    public void EndpointUsage_UsesRetentionBoundedAuditEventsWithoutCsvFiles()
    {
        var helper = File.ReadAllText(FindRepositoryFile(
            "allstarr", "Controllers", "Helpers.cs"));
        var diagnostics = File.ReadAllText(FindRepositoryFile(
            "allstarr", "Controllers", "DiagnosticsController.cs"));
        var audit = File.ReadAllText(FindRepositoryFile(
            "allstarr", "Core", "Operations", "EndpointUsageAudit.cs"));

        Assert.Contains("EndpointUsageAudit", helper, StringComparison.Ordinal);
        Assert.Contains("AuditEvents", audit, StringComparison.Ordinal);
        Assert.DoesNotContain("AppendAllText", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadAllLines", diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain("endpoints.csv", helper + diagnostics + audit, StringComparison.Ordinal);
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

namespace allstarr.Tests;

public sealed class StorageOperationsRunbookTests
{
    [Fact]
    public void Runbook_PreservesMediaAndRecoveryBoundaries()
    {
        var runbook = File.ReadAllText(FindRepositoryFile("docs", "operations", "storage.md"));

        Assert.Contains("Postgres does not store song audio", runbook, StringComparison.Ordinal);
        Assert.Contains("/app/downloads", runbook, StringComparison.Ordinal);
        Assert.Contains("/app/kept", runbook, StringComparison.Ordinal);
        Assert.Contains("docker compose down --volumes --remove-orphans", runbook, StringComparison.Ordinal);
        Assert.Contains("Never run `pg_restore --clean` against the live database", runbook, StringComparison.Ordinal);
        Assert.Contains("SecretKeyMaterialIncluded: false", runbook, StringComparison.Ordinal);
        Assert.Contains("schema_version_unsupported", runbook, StringComparison.Ordinal);
        Assert.Contains("create-new-allstarr-database", runbook, StringComparison.Ordinal);
        Assert.Contains("sqlite_database_missing", runbook, StringComparison.Ordinal);
        Assert.Contains("durable jobs, and outbox delivery pause", runbook, StringComparison.Ordinal);
        Assert.Contains("reports `verified` only after that check", runbook, StringComparison.Ordinal);
        Assert.Contains("storage restore-sqlite", runbook, StringComparison.Ordinal);
        Assert.Contains("storage export", runbook, StringComparison.Ordinal);
        Assert.Contains("storage import", runbook, StringComparison.Ordinal);
        Assert.DoesNotContain("no supported operator command", runbook, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryFile(params string[] path)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "allstarr.sln")))
        {
            current = current.Parent;
        }

        if (current == null)
        {
            throw new DirectoryNotFoundException("Could not locate allstarr.sln");
        }

        return Path.Combine([current.FullName, .. path]);
    }
}

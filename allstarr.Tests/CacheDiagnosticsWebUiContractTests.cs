namespace allstarr.Tests;

public sealed class CacheDiagnosticsWebUiContractTests
{
    private readonly string _script = File.ReadAllText(
        FindRepositoryFile("allstarr", "wwwroot", "js", "webui.js"));
    private readonly string _css = File.ReadAllText(
        FindRepositoryFile("allstarr", "wwwroot", "css", "design-system.css"));

    [Fact]
    public void Settings_ShowsAllCacheTiersAndUsesOnlyScopedPurgeApi()
    {
        var maintenanceStart = _script.IndexOf("renderSettingsMaintenance()", StringComparison.Ordinal);
        var maintenanceEnd = _script.IndexOf("canOfferEnvMigration()", maintenanceStart, StringComparison.Ordinal);
        var maintenance = _script[maintenanceStart..maintenanceEnd];

        Assert.Contains("PostgreSQL metadata", _script, StringComparison.Ordinal);
        Assert.Contains("RAM hot tier", _script, StringComparison.Ordinal);
        Assert.Contains("Disk media", _script, StringComparison.Ordinal);
        Assert.Contains("API.purgeCache(scope)", _script, StringComparison.Ordinal);
        Assert.Contains("purgeCacheScope(\"all\")", _script, StringComparison.Ordinal);
        Assert.Contains("% hit rate · ${writes} writes · ${evictions} evictions", _script, StringComparison.Ordinal);
        Assert.DoesNotContain(">Clear cache<", maintenance, StringComparison.Ordinal);
    }

    [Fact]
    public void UsageCards_AreResponsiveAndExposeProgressSemantics()
    {
        Assert.Contains("class=\"cache-usage-meter\" role=\"progressbar\"", _script, StringComparison.Ordinal);
        Assert.Contains(".cache-tier-grid {", _css, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: repeat(3, minmax(0, 1fr));", _css, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: 1fr;", _css, StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }
}

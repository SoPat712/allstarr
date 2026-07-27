namespace allstarr.Tests;

public sealed class ProviderRouteIdentityContractTests
{
    [Fact]
    public void AdminSourceRoutes_AreStableAndOriginQualified()
    {
        var controller = File.ReadAllText(FindRepositoryFile(
            "allstarr",
            "Controllers",
            "AdminUiController.cs"));

        Assert.Contains("provider.ImplementationOrigin ??= \"built_in\";", controller, StringComparison.Ordinal);
        Assert.Contains("provider.RouteId ??= $\"builtin:{provider.Id}\";", controller, StringComparison.Ordinal);
        Assert.Contains("RouteId = $\"{item.Origin.ToString().ToLowerInvariant()}:{item.Id}\"", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("RouteId = item.Id,", controller, StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(params string[] path)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine([current.FullName, .. path]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException($"Could not locate {Path.Combine(path)}");
    }
}

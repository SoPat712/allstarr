using allstarr.Services.Common;
using Microsoft.Extensions.Configuration;

namespace allstarr.Tests;

public sealed class EndpointUsagePathResolverTests
{
    [Fact]
    public void GetLogFile_UsesConfiguredDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "allstarr-tests", Guid.NewGuid().ToString("N"));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Diagnostics:EndpointUsageDirectory"] = root
            })
            .Build();

        var result = EndpointUsagePathResolver.GetLogFile(configuration);

        Assert.Equal(Path.Combine(Path.GetFullPath(root), "endpoints.csv"), result);
    }

    [Fact]
    public void GetLogFile_UsesContainerDefaultWhenUnset()
    {
        var configuration = new ConfigurationBuilder().Build();

        Assert.Equal(
            "/app/cache/endpoint-usage/endpoints.csv",
            EndpointUsagePathResolver.GetLogFile(configuration));
    }
}

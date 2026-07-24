using System.Reflection;
using allstarr.Services.Common;

namespace allstarr.Tests;

public sealed class ApplicationCacheContractTests
{
    [Fact]
    public void ProductionFacade_ImplementsApplicationCacheContract()
    {
        Assert.True(typeof(IApplicationCache).IsAssignableFrom(typeof(BoundedHotApplicationCache)));
    }

    [Fact]
    public void ApplicationCacheContract_ContainsOnlyDisposableCacheOperations()
    {
        var methodNames = typeof(IApplicationCache)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal)
            {
                "get_IsEnabled",
                nameof(IApplicationCache.GetStringAsync),
                nameof(IApplicationCache.GetAsync),
                nameof(IApplicationCache.SetStringAsync),
                nameof(IApplicationCache.SetAsync),
                nameof(IApplicationCache.DeleteAsync),
                nameof(IApplicationCache.ExistsAsync),
                nameof(IApplicationCache.GetKeysByPattern),
                nameof(IApplicationCache.DeleteByPatternAsync),
            },
            methodNames);
    }
}
